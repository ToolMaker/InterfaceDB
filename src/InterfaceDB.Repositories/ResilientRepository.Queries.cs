using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Commercial.Repositories;

public sealed partial class ResilientRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public async ValueTask<TContract?> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ;

    public async ValueTask<TImplementation?> QueryAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ;

    public IAsyncEnumerable<TContract> QueryManyAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return QueryManyInternalAsync(
            (repository, token) => repository.QueryManyAsync(filterFunction, token),
            filterFunction,
            cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> QueryManyAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return QueryManyInternalAsync(
            (repository, token) => repository.QueryManyAsync(filterFunction, token),
            filterFunction,
            cancellationToken);
    }

    public IAsyncEnumerable<TContract> SearchManyAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return QueryManyInternalAsync(
            (repository, token) => repository.SearchManyAsync(filterExpression, token),
            filterExpression.Compile(),
            cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> SearchManyAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return QueryManyInternalAsync(
            (repository, token) => repository.SearchManyAsync(filterExpression, token),
            filterExpression.Compile(),
            cancellationToken);
    }

    public async IAsyncEnumerable<TContract> QueryManyByStorageKeyAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateStorageKey(storageKey);
        var entity = await QueryFirstByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (entity is not null)
        {
            yield return entity;
        }
    }

    public async ValueTask<TContract?> QueryFirstAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return await QueryFirstInternalAsync(
                (repository, token) => repository.QueryFirstAsync(filterFunction, token),
                token => QueryManyAsync(filterFunction, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<TImplementation?> QueryFirstAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return await QueryFirstInternalAsync(
                (repository, token) => repository.QueryFirstAsync(filterFunction, token),
                token => QueryManyAsync(filterFunction, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<TContract?> SearchFirstAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return await QueryFirstInternalAsync(
                (repository, token) => repository.SearchFirstAsync(filterExpression, token),
                token => SearchManyAsync(filterExpression, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<TImplementation?> SearchFirstAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return await QueryFirstInternalAsync(
                (repository, token) => repository.SearchFirstAsync(filterExpression, token),
                token => SearchManyAsync(filterExpression, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<TContract?> QueryFirstByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateStorageKey(storageKey);
        if (CompositionOptions is not null) return (await ReadCompositionAsync<TContract>(entity => GetRequiredStorageKey(entity) == storageKey, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        var synchronizationState = await _synchronizationStateStore
            .GetAsync(_policy.DatasetId, cancellationToken)
            .ConfigureAwait(false);
        synchronizationState.DirtyStorageKeys.TryGetValue(storageKey, out var dirtyKey);
        var revisionCheckMode = GetRevisionCheckMode(storageKey);
        var forcePeerRead = revisionCheckMode == RevisionCheckMode.AlwaysCompare
            || (revisionCheckMode == RevisionCheckMode.WhenInvalidated
                && (synchronizationState.IsDatasetDirty || dirtyKey is not null));

        var selected = await ReadExactCoreAsync(
                storageKey,
                forcePeerRead,
                synchronizationState.IsDatasetDirty,
                dirtyKey,
                cancellationToken)
            .ConfigureAwait(false);
        return selected is null
            ? null
            : CreateApplicationCopy(selected, storageKey);
    }

    public async ValueTask<TContract> QuerySingleAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null)
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(noneExceptionFactory, RepositoryQueryCardinalityException.NoMatches);
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TImplementation> QuerySingleAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null)
        where TImplementation : class, TContract
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(noneExceptionFactory, RepositoryQueryCardinalityException.NoMatches);
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TContract?> QuerySingleOrDefaultAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TImplementation?> QuerySingleOrDefaultAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
        where TImplementation : class, TContract
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateCardinalityException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    private async ValueTask<TContract?> ReadExactCoreAsync(
        string storageKey,
        bool forcePeerRead,
        bool datasetIsDirty,
        ResilientDirtyKey? dirtyKey,
        CancellationToken cancellationToken)
    {
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
        }
        finally
        {
            preferredGate.Release();
        }

        if (forcePeerRead
            && GetMutationAuthority(storageKey) == MutationAuthority.PreferredWritable
            && !datasetIsDirty
            && dirtyKey is not null
            && dirtyKey.RevisionTimestampUtcMicroseconds > 0
            && preferredRead.State == EndpointReadState.Found
            && preferredMetadata.RevisionTimestampUtcMicroseconds
                >= dirtyKey.RevisionTimestampUtcMicroseconds)
        {
            await _synchronizationStateStore.MarkStorageKeySynchronizedAsync(
                    _policy.DatasetId,
                    storageKey,
                    dirtyKey.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            return preferredMetadata.IsTombstone ? null : preferredRead.Entity;
        }

        if (!forcePeerRead && preferredRead.State == EndpointReadState.Found)
        {
            return preferredMetadata.IsTombstone ? null : preferredRead.Entity;
        }

        var peerRead = await ReadEndpointByStorageKeyAsync(
                _peer,
                ResilientEndpoint.Peer,
                storageKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (preferredRead.State == EndpointReadState.Unavailable
            && peerRead.State == EndpointReadState.Unavailable)
        {
            throw new ResilientRepositoryAvailabilityException(
                ResilientRepositoryOperation.Read,
                storageKey,
                isPartialResult: false,
                preferredRead.Failure,
                peerRead.Failure);
        }

        TContract? selected;
        var preferredIsCurrent = preferredRead.State != EndpointReadState.Unavailable;
        if (peerRead.State != EndpointReadState.Found)
        {
            selected = preferredRead.State == EndpointReadState.Found && !preferredMetadata.IsTombstone
                ? preferredRead.Entity
                : null;
            if (peerRead.State == EndpointReadState.Missing
                && preferredRead.State == EndpointReadState.Found
                && GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly)
            {
                preferredIsCurrent = false;
                ReportPeerAuthorityInvariant(
                    storageKey,
                    preferredMetadata,
                    peerRead.State,
                    ResilientEnvelopeMetadata.Empty,
                    ResilientRepositoryOperation.Read);
            }
        }
        else if (preferredRead.State != EndpointReadState.Found)
        {
            var restored = await RestorePreferredOrDetachAsync(
                    peerRead.Entity!,
                    ReadMetadata(peerRead.Entity),
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            selected = restored.Entity;
            preferredIsCurrent = restored.PreferredIsCurrent;
        }
        else
        {
            var peerMetadata = ReadMetadata(peerRead.Entity);
            if (preferredMetadata.RevisionTimestampUtcMicroseconds
                == peerMetadata.RevisionTimestampUtcMicroseconds
                && preferredMetadata.IsTombstone != peerMetadata.IsTombstone)
            {
                throw new InvalidDataException(
                    $"Storage key '{storageKey}' has the same revision timestamp with conflicting tombstone state.");
            }

            if (peerMetadata.RevisionTimestampUtcMicroseconds
                > preferredMetadata.RevisionTimestampUtcMicroseconds)
            {
                var restored = await RestorePreferredOrDetachAsync(
                        peerRead.Entity!,
                        peerMetadata,
                        storageKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                selected = restored.Entity;
                preferredIsCurrent = restored.PreferredIsCurrent;
            }
            else
            {
                selected = preferredMetadata.IsTombstone ? null : preferredRead.Entity;
                if (preferredMetadata.RevisionTimestampUtcMicroseconds
                        > peerMetadata.RevisionTimestampUtcMicroseconds
                    && GetMutationAuthority(storageKey) == MutationAuthority.PeerAuthoritativeReadOnly)
                {
                    preferredIsCurrent = false;
                    ReportPeerAuthorityInvariant(
                        storageKey,
                        preferredMetadata,
                        peerRead.State,
                        peerMetadata,
                        ResilientRepositoryOperation.Read);
                }
            }
        }

        if (dirtyKey is not null
            && peerRead.State != EndpointReadState.Unavailable
            && preferredIsCurrent)
        {
            await _synchronizationStateStore.MarkStorageKeySynchronizedAsync(
                    _policy.DatasetId,
                    storageKey,
                    dirtyKey.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return selected;
    }

    private async ValueTask<PreferredRestoreResult> RestorePreferredOrDetachAsync(
        TContract peerEntity,
        ResilientEnvelopeMetadata peerMetadata,
        string storageKey,
        CancellationToken cancellationToken)
    {
        var preferredGate = GetKeyGate(storageKey);
        await preferredGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferredRead = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preferredRead.State == EndpointReadState.Unavailable)
            {
                return new PreferredRestoreResult(
                    peerMetadata.IsTombstone ? null : CreateApplicationCopy(peerEntity, storageKey),
                    PreferredIsCurrent: false);
            }

            if (preferredRead.State == EndpointReadState.Found)
            {
                var currentMetadata = ReadMetadata(preferredRead.Entity);
                if (currentMetadata.RevisionTimestampUtcMicroseconds
                    >= peerMetadata.RevisionTimestampUtcMicroseconds)
                {
                    if (currentMetadata.RevisionTimestampUtcMicroseconds
                            == peerMetadata.RevisionTimestampUtcMicroseconds
                        && currentMetadata.IsTombstone != peerMetadata.IsTombstone)
                    {
                        throw new InvalidDataException(
                            $"Storage key '{storageKey}' has the same revision timestamp with conflicting tombstone state.");
                    }

                    return new PreferredRestoreResult(
                        currentMetadata.IsTombstone ? null : preferredRead.Entity,
                        PreferredIsCurrent: true);
                }

                if (GetMutationAuthority(storageKey) == MutationAuthority.PreferredWritable)
                {
                    throw new ResilientRevisionConflictException(
                        storageKey,
                        currentMetadata.RevisionTimestampUtcMicroseconds,
                        peerMetadata.RevisionTimestampUtcMicroseconds);
                }
            }

            if (!_policy.RestorePreferredOnFallback)
            {
                return new PreferredRestoreResult(
                    peerMetadata.IsTombstone ? null : CreateApplicationCopy(peerEntity, storageKey),
                    PreferredIsCurrent: false);
            }

            var candidate = PrepareStagedCopy(peerEntity, preferredRead.Entity, storageKey);
            var inboundMetadata = peerMetadata with
            {
                PendingMutationId = Guid.Empty,
                PendingMutationKind = PendingMutationKind.None
            };
            var canonical = await SaveWithMetadataAsync(
                    _preferredMetadataRepository,
                    candidate,
                    GetExistingEnvelopeCacheKey(
                        preferredRead.Entity,
                        ResilientEndpoint.Preferred,
                        storageKey),
                    inboundMetadata,
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Synchronize,
                    storageKey,
                    cancellationToken,
                    new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(
                        preferredRead.Entity?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                        Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.Import,
                        peerMetadata.RevisionTimestampUtcMicroseconds))
                .ConfigureAwait(false);
            return new PreferredRestoreResult(
                inboundMetadata.IsTombstone ? null : canonical,
                PreferredIsCurrent: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(
                ResilientEndpoint.Preferred,
                ResilientRepositoryOperation.Synchronize,
                storageKey,
                exception);
            return new PreferredRestoreResult(
                peerMetadata.IsTombstone ? null : CreateApplicationCopy(peerEntity, storageKey),
                PreferredIsCurrent: false);
        }
        finally
        {
            preferredGate.Release();
        }
    }

    private async IAsyncEnumerable<TItem> QueryManyInternalAsync<TItem>(
        Func<IRepository<TContract>, CancellationToken, IAsyncEnumerable<TItem>> endpointQuery,
        Func<TItem, bool> filter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TItem : class, TContract
    {
        ThrowIfDisposed();
        if (CompositionOptions is not null)
        {
            foreach (var item in await ReadCompositionAsync(filter, cancellationToken).ConfigureAwait(false))
            { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
            yield break;
        }
        var revisionCheckMode = GetRevisionCheckMode(storageKey: null);
        var synchronizationState = await _synchronizationStateStore
            .GetAsync(_policy.DatasetId, cancellationToken)
            .ConfigureAwait(false);

        if (revisionCheckMode == RevisionCheckMode.WhenInvalidated
            && !synchronizationState.IsDatasetDirty
            && synchronizationState.DirtyStorageKeys.Count > 0)
        {
            foreach (var dirtyStorageKey in synchronizationState.DirtyStorageKeys.Keys)
            {
                await QueryFirstByStorageKeyAsync(dirtyStorageKey, cancellationToken).ConfigureAwait(false);
            }
        }

        var compareAll = revisionCheckMode == RevisionCheckMode.AlwaysCompare
            || (revisionCheckMode == RevisionCheckMode.WhenInvalidated
                && synchronizationState.IsDatasetDirty);
        if (!compareAll)
        {
            var preferred = await ReadEndpointQueryListAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    endpointQuery,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preferred.Failure is null)
            {
                var yieldedPreferred = false;
                foreach (var item in preferred.Items)
                {
                    if (!ReadMetadata(item).IsTombstone && filter(item))
                    {
                        yieldedPreferred = true;
                        var storageKey = GetRequiredStorageKey(item);
                        yield return CreateApplicationCopy(item, storageKey);
                    }
                }

                if (yieldedPreferred)
                {
                    yield break;
                }
            }

            var peer = await ReadEndpointQueryListAsync(
                    _peer,
                    ResilientEndpoint.Peer,
                    endpointQuery,
                    cancellationToken)
                .ConfigureAwait(false);
            if (peer.Failure is not null)
            {
                if (preferred.Failure is null)
                {
                    // The preferred endpoint produced a complete (empty) result. Preserve
                    // offline continuity when the peer cannot be consulted for fallbacks.
                    yield break;
                }

                throw new ResilientRepositoryAvailabilityException(
                    ResilientRepositoryOperation.Read,
                    storageKey: null,
                    isPartialResult: preferred.Items.Count > 0 || peer.Items.Count > 0,
                    preferred.Failure,
                    peer.Failure);
            }

            var seenStorageKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in peer.Items)
            {
                var storageKey = GetRequiredStorageKey(item);
                if (!seenStorageKeys.Add(storageKey))
                {
                    continue;
                }

                if (preferred.Failure is null)
                {
                    var reconciled = await QueryFirstByStorageKeyAsync(storageKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (reconciled is TItem typedReconciled && filter(typedReconciled))
                    {
                        yield return typedReconciled;
                    }

                    continue;
                }

                if (!ReadMetadata(item).IsTombstone && filter(item))
                {
                    yield return CreateApplicationCopy(item, storageKey);
                }
            }

            yield break;
        }

        var preferredCandidates = await ReadEndpointListAsync<TContract>(
                _preferred,
                ResilientEndpoint.Preferred,
                cancellationToken)
            .ConfigureAwait(false);
        var peerCandidates = await ReadEndpointListAsync<TContract>(
                _peer,
                ResilientEndpoint.Peer,
                cancellationToken)
            .ConfigureAwait(false);
        if (preferredCandidates.Failure is not null && peerCandidates.Failure is not null)
        {
            throw new ResilientRepositoryAvailabilityException(
                ResilientRepositoryOperation.Read,
                storageKey: null,
                isPartialResult: preferredCandidates.Items.Count > 0 || peerCandidates.Items.Count > 0,
                preferredCandidates.Failure,
                peerCandidates.Failure);
        }

        var storageKeys = preferredCandidates.Items
            .Concat(peerCandidates.Items)
            .Select(GetRequiredStorageKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var storageKey in storageKeys)
        {
            var selected = await QueryFirstByStorageKeyAsync(storageKey, cancellationToken)
                .ConfigureAwait(false);
            if (selected is TItem typed && filter(typed))
            {
                yield return typed;
            }
        }

        // Broad reads may serve a complete preferred cache while the peer is offline. Only an
        // explicit successful synchronization pass clears a dataset-wide invalidation.
    }

    private async ValueTask<TItem?> QueryFirstInternalAsync<TItem>(
        Func<IRepository<TContract>, CancellationToken, ValueTask<TItem?>> preferredQuery,
        Func<CancellationToken, IAsyncEnumerable<TItem>> reconciliationQuery,
        CancellationToken cancellationToken)
        where TItem : class, TContract
    {
        ThrowIfDisposed();
        var revisionCheckMode = GetRevisionCheckMode(storageKey: null);
        var usePreferredFirstFastPath = CompositionOptions is null && revisionCheckMode == RevisionCheckMode.PreferredOnly;
        if (CompositionOptions is null && revisionCheckMode == RevisionCheckMode.WhenInvalidated)
        {
            var synchronizationState = await _synchronizationStateStore
                .GetAsync(_policy.DatasetId, cancellationToken)
                .ConfigureAwait(false);
            usePreferredFirstFastPath = !synchronizationState.IsDatasetDirty
                && synchronizationState.DirtyStorageKeys.Count == 0;
        }

        if (usePreferredFirstFastPath)
        {
            var preferred = await ReadEndpointFirstAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    preferredQuery,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preferred.Failure is null
                && preferred.Item is not null
                && !ReadMetadata(preferred.Item).IsTombstone)
            {
                var storageKey = GetRequiredStorageKey(preferred.Item);
                return CreateApplicationCopy(preferred.Item, storageKey);
            }
        }

        // Missing/tombstoned preferred results still need normal peer fallback. Dirty and
        // always-compare modes retain the complete reconciliation path by design.
        await foreach (var item in reconciliationQuery(cancellationToken).ConfigureAwait(false))
        {
            return item;
        }

        return null;
    }

    private async ValueTask<EndpointReadResult> ReadEndpointByStorageKeyAsync(
        IRepository<TContract> repository,
        ResilientEndpoint endpoint,
        string storageKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = await repository
                .QueryFirstByStorageKeyAsync(storageKey, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                return new EndpointReadResult(EndpointReadState.Missing, null, null);
            }

            var actualStorageKey = GetRequiredStorageKey(entity);
            if (!string.Equals(actualStorageKey, storageKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The {endpoint} endpoint returned logical key '{actualStorageKey}' "
                    + $"for requested key '{storageKey}'.");
            }

            entity = await UpgradeLegacyRevisionAsync(repository, endpoint, entity, cancellationToken).ConfigureAwait(false);
            return new EndpointReadResult(EndpointReadState.Found, entity, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(endpoint, ResilientRepositoryOperation.Read, storageKey, exception);
            return new EndpointReadResult(EndpointReadState.Unavailable, null, exception);
        }
    }

    private async ValueTask<EndpointListResult<TItem>> ReadEndpointListAsync<TItem>(
        IRepository<TContract> repository,
        ResilientEndpoint endpoint,
        CancellationToken cancellationToken)
        where TItem : class, TContract
    {
        var items = new List<TItem>();
        try
        {
            await foreach (var item in repository
                               .QueryManyAsync<TItem>(static _ => true, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (items.Count >= _policy.MaximumSynchronizationScanItems)
                {
                    throw new InvalidDataException(
                        $"The endpoint scan exceeded {_policy.MaximumSynchronizationScanItems} items.");
                }

                items.Add(await UpgradeLegacyRevisionAsync(repository, endpoint, item, cancellationToken).ConfigureAwait(false));
            }

            return new EndpointListResult<TItem>(items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(endpoint, ResilientRepositoryOperation.Read, storageKey: null, exception);
            return new EndpointListResult<TItem>(items, exception);
        }
    }

    private async ValueTask<EndpointFirstResult<TItem>> ReadEndpointFirstAsync<TItem>(
        IRepository<TContract> repository,
        ResilientEndpoint endpoint,
        Func<IRepository<TContract>, CancellationToken, ValueTask<TItem?>> query,
        CancellationToken cancellationToken)
        where TItem : class, TContract
    {
        try
        {
            var item = await query(repository, cancellationToken).ConfigureAwait(false);
            if (item is not null) item = await UpgradeLegacyRevisionAsync(repository, endpoint, item, cancellationToken).ConfigureAwait(false);
            return new EndpointFirstResult<TItem>(item, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(endpoint, ResilientRepositoryOperation.Read, storageKey: null, exception);
            return new EndpointFirstResult<TItem>(null, exception);
        }
    }

    private async ValueTask<EndpointListResult<TItem>> ReadEndpointQueryListAsync<TItem>(
        IRepository<TContract> repository,
        ResilientEndpoint endpoint,
        Func<IRepository<TContract>, CancellationToken, IAsyncEnumerable<TItem>> query,
        CancellationToken cancellationToken)
        where TItem : class, TContract
    {
        var items = new List<TItem>();
        try
        {
            await foreach (var item in query(repository, cancellationToken).ConfigureAwait(false))
            {
                if (items.Count >= _policy.MaximumSynchronizationScanItems)
                {
                    throw new InvalidDataException(
                        $"The endpoint query exceeded {_policy.MaximumSynchronizationScanItems} items.");
                }

                items.Add(await UpgradeLegacyRevisionAsync(repository, endpoint, item, cancellationToken).ConfigureAwait(false));
            }

            return new EndpointListResult<TItem>(items, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(endpoint, ResilientRepositoryOperation.Read, storageKey: null, exception);
            return new EndpointListResult<TItem>(items, exception);
        }
    }

    private async ValueTask<TItem> UpgradeLegacyRevisionAsync<TItem>(IRepository<TContract> repository,
        ResilientEndpoint endpoint, TItem item, CancellationToken token) where TItem : class, TContract
    {
        if (item.Envelope is not { RevisionTimestampUtcMicroseconds: 0 } envelope) return item;
        var metadata = ResilientEnvelopeMetadataCodec.Decode(envelope.ExtensionMetadata);
        if (metadata.RevisionTimestampUtcMicroseconds <= 0) return item;
        var key = GetRequiredStorageKey(item);
        // Translate the legacy private timestamp once, under the core destination condition.
        // All tombstone, pending and acknowledgement fields survive the format transition.
        TContract canonical;
        try
        {
        canonical = await SaveWithMetadataAsync((IEnvelopeMetadataRepository<TContract>)repository,
            CreateDetachedCopy(item, key), envelope.EnvelopeCacheKey, metadata, endpoint,
            ResilientRepositoryOperation.Synchronize, key, token,
            new Idb.Libraries.Abstractions.Services.Serialization.EnvelopeWriteCondition(0,
                Idb.Libraries.Abstractions.Services.Serialization.EnvelopeRevisionAction.Import,
                metadata.RevisionTimestampUtcMicroseconds)).ConfigureAwait(false);
        }
        catch (Idb.Libraries.Abstractions.Services.Serialization.WriteConflictException<TContract> conflict)
        {
            // Another reader may already have upgraded the same legacy record.
            canonical = await conflict.ReadCurrentAsync(token).ConfigureAwait(false) ?? throw new InvalidDataException("The legacy record was deleted during upgrade.");
        }
        return canonical as TItem ?? throw new InvalidDataException("Legacy revision upgrade changed the contract type.");
    }

    private RevisionCheckMode GetRevisionCheckMode(string? storageKey)
    {
        var mode = _policy.GetRevisionCheckMode(storageKey);
        return Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException(
                "The resilient repository policy returned an invalid revision-check mode.");
    }

    private static void ValidateStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaximumStorageKeyLength)
        {
            throw new ArgumentException(
                $"A storage key between 1 and {MaximumStorageKeyLength} characters is required.",
                nameof(storageKey));
        }
    }

    private static Exception CreateCardinalityException(
        Func<Exception>? exceptionFactory,
        Func<Exception> defaultFactory)
    {
        var exception = (exceptionFactory ?? defaultFactory)();
        return exception
            ?? throw new InvalidOperationException("A repository query exception factory returned null.");
    }

    private enum EndpointReadState
    {
        Found,
        Missing,
        Unavailable
    }

    private readonly record struct EndpointReadResult(
        EndpointReadState State,
        TContract? Entity,
        Exception? Failure);

    private readonly record struct EndpointListResult<TItem>(
        IReadOnlyList<TItem> Items,
        Exception? Failure);

    private readonly record struct EndpointFirstResult<TItem>(
        TItem? Item,
        Exception? Failure);

    private readonly record struct PreferredRestoreResult(
        TContract? Entity,
        bool PreferredIsCurrent);
}
