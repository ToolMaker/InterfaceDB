using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

public sealed partial class ResilientRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public async ValueTask<ResilientSynchronizationResult> SynchronizeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (CompositionOptions is not null) return await SynchronizeCompositionAsync(cancellationToken).ConfigureAwait(false);
        var accumulator = new SynchronizationAccumulator();
        var preferredCandidates = await ReadEndpointListAsync<TContract>(
                _preferred,
                ResilientEndpoint.Preferred,
                cancellationToken)
            .ConfigureAwait(false);
        if (preferredCandidates.Failure is not null)
        {
            accumulator.AddFailure(new ResilientRepositoryFailure(
                ResilientEndpoint.Preferred,
                ResilientRepositoryOperation.Synchronize,
                StorageKey: null,
                preferredCandidates.Failure,
                IsTransient: true));
            return accumulator.ToResult();
        }

        var synchronizationState = await _synchronizationStateStore
            .GetAsync(_policy.DatasetId, cancellationToken)
            .ConfigureAwait(false);
        var storageKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in preferredCandidates.Items)
        {
            if (ReadMetadata(candidate).HasPendingMutation)
            {
                storageKeys.Add(GetRequiredStorageKey(candidate));
            }
        }

        foreach (var dirtyStorageKey in synchronizationState.DirtyStorageKeys.Keys)
        {
            storageKeys.Add(dirtyStorageKey);
        }

        var revisionCheckMode = GetRevisionCheckMode(storageKey: null);
        var compareDataset = synchronizationState.IsDatasetDirty
            || revisionCheckMode == RevisionCheckMode.AlwaysCompare;
        if (compareDataset)
        {
            foreach (var candidate in preferredCandidates.Items)
            {
                storageKeys.Add(GetRequiredStorageKey(candidate));
            }

            var peerCandidates = await ReadEndpointListAsync<TContract>(
                    _peer,
                    ResilientEndpoint.Peer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (peerCandidates.Failure is not null)
            {
                accumulator.AddFailure(new ResilientRepositoryFailure(
                    ResilientEndpoint.Peer,
                    ResilientRepositoryOperation.Synchronize,
                    StorageKey: null,
                    peerCandidates.Failure,
                    IsTransient: true));
                // A failed peer enumeration already proves that this comparison pass cannot be
                // complete. Do not amplify one outage into a keyed peer request for every local
                // record; leave the dataset/pending markers intact for the next retry.
                return accumulator.ToResult();
            }
            else
            {
                foreach (var candidate in peerCandidates.Items)
                {
                    storageKeys.Add(GetRequiredStorageKey(candidate));
                }
            }
        }

        foreach (var storageKey in storageKeys)
        {
            synchronizationState.DirtyStorageKeys.TryGetValue(storageKey, out var dirtyKey);
            var result = await SynchronizeStorageKeyCoreWithGateAsync(
                    storageKey,
                    forcePeerRead: true,
                    dirtyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            accumulator.Add(result);
            if (result.Failures.Any(failure =>
                    failure.IsTransient && IsEndpointWideFailure(failure.Exception)))
            {
                // Pending markers are durable. One unavailable peer is enough to stop this pass;
                // continuing would amplify an outage into one timeout per queued entity.
                break;
            }
        }

        if (synchronizationState.DatasetDirtyThroughCheckpoint is { } throughCheckpoint
            && accumulator.Failures.Count == 0
            && accumulator.Conflicts == 0)
        {
            await _synchronizationStateStore.MarkDatasetSynchronizedAsync(
                    _policy.DatasetId,
                    throughCheckpoint,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return accumulator.ToResult();
    }

    public async ValueTask<ResilientSynchronizationResult> SynchronizeAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateStorageKey(storageKey);
        if (CompositionOptions is not null) return await SynchronizeCompositionAsync(cancellationToken).ConfigureAwait(false);
        var synchronizationState = await _synchronizationStateStore
            .GetAsync(_policy.DatasetId, cancellationToken)
            .ConfigureAwait(false);
        synchronizationState.DirtyStorageKeys.TryGetValue(storageKey, out var dirtyKey);
        return await SynchronizeStorageKeyCoreWithGateAsync(
                storageKey,
                forcePeerRead: true,
                dirtyKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ResilientSynchronizationResult> SynchronizeStorageKeyCoreWithGateAsync(
        string storageKey,
        bool forcePeerRead,
        ResilientDirtyKey? dirtyKey,
        CancellationToken cancellationToken)
    {
        var synchronizationGate = GetSynchronizationGate(storageKey);
        await synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SynchronizeStorageKeyCoreAsync(
                    storageKey,
                    forcePeerRead,
                    dirtyKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            synchronizationGate.Release();
        }
    }

    private async ValueTask<ResilientSynchronizationResult> SynchronizeStorageKeyCoreAsync(
        string storageKey,
        bool forcePeerRead,
        ResilientDirtyKey? dirtyKey,
        CancellationToken cancellationToken)
    {
        var accumulator = new SynchronizationAccumulator();
        EndpointReadResult preferredRead;
        ResilientEnvelopeMetadata preferredMetadata;
        var preferredGate = GetKeyGate(storageKey);
        await preferredGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            preferredRead = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            preferredMetadata = ReadMetadata(preferredRead.Entity);
            if (preferredRead.Entity is not null)
            {
                preferredRead = preferredRead with
                {
                    Entity = CreateDetachedCopy(preferredRead.Entity, storageKey)
                };
            }
        }
        finally
        {
            preferredGate.Release();
        }

        if (preferredRead.State == EndpointReadState.Unavailable)
        {
            accumulator.AddFailure(new ResilientRepositoryFailure(
                ResilientEndpoint.Preferred,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                preferredRead.Failure!,
                IsTransient: true));
            return accumulator.ToResult();
        }

        if (!forcePeerRead && !preferredMetadata.HasPendingMutation)
        {
            return accumulator.ToResult();
        }

        var peerRead = await ReadEndpointByStorageKeyAsync(
                _peer,
                ResilientEndpoint.Peer,
                storageKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (peerRead.State == EndpointReadState.Unavailable)
        {
            accumulator.AddFailure(new ResilientRepositoryFailure(
                ResilientEndpoint.Peer,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                peerRead.Failure!,
                IsTransient: true));
            return accumulator.ToResult();
        }

        var peerMetadata = ReadMetadata(peerRead.Entity);
        if (preferredRead.State == EndpointReadState.Found && preferredMetadata.HasPendingMutation)
        {
            accumulator.PendingMutationsExamined++;

            if (peerRead.State == EndpointReadState.Found
                && peerMetadata.LastAppliedMutationId == preferredMetadata.PendingMutationId)
            {
                if (peerMetadata.RevisionTimestampUtcMicroseconds
                        == preferredMetadata.RevisionTimestampUtcMicroseconds
                    && peerMetadata.IsTombstone != preferredMetadata.IsTombstone)
                {
                    accumulator.Conflicts++;
                    ReportFailure(
                        ResilientEndpoint.Peer,
                        ResilientRepositoryOperation.Synchronize,
                        storageKey,
                        new InvalidDataException(
                            $"Peer '{storageKey}' reports the pending operation at the same revision with conflicting tombstone state."));
                }
                else if (peerMetadata.RevisionTimestampUtcMicroseconds
                    < preferredMetadata.RevisionTimestampUtcMicroseconds)
                {
                    accumulator.Conflicts++;
                    ReportFailure(
                        ResilientEndpoint.Peer,
                        ResilientRepositoryOperation.Synchronize,
                        storageKey,
                        new InvalidDataException(
                            $"Peer '{storageKey}' reports the pending operation as applied at an older revision."));
                }
                else
                {
                    await AcknowledgePendingMutationAsync(
                            storageKey,
                            preferredMetadata.PendingMutationId,
                            accumulator,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (peerMetadata.RevisionTimestampUtcMicroseconds
                        > preferredMetadata.RevisionTimestampUtcMicroseconds)
                    {
                        if (GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly)
                        {
                            await ApplyPeerRevisionToPreferredAsync(
                                    peerRead.Entity!,
                                    peerMetadata,
                                    storageKey,
                                    accumulator,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            accumulator.Conflicts++;
                            var conflict = new ResilientRevisionConflictException(
                                storageKey,
                                preferredMetadata.RevisionTimestampUtcMicroseconds,
                                peerMetadata.RevisionTimestampUtcMicroseconds);
                            ReportFailure(
                                ResilientEndpoint.Peer,
                                ResilientRepositoryOperation.Synchronize,
                                storageKey,
                                conflict);
                        }
                    }
                }
            }
            else if (GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly)
            {
                accumulator.Conflicts++;
                var conflict = new InvalidOperationException(
                    $"Storage key '{storageKey}' has a durable preferred mutation but is now peer-authoritative read-only.");
                ReportFailure(
                    ResilientEndpoint.Peer,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    conflict);

                if (peerRead.State == EndpointReadState.Found
                    && peerMetadata.RevisionTimestampUtcMicroseconds
                        > preferredMetadata.RevisionTimestampUtcMicroseconds)
                {
                    await ApplyPeerRevisionToPreferredAsync(
                            peerRead.Entity!,
                            peerMetadata,
                            storageKey,
                            accumulator,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (peerRead.State == EndpointReadState.Found
                     && peerMetadata.RevisionTimestampUtcMicroseconds
                        > preferredMetadata.RevisionTimestampUtcMicroseconds)
            {
                accumulator.Conflicts++;
                var conflict = new ResilientRevisionConflictException(
                    storageKey,
                    preferredMetadata.RevisionTimestampUtcMicroseconds,
                    peerMetadata.RevisionTimestampUtcMicroseconds);
                ReportFailure(
                    ResilientEndpoint.Peer,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    conflict);
            }
            else
            {
                var peerCommitted = await CommitPendingMutationToPeerAsync(
                        preferredRead.Entity!,
                        preferredMetadata,
                        peerRead.Entity,
                        storageKey,
                        accumulator,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (peerCommitted)
                {
                    await AcknowledgePendingMutationAsync(
                            storageKey,
                            preferredMetadata.PendingMutationId,
                            accumulator,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        else if (peerRead.State == EndpointReadState.Found
                 && preferredRead.State != EndpointReadState.Found)
        {
            await ApplyPeerRevisionToPreferredAsync(
                    peerRead.Entity!,
                    peerMetadata,
                    storageKey,
                    accumulator,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (peerRead.State == EndpointReadState.Found
                 && preferredRead.State == EndpointReadState.Found
                 && peerMetadata.RevisionTimestampUtcMicroseconds
                    > preferredMetadata.RevisionTimestampUtcMicroseconds)
        {
            if (GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly)
            {
                await ApplyPeerRevisionToPreferredAsync(
                        peerRead.Entity!,
                        peerMetadata,
                        storageKey,
                        accumulator,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                accumulator.Conflicts++;
                var conflict = new ResilientRevisionConflictException(
                    storageKey,
                    preferredMetadata.RevisionTimestampUtcMicroseconds,
                    peerMetadata.RevisionTimestampUtcMicroseconds);
                ReportFailure(
                    ResilientEndpoint.Peer,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    conflict);
            }
        }
        else if (peerRead.State == EndpointReadState.Found
                 && preferredRead.State == EndpointReadState.Found
                 && peerMetadata.RevisionTimestampUtcMicroseconds
                    == preferredMetadata.RevisionTimestampUtcMicroseconds
                 && peerMetadata.IsTombstone != preferredMetadata.IsTombstone)
        {
            accumulator.Conflicts++;
            ReportFailure(
                ResilientEndpoint.Peer,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                new InvalidDataException(
                    $"Storage key '{storageKey}' has the same revision timestamp with conflicting tombstone state."));
        }
        else if (preferredRead.State == EndpointReadState.Found
                 && GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly
                 && (peerRead.State == EndpointReadState.Missing
                     || preferredMetadata.RevisionTimestampUtcMicroseconds
                        > peerMetadata.RevisionTimestampUtcMicroseconds))
        {
            accumulator.Conflicts++;
            ReportPeerAuthorityInvariant(
                storageKey,
                preferredMetadata,
                peerRead.State,
                peerMetadata,
                ResilientRepositoryOperation.Synchronize);
        }
        else if (preferredRead.State == EndpointReadState.Found
                 && ShouldRepairPeer(storageKey, preferredMetadata, peerRead.State, peerMetadata))
        {
            await PersistAndCommitRepairMutationAsync(
                    preferredMetadata,
                    peerRead.Entity,
                    storageKey,
                    accumulator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (dirtyKey is not null && accumulator.Failures.Count == 0 && accumulator.Conflicts == 0)
        {
            await _synchronizationStateStore.MarkStorageKeySynchronizedAsync(
                    _policy.DatasetId,
                    storageKey,
                    dirtyKey.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return accumulator.ToResult();
    }

    private bool ShouldRepairPeer(
        string storageKey,
        ResilientEnvelopeMetadata preferredMetadata,
        EndpointReadState peerState,
        ResilientEnvelopeMetadata peerMetadata)
    {
        if (GetMutationAuthority(storageKey) != MutationAuthority.PreferredWritable)
        {
            return false;
        }

        return peerState == EndpointReadState.Missing
            || preferredMetadata.RevisionTimestampUtcMicroseconds
                > peerMetadata.RevisionTimestampUtcMicroseconds;
    }

    private async ValueTask<bool> CommitPendingMutationToPeerAsync(
        TContract preferredEntity,
        ResilientEnvelopeMetadata preferredMetadata,
        TContract? peerEntity,
        string storageKey,
        SynchronizationAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        // Stage on a detached instance. Mutating a peer-owned cached object before the durable
        // save could make a failed write look acknowledged on retry.
        var candidate = PrepareStagedCopy(preferredEntity, peerEntity, storageKey);
        var committedMetadata = preferredMetadata with
        {
            PendingMutationId = Guid.Empty,
            PendingMutationKind = PendingMutationKind.None,
            LastAppliedMutationId = preferredMetadata.PendingMutationId
        };

        try
        {
            await SaveWithMetadataAsync(
                    _peerMetadataRepository,
                    candidate,
                    GetExistingEnvelopeCacheKey(peerEntity, ResilientEndpoint.Peer, storageKey),
                    committedMetadata,
                    ResilientEndpoint.Peer,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    cancellationToken,
                    new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(
                        peerEntity?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                        Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.Import,
                        preferredMetadata.RevisionTimestampUtcMicroseconds))
                .ConfigureAwait(false);
            accumulator.PeerCommits++;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new ResilientRepositoryFailure(
                ResilientEndpoint.Peer,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                exception,
                IsTransient: IsUnavailable(exception));
            accumulator.AddFailure(failure);
            ReportFailure(failure.Endpoint, failure.Operation, failure.StorageKey, failure.Exception);
            return false;
        }
    }

    private async ValueTask AcknowledgePendingMutationAsync(
        string storageKey,
        Guid pendingMutationId,
        SynchronizationAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var preferredGate = GetKeyGate(storageKey);
        await preferredGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.State != EndpointReadState.Found)
            {
                var exception = current.Failure
                    ?? new InvalidOperationException(
                        $"Preferred entity '{storageKey}' disappeared before its synchronization acknowledgement.");
                var failure = new ResilientRepositoryFailure(
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    exception,
                    IsTransient: current.Failure is not null);
                accumulator.AddFailure(failure);
                ReportFailure(failure.Endpoint, failure.Operation, failure.StorageKey, failure.Exception);
                return;
            }

            var currentMetadata = ReadMetadata(current.Entity);
            if (currentMetadata.PendingMutationId != pendingMutationId)
            {
                return;
            }

            var acknowledgedMetadata = currentMetadata with
            {
                PendingMutationId = Guid.Empty,
                PendingMutationKind = PendingMutationKind.None
            };
            try
            {
                await SaveWithMetadataAsync(
                        _preferredMetadataRepository,
                        CreateDetachedCopy(current.Entity!, storageKey),
                        GetExistingEnvelopeCacheKey(
                            current.Entity,
                            ResilientEndpoint.Preferred,
                            storageKey),
                        acknowledgedMetadata,
                        ResilientEndpoint.Preferred,
                        ResilientRepositoryOperation.Synchronize,
                        storageKey,
                        cancellationToken,
                        new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(
                            current.Entity!.Envelope!.RevisionTimestampUtcMicroseconds,
                            Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.MetadataOnly,
                            ExpectedExtensionMetadata: current.Entity.Envelope.ExtensionMetadata))
                    .ConfigureAwait(false);
                accumulator.Acknowledgements++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failure = new ResilientRepositoryFailure(
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    exception,
                    IsTransient: IsUnavailable(exception));
                accumulator.AddFailure(failure);
                ReportFailure(failure.Endpoint, failure.Operation, failure.StorageKey, failure.Exception);
            }
        }
        finally
        {
            preferredGate.Release();
        }
    }

    private async ValueTask ApplyPeerRevisionToPreferredAsync(
        TContract peerEntity,
        ResilientEnvelopeMetadata peerMetadata,
        string storageKey,
        SynchronizationAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var preferredGate = GetKeyGate(storageKey);
        await preferredGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.State == EndpointReadState.Unavailable)
            {
                var unavailableFailure = new ResilientRepositoryFailure(
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    current.Failure!,
                    IsTransient: true);
                accumulator.AddFailure(unavailableFailure);
                return;
            }

            if (current.State == EndpointReadState.Found)
            {
                var currentMetadata = ReadMetadata(current.Entity);
                if (currentMetadata.RevisionTimestampUtcMicroseconds
                    >= peerMetadata.RevisionTimestampUtcMicroseconds)
                {
                    if (currentMetadata.RevisionTimestampUtcMicroseconds
                            == peerMetadata.RevisionTimestampUtcMicroseconds
                        && currentMetadata.IsTombstone != peerMetadata.IsTombstone)
                    {
                        accumulator.Conflicts++;
                    }

                    return;
                }

                if (GetMutationAuthority(storageKey) == MutationAuthority.PreferredWritable)
                {
                    accumulator.Conflicts++;
                    var conflict = new ResilientRevisionConflictException(
                        storageKey,
                        currentMetadata.RevisionTimestampUtcMicroseconds,
                        peerMetadata.RevisionTimestampUtcMicroseconds);
                    ReportFailure(
                        ResilientEndpoint.Peer,
                        ResilientRepositoryOperation.Synchronize,
                        storageKey,
                        conflict);
                    return;
                }
            }

            // Stage inbound values away from the preferred cache. A provider failure must not
            // publish the peer payload or its LastAppliedMutationId in memory.
            var candidate = PrepareStagedCopy(peerEntity, current.Entity, storageKey);
            var preferredMetadata = peerMetadata with
            {
                PendingMutationId = Guid.Empty,
                PendingMutationKind = PendingMutationKind.None
            };
            await SaveWithMetadataAsync(
                    _preferredMetadataRepository,
                    candidate,
                    GetExistingEnvelopeCacheKey(
                        current.Entity,
                        ResilientEndpoint.Preferred,
                        storageKey),
                    preferredMetadata,
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    cancellationToken,
                    new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(
                        current.Entity?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                        Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.Import,
                        peerMetadata.RevisionTimestampUtcMicroseconds))
                .ConfigureAwait(false);
            accumulator.PreferredRefreshes++;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new ResilientRepositoryFailure(
                ResilientEndpoint.Preferred,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                exception,
                IsTransient: IsUnavailable(exception));
            accumulator.AddFailure(failure);
            ReportFailure(failure.Endpoint, failure.Operation, failure.StorageKey, failure.Exception);
        }
        finally
        {
            preferredGate.Release();
        }
    }

    private async ValueTask PersistAndCommitRepairMutationAsync(
        ResilientEnvelopeMetadata preferredMetadata,
        TContract? peerEntity,
        string storageKey,
        SynchronizationAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        ResilientEnvelopeMetadata? pendingMetadata = null;
        TContract? repairSnapshot = null;
        var preferredGate = GetKeyGate(storageKey);
        await preferredGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.State != EndpointReadState.Found)
            {
                if (current.Failure is not null)
                {
                    accumulator.AddFailure(new ResilientRepositoryFailure(
                        ResilientEndpoint.Preferred,
                        ResilientRepositoryOperation.Synchronize,
                        storageKey,
                        current.Failure,
                        IsTransient: true));
                }

                return;
            }

            var currentMetadata = ReadMetadata(current.Entity);
            if (currentMetadata.HasPendingMutation
                || currentMetadata.RevisionTimestampUtcMicroseconds
                    != preferredMetadata.RevisionTimestampUtcMicroseconds)
            {
                return;
            }

            pendingMetadata = currentMetadata with
            {
                PendingMutationId = Guid.NewGuid(),
                PendingMutationKind = currentMetadata.IsTombstone
                    ? PendingMutationKind.Delete
                    : PendingMutationKind.Upsert
            };
            var canonical = await SaveWithMetadataAsync(
                    _preferredMetadataRepository,
                    CreateDetachedCopy(current.Entity!, storageKey),
                    GetExistingEnvelopeCacheKey(
                        current.Entity,
                        ResilientEndpoint.Preferred,
                        storageKey),
                    pendingMetadata,
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    cancellationToken,
                    new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(
                        current.Entity!.Envelope!.RevisionTimestampUtcMicroseconds,
                        Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.MetadataOnly,
                        ExpectedExtensionMetadata: current.Entity.Envelope.ExtensionMetadata))
                .ConfigureAwait(false);
            repairSnapshot = CreateDetachedCopy(canonical, storageKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new ResilientRepositoryFailure(
                ResilientEndpoint.Preferred,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                exception,
                IsTransient: IsUnavailable(exception));
            accumulator.AddFailure(failure);
            ReportFailure(failure.Endpoint, failure.Operation, failure.StorageKey, failure.Exception);
            return;
        }
        finally
        {
            preferredGate.Release();
        }

        accumulator.PendingMutationsExamined++;
        if (await CommitPendingMutationToPeerAsync(
                repairSnapshot!,
                pendingMetadata!,
                peerEntity,
                storageKey,
                accumulator,
                cancellationToken)
            .ConfigureAwait(false))
        {
            await AcknowledgePendingMutationAsync(
                    storageKey,
                    pendingMetadata!.PendingMutationId,
                    accumulator,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunBackgroundSynchronizationAsync(CancellationToken cancellationToken)
    {
        var retry = new RepositoryRetryBackoff(_policy.BackgroundRetryDelay);
        try
        {
            while (await _synchronizationWakeSignal.Reader
                       .WaitToReadAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                while (_synchronizationWakeSignal.Reader.TryRead(out _))
                {
                }

                var retryNeeded = false;
                if (Interlocked.Exchange(ref _synchronizeAllRequested, 0) != 0)
                {
                    var queuedBeforeScan = _queuedSynchronizationKeys.Keys.ToArray();
                    foreach (var storageKey in queuedBeforeScan)
                    {
                        _queuedSynchronizationKeys.TryRemove(storageKey, out _);
                    }

                    var outcome = await RunBackgroundRequestAsync(
                            storageKey: null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    retryNeeded = outcome.RetryNeeded;
                    if (retryNeeded)
                    {
                        Interlocked.Exchange(ref _synchronizeAllRequested, 1);
                    }
                }
                else
                {
                    var storageKeys = _queuedSynchronizationKeys.Keys
                        .Take(_policy.MaximumPendingSynchronizationKeys)
                        .ToArray();
                    foreach (var storageKey in storageKeys)
                    {
                        _queuedSynchronizationKeys.TryRemove(storageKey, out _);
                        var outcome = await RunBackgroundRequestAsync(storageKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (outcome.RetryNeeded)
                        {
                            retryNeeded = true;
                            QueueSynchronization(storageKey);
                        }

                        if (outcome.StopPass)
                        {
                            // Leave the remaining keys in the coalescing dictionary and retry
                            // after the configured delay instead of producing an outage storm.
                            break;
                        }
                    }
                }

                if (retryNeeded)
                {
                    await Task.Delay(retry.NextDelay(), cancellationToken).ConfigureAwait(false);
                }
                else retry.Reset();

                if (Volatile.Read(ref _synchronizeAllRequested) != 0
                    || !_queuedSynchronizationKeys.IsEmpty)
                {
                    _synchronizationWakeSignal.Writer.TryWrite(0);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<BackgroundRequestOutcome> RunBackgroundRequestAsync(
        string? storageKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            var result = storageKey is null
                ? await SynchronizeAsync(cancellationToken).ConfigureAwait(false)
                : await SynchronizeAsync(storageKey, cancellationToken).ConfigureAwait(false);
            var retryNeeded = result.Failures.Any(static failure => failure.IsTransient);
            var stopPass = result.Failures.Any(failure =>
                failure.IsTransient && IsEndpointWideFailure(failure.Exception));
            return new BackgroundRequestOutcome(retryNeeded, stopPass);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(
                endpoint: null,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                exception);
            var retryNeeded = IsUnavailable(exception);
            return new BackgroundRequestOutcome(
                retryNeeded,
                retryNeeded && IsEndpointWideFailure(exception));
        }
    }

    private sealed class SynchronizationAccumulator
    {
        internal int PendingMutationsExamined { get; set; }

        internal int PeerCommits { get; set; }

        internal int Acknowledgements { get; set; }

        internal int PreferredRefreshes { get; set; }

        internal int Conflicts { get; set; }

        internal List<ResilientRepositoryFailure> Failures { get; } = new();

        internal void Add(ResilientSynchronizationResult result)
        {
            PendingMutationsExamined += result.PendingMutationsExamined;
            PeerCommits += result.PeerCommits;
            Acknowledgements += result.Acknowledgements;
            PreferredRefreshes += result.PreferredRefreshes;
            Conflicts += result.Conflicts;
            Failures.AddRange(result.Failures);
        }

        internal void AddFailure(ResilientRepositoryFailure failure)
            => Failures.Add(failure);

        internal ResilientSynchronizationResult ToResult()
            => new(
                PendingMutationsExamined,
                PeerCommits,
                Acknowledgements,
                PreferredRefreshes,
                Conflicts,
                Failures.ToArray());
    }

    private readonly record struct BackgroundRequestOutcome(
        bool RetryNeeded,
        bool StopPass);
}
