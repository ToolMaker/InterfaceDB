using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories;

public sealed partial class ResilientRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private ResilientRepositoryOptions<TContract>? CompositionOptions
        => _policy is ResilientRepositoryOptions<TContract> { ReadPreference: not null } options ? options : null;
    private readonly SemaphoreSlim _compositionGate = new(1, 1);
    private string? _compositionIdentity;
    private int _compositionDirty = 1;
    private bool _compositionPending;
    private bool _compositionCacheUsable = true;
    public RepositoryConnection Connection { get; } = new();
    public RepositoryReadPreference? EffectiveReadPreference => CompositionOptions is null ? null
        : !Connection.IsDowntime && !_compositionPending
            ? CompositionOptions.ReadPreference : RepositoryReadPreference.LocalFirst;
    private Exception? _compositionReadFailure;
    private void PublishCompositionChange() => _compositionChanges.PublishChanged();

    private async ValueTask InitializeCompositionAsync(CancellationToken token)
    {
        ThrowIfDisposed();
        await _compositionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await ValidateCompositionAccessAsync(token).ConfigureAwait(false);
            if (_initialized) return;
            await _preferred.InitializeAsync(token).ConfigureAwait(false);
            RepositoryDataAvailability.RequireComplete(_preferred);
            var local = await CompositionSnapshotAsync(_preferred, true, token).ConfigureAwait(false);
            _compositionPending = local.Values.Any(entity => ReadMetadata(entity).HasPendingMutation);

            if (_policy.EnableBackgroundSynchronization)
            {
                if (_preferred is not IRepositoryNotifications<TContract> notifications)
                    throw new NotSupportedException("Enable repository notifications on the dedicated local cache.");
                _compositionLocalSubscription = new(notifications, WakeCompositionAsync, _disposeCancellation.Token);
            }
            _initialized = true;
            if (_policy.EnableBackgroundSynchronization)
                _backgroundWorker = Task.Run(() => RunCompositionWorkerAsync(_disposeCancellation.Token), CancellationToken.None);
        }
        finally { _compositionGate.Release(); }
    }

    private async ValueTask ValidateCompositionAccessAsync(CancellationToken token)
    {
        if (CompositionOptions!.ValidateAccessAsync is { } validate) await validate(token).ConfigureAwait(false);
        if (_peer is IRepositoryCacheIdentity identity)
        {
            var current = await identity.GetCacheIdentityAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(current) || current.Length > 256) throw new InvalidDataException("Invalid remote cache identity.");
            if (_compositionIdentity is { } prior && prior != current)
                throw new InvalidOperationException("The remote identity changed. Dispose this composition and open that account's separate cache.");
            _compositionIdentity = current;
        }
    }

    private async ValueTask<Dictionary<string, TContract>> CompositionSnapshotAsync(
        IRepository<TContract> endpoint, bool local, CancellationToken token)
    {
        await endpoint.InitializeAsync(token).ConfigureAwait(false);
        RepositoryDataAvailability.RequireComplete(endpoint);
        var result = new Dictionary<string, TContract>(StringComparer.Ordinal);
        await foreach (var entity in endpoint.QueryManyAsync(_ => true, token).WithCancellation(token).ConfigureAwait(false))
        {
            if (result.Count >= _policy.MaximumSynchronizationScanItems)
                throw new InvalidDataException("The composition snapshot exceeds its configured record limit.");
            var key = GetRequiredStorageKey(entity);
            if (!result.TryAdd(key, entity))
                throw new InvalidDataException("Composition requires exactly one entity per logical storage key.");
            var metadata = ReadMetadata(entity);
            if (entity.Envelope is null || entity.Envelope.RevisionTimestampUtcMicroseconds <= 0)
                throw new InvalidDataException("Composition requires committed envelope revisions.");
            if (local && (metadata.FormatVersion != 3 || metadata.BasePeerRevision < 0
                || metadata.CacheScope != CompositionOptions!.CacheScope || metadata.RemoteIdentity != _compositionIdentity))
                throw new InvalidDataException("This cache belongs to another scope or requires explicit migration into a new composition cache.");
            if (!local && metadata.HasPendingMutation)
                throw new InvalidDataException("The remote endpoint contains unacknowledged local mutations.");
        }
        return result;
    }

    private async ValueTask<IReadOnlyList<TItem>> ReadCompositionAsync<TItem>(Func<TItem, bool> filter, CancellationToken token)
        where TItem : class, TContract
    {
        await InitializeCompositionAsync(token).ConfigureAwait(false);
        await _compositionGate.WaitAsync(token).ConfigureAwait(false);
        bool changed = false;
        try
        {
            ThrowIfDisposed();
            var local = await CompositionSnapshotAsync(_preferred, true, token).ConfigureAwait(false);
            IReadOnlyList<TItem> Select(Dictionary<string, TContract> items) => items.Values
                .Where(entity => !ReadMetadata(entity).IsTombstone).OfType<TItem>()
                .Select(entity => CreateApplicationCopy(entity, GetRequiredStorageKey(entity)))
                .Where(filter).ToArray();
            var matches = Select(local);
            if (Connection.IsDowntime
                && _compositionCacheUsable && _compositionReadFailure is null && CompositionOptions!.EnableBackgroundSynchronization)
                return matches;
            var invalidation = await _synchronizationStateStore.GetAsync(_policy.DatasetId, token).ConfigureAwait(false);
            if (CompositionOptions!.ReadPreference == RepositoryReadPreference.LocalFirst
                && _compositionCacheUsable && _compositionReadFailure is null && matches.Count != 0
                && !invalidation.IsDatasetDirty && invalidation.DirtyStorageKeys.Count == 0)
                return matches;
            Dictionary<string, TContract> selected;
            try
            {
                var refreshed = await ReconcileCompositionAsync(local, token).ConfigureAwait(false);
                changed = refreshed.Result.PreferredRefreshes > 0;
                _compositionReadFailure = refreshed.Result.Failures.FirstOrDefault(
                    failure => failure.Endpoint == ResilientEndpoint.Peer && !failure.IsTransient)?.Exception;
                if (_compositionReadFailure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                selected = refreshed.Visible;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsUnavailable(exception) && _compositionCacheUsable && _compositionReadFailure is null)
            {
                Connection.Set(RepositoryConnectionState.Offline, exception);
                ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Read, null, exception);
                return matches;
            }
            catch (Exception exception)
            {
                if (!IsUnavailable(exception)) _compositionReadFailure = exception;
                Connection.Set(IsUnavailable(exception) ? RepositoryConnectionState.Offline : RepositoryConnectionState.Faulted, exception);
                throw;
            }
            // Application predicates execute outside the transport failure classifier.
            return Select(selected);
        }
        finally
        {
            _compositionGate.Release();
            if (changed) PublishCompositionChange();
        }
    }

    private async ValueTask<(TContract Entity, bool Existed)> WriteCompositionAsync(TContract input, bool deleting, CancellationToken token)
    {
        ThrowIfCompositionFeedbackWrite(deleting ? ResilientRepositoryOperation.Delete : ResilientRepositoryOperation.Save);
        await InitializeCompositionAsync(token).ConfigureAwait(false);
        await _compositionGate.WaitAsync(token).ConfigureAwait(false);
        bool changed = false;
        try
        {
            ThrowIfDisposed();
            var key = GetInputStorageKey(input);
            var local = await CompositionSnapshotAsync(_preferred, true, token).ConfigureAwait(false);
            var localFirst = CompositionOptions!.ReadPreference == RepositoryReadPreference.LocalFirst || _compositionPending
                || Connection.IsDowntime;
            if (localFirst && _compositionReadFailure is { } remoteFailure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(remoteFailure).Throw();
            if (localFirst && !_compositionCacheUsable)
                throw new InvalidOperationException("Refresh the cache after its failed update before accepting another local mutation.");
            Dictionary<string, TContract> targetSnapshot;
            try { targetSnapshot = localFirst ? local : await CompositionSnapshotAsync(_peer, false, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsUnavailable(exception) && _compositionCacheUsable && _compositionReadFailure is null)
            {
                Connection.Set(RepositoryConnectionState.Offline, exception);
                ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Save, key, exception);
                localFirst = true;
                targetSnapshot = local;
                _synchronizationWakeSignal.Writer.TryWrite(0);
            }
            if (_compositionReadFailure is { } blocked)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(blocked).Throw();
            if (localFirst && !_compositionCacheUsable)
                throw new InvalidOperationException("Reconcile the uncertain remote write before accepting offline edits.");
            var target = targetSnapshot.GetValueOrDefault(key);
            if (target is null && targetSnapshot.Count >= _policy.MaximumSynchronizationScanItems)
                throw new InvalidDataException("The write would exceed the composition record limit.");
            var prior = ReadMetadata(target);
            var existed = target is not null && !prior.IsTombstone;
            if (deleting && prior.IsTombstone) return (CreateApplicationCopy(target!, key), false);
            var baseRevision = localFirst
                ? (prior.HasPendingMutation ? prior.BasePeerRevision : prior.BasePeerRevision < 0 ? 0 : prior.BasePeerRevision)
                : 0;
            var metadata = new ResilientEnvelopeMetadata(3, 0, deleting,
                localFirst ? Guid.NewGuid() : Guid.Empty,
                localFirst ? deleting ? PendingMutationKind.Delete : PendingMutationKind.Upsert : PendingMutationKind.None,
                localFirst ? prior.LastAppliedMutationId : Guid.NewGuid(), baseRevision,
                localFirst ? CompositionOptions.CacheScope : null, localFirst ? _compositionIdentity : null, prior.RemoteStorageKey);
            var candidate = deleting ? CreateDetachedCopy(target ?? input, key) : PrepareStagedCopy(input, target, key);
            var expected = input.Envelope?.RevisionTimestampUtcMicroseconds ?? (prior.IsTombstone ? prior.RevisionTimestampUtcMicroseconds : 0);
            var endpoint = localFirst ? _preferredMetadataRepository : _peerMetadataRepository;
            // Dispatch may commit even if its acknowledgement is lost. Until reconciliation,
            // neither an acknowledged nor an uncertain remote write permits stale offline fallback.
            if (!localFirst) _compositionCacheUsable = false;
            var committed = await CompositionSaveAsync(endpoint, candidate, target, key, metadata,
                new EnvelopeWriteCondition(expected, ExpectedMissing: target is null), token).ConfigureAwait(false);
            changed = localFirst;
            if (localFirst)
            {
                _compositionPending = true;
                Interlocked.Exchange(ref _compositionDirty, 1);
                _synchronizationWakeSignal.Writer.TryWrite(0);
            }
            // Remote-first commands end at the remote commit. The notification/read reconciliation
            // path alone imports remote state and publishes the local change.
            return (CreateApplicationCopy(committed, key), existed);
        }
        finally
        {
            _compositionGate.Release();
            if (changed) PublishCompositionChange();
        }
    }

    private ValueTask<TContract> CompositionSaveAsync(IEnvelopeMetadataRepository<TContract> endpoint,
        TContract detached, TContract? existing, string key, ResilientEnvelopeMetadata metadata,
        EnvelopeWriteCondition condition, CancellationToken token)
        => endpoint.SaveWithExtensionMetadataAsync(detached, existing?.Envelope?.EnvelopeCacheKey ?? key,
            ResilientEnvelopeMetadataCodec.Encode(metadata with { RevisionTimestampUtcMicroseconds = 0 }),
            condition, token);

    private async ValueTask<TContract> ImportCompositionAsync(TContract remote, TContract? local, string key, CancellationToken token)
    {
        var revision = remote.Envelope!.RevisionTimestampUtcMicroseconds;
        var metadata = ReadMetadata(remote) with
        {
            FormatVersion = 3, RevisionTimestampUtcMicroseconds = 0, BasePeerRevision = revision, RemoteStorageKey = remote.Envelope.EnvelopeCacheKey,
            CacheScope = CompositionOptions!.CacheScope, RemoteIdentity = _compositionIdentity, PendingMutationId = Guid.Empty, PendingMutationKind = PendingMutationKind.None
        };
        using var origin = _compositionLocalSubscription?.BeginWriteScope();
        return await CompositionSaveAsync(_preferredMetadataRepository, CreateDetachedCopy(remote, key), local, key, metadata,
            new EnvelopeWriteCondition(local?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                EnvelopeRevisionAction.Import, revision, ExpectedMissing: local is null), token).ConfigureAwait(false);
    }

    private async ValueTask<(Dictionary<string, TContract> Visible, ResilientSynchronizationResult Result)> ReconcileCompositionAsync(
        Dictionary<string, TContract> local, CancellationToken token)
    {
        // Fully enumerate before applying removals: a partial or failed remote scan never means deletion.
        var observed = await _synchronizationStateStore.GetAsync(_policy.DatasetId, token).ConfigureAwait(false);
        var remote = await ReadRemoteCompositionAsync(local, token).ConfigureAwait(false);
        var visible = new Dictionary<string, TContract>(remote, StringComparer.Ordinal);
        foreach (var pair in local.Where(pair => ReadMetadata(pair.Value).HasPendingMutation))
            visible[pair.Key] = pair.Value;
        var result = new SynchronizationAccumulator();
        foreach (var pair in local.Where(pair => ReadMetadata(pair.Value).HasPendingMutation))
        {
            var key = pair.Key;
            var pending = ReadMetadata(pair.Value);
            visible[key] = pair.Value;
            result.PendingMutationsExamined++;
            var peer = remote.GetValueOrDefault(key);
            var peerMetadata = ReadMetadata(peer);
            if (peerMetadata.LastAppliedMutationId != pending.PendingMutationId
                    && (peer?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0) != pending.BasePeerRevision)
            {
                result.Conflicts++;
                ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Synchronize, key,
                    new ResilientRevisionConflictException(key, pending.BasePeerRevision, peerMetadata.RevisionTimestampUtcMicroseconds));
                continue;
            }
            var failureEndpoint = ResilientEndpoint.Peer;
            try
            {
                if (peerMetadata.LastAppliedMutationId != pending.PendingMutationId)
                {
                    var metadata = pending with
                    {
                        PendingMutationId = Guid.Empty, PendingMutationKind = PendingMutationKind.None,
                        LastAppliedMutationId = pending.PendingMutationId, CacheScope = null, RemoteIdentity = null, BasePeerRevision = 0
                    };
                    peer = await CompositionSaveAsync(_peerMetadataRepository, CreateDetachedCopy(pair.Value, key), peer, key,
                        metadata, new EnvelopeWriteCondition(pending.BasePeerRevision, ExpectedMissing: peer is null), token).ConfigureAwait(false);
                    remote[key] = peer;
                    result.PeerCommits++;
                }
                failureEndpoint = ResilientEndpoint.Preferred;
                var acknowledged = ReadMetadata(peer!) with
                {
                    FormatVersion = 3, BasePeerRevision = peer!.Envelope!.RevisionTimestampUtcMicroseconds, RemoteStorageKey = peer.Envelope.EnvelopeCacheKey,
                    CacheScope = CompositionOptions!.CacheScope, RemoteIdentity = _compositionIdentity, PendingMutationId = Guid.Empty, PendingMutationKind = PendingMutationKind.None
                };
                // A fresh acknowledged commit has our exact payload, so only its delivery metadata changes.
                // After an uncertain earlier commit, an ordinary endpoint writer may have preserved the
                // mutation ID while editing the payload. Import that freshly observed remote state.
                visible[key] = peerMetadata.LastAppliedMutationId == pending.PendingMutationId || CompositionOptions!.ReadPreference == RepositoryReadPreference.RemoteFirst
                    ? await ImportCompositionAsync(peer!, pair.Value, key, token).ConfigureAwait(false)
                    : await CompositionSaveAsync(_preferredMetadataRepository,
                        CreateDetachedCopy(pair.Value, key), pair.Value, key, acknowledged,
                        new EnvelopeWriteCondition(pair.Value.Envelope!.RevisionTimestampUtcMicroseconds,
                            EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: pair.Value.Envelope.ExtensionMetadata), token).ConfigureAwait(false);
                if (peerMetadata.LastAppliedMutationId == pending.PendingMutationId) result.PreferredRefreshes++;
                result.Acknowledgements++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (exception is WriteConflictException<TContract>)
                {
                    result.Conflicts++;
                    ReportFailure(failureEndpoint, ResilientRepositoryOperation.Synchronize, key, exception);
                }
                else AddCompositionFailure(result, failureEndpoint, key, exception);
                if (IsEndpointWideFailure(exception)) break;
            }
        }
        foreach (var pair in remote)
        {
            var key = pair.Key;
            var cached = local.GetValueOrDefault(key);
            if (cached is not null && ReadMetadata(cached).HasPendingMutation) continue;
            if (cached is not null && ReadMetadata(cached).BasePeerRevision == pair.Value.Envelope!.RevisionTimestampUtcMicroseconds
                && ReadMetadata(cached).IsTombstone == ReadMetadata(pair.Value).IsTombstone)
            {
                visible[key] = cached;
                continue;
            }
            try
            {
                visible[key] = await ImportCompositionAsync(pair.Value, cached, key, token).ConfigureAwait(false);
                result.PreferredRefreshes++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { AddCompositionFailure(result, ResilientEndpoint.Preferred, key, exception); }
        }
        foreach (var pair in local)
        {
            if (remote.ContainsKey(pair.Key) || ReadMetadata(pair.Value).HasPendingMutation) continue;
            try
            {
                using var origin = _compositionLocalSubscription?.BeginWriteScope();
                await _preferred.DeleteAsync(pair.Value, token).ConfigureAwait(false);
                result.PreferredRefreshes++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { AddCompositionFailure(result, ResilientEndpoint.Preferred, pair.Key, exception); }
        }
        _compositionPending = local.Values.Count(entity => ReadMetadata(entity).HasPendingMutation) > result.Acknowledgements;
        _compositionCacheUsable = !result.Failures.Any(failure => failure.Endpoint == ResilientEndpoint.Preferred);
        var completed = result.ToResult();
        if (completed.IsSuccessful)
        {
            await _synchronizationStateStore.MarkDatasetSynchronizedAsync(_policy.DatasetId, observed.LatestObservedCheckpoint, token).ConfigureAwait(false);
            if (_nextCompositionCursor is { } next)
            {
                if (CompositionOptions!.CursorStore is { } store)
                    await store.WriteAsync(CompositionCursorScope, next, token).ConfigureAwait(false);
                _compositionCursor = next;
            }
            Connection.Set(RepositoryConnectionState.Online);
        }
        else if (completed.Failures.Count == 0) Connection.Set(RepositoryConnectionState.Synchronizing);
        return (visible, completed);
    }

    private void AddCompositionFailure(SynchronizationAccumulator result, ResilientEndpoint endpoint, string? key, Exception exception)
    {
        result.AddFailure(new(endpoint, ResilientRepositoryOperation.Synchronize, key, exception, IsUnavailable(exception)));
        ReportFailure(endpoint, ResilientRepositoryOperation.Synchronize, key, exception);
    }

    private async ValueTask<ResilientSynchronizationResult> SynchronizeCompositionAsync(CancellationToken token)
    {
        await InitializeCompositionAsync(token).ConfigureAwait(false);
        await _compositionGate.WaitAsync(token).ConfigureAwait(false);
        ResilientSynchronizationResult? result = null;
        try
        {
            ThrowIfDisposed();
            var local = await CompositionSnapshotAsync(_preferred, true, token).ConfigureAwait(false);
            result = (await ReconcileCompositionAsync(local, token).ConfigureAwait(false)).Result;
            _compositionReadFailure = result.Failures.FirstOrDefault(
                failure => failure.Endpoint == ResilientEndpoint.Peer && !failure.IsTransient)?.Exception;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (!IsUnavailable(exception)) _compositionReadFailure = exception;
                Connection.Set(IsUnavailable(exception) ? RepositoryConnectionState.Offline : RepositoryConnectionState.Faulted, exception);
            var accumulator = new SynchronizationAccumulator();
            AddCompositionFailure(accumulator, ResilientEndpoint.Peer, null, exception);
            return accumulator.ToResult();
        }
        finally
        {
            _compositionGate.Release();
            if (result is { PreferredRefreshes: > 0 })
                PublishCompositionChange();
        }
    }

    private readonly RepositoryChangePublisher _compositionChanges = new();
    private RepositoryNotificationAdapter<TContract>? _compositionLocalSubscription;

    public IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default)
    {
        ThrowIfDisposed();
        return _compositionChanges.SubscribeChanges(handler, lifetime);
    }

    private ValueTask WakeCompositionAsync()
    {
        Interlocked.Exchange(ref _compositionDirty, 1);
        _synchronizationWakeSignal.Writer.TryWrite(0);
        return ValueTask.CompletedTask;
    }

    private IRepositoryChangeSubscription SubscribeRemoteComposition(CancellationToken token)
    {
        if (_peer is IRepositoryChangeNotifications remote) return remote.SubscribeChanges(WakeCompositionAsync, token);
        if (_peer is IRepositoryNotifications<TContract> local)
            return new RepositoryNotificationAdapter<TContract>(local, WakeCompositionAsync, token);
        throw new NotSupportedException("Background composition requires a remote repository with disposable change subscriptions.");
    }

    private async Task WaitForCompositionChangeAsync(IRepositoryChangeSubscription subscription, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wake = _synchronizationWakeSignal.Reader.WaitToReadAsync(wait.Token).AsTask();
        try
        {
            var localEnded = _compositionLocalSubscription!.Completion;
            if (await Task.WhenAny(wake, subscription.Completion, localEnded).ConfigureAwait(false) == localEnded)
            {
                await localEnded.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The local cache subscription ended.");
            }
            if (subscription.Completion.IsCompleted)
            {
                await subscription.Completion.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new EndOfStreamException("Repository subscription ended; reconnect and catch up.");
            }
            if (!await wake.ConfigureAwait(false)) token.ThrowIfCancellationRequested();
        }
        finally
        {
            await wait.CancelAsync().ConfigureAwait(false);
            try { await wake.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunCompositionWorkerAsync(CancellationToken token)
    {
        var retry = new RepositoryRetryBackoff(_policy.BackgroundRetryDelay);
        while (!token.IsCancellationRequested)
        {
            IRepositoryChangeSubscription? subscription = null;
            try
            {
                if (_compositionLocalSubscription!.IsDisposed)
                {
                    await _compositionGate.WaitAsync(token).ConfigureAwait(false);
                    try { _compositionLocalSubscription = new((IRepositoryNotifications<TContract>)_preferred, WakeCompositionAsync, token); }
                    finally { _compositionGate.Release(); }
                }
                Connection.Set(RepositoryConnectionState.Reconnecting);
                subscription = SubscribeRemoteComposition(token);
                await subscription.Ready.WaitAsync(token).ConfigureAwait(false);
                await _compositionLocalSubscription.Ready.WaitAsync(token).ConfigureAwait(false);
                Connection.Set(RepositoryConnectionState.Synchronizing);
                // Subscribe first, then catch up. Any concurrent commit leaves another wake queued.
                await WakeCompositionAsync().ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    while (_synchronizationWakeSignal.Reader.TryRead(out _)) { }
                    Interlocked.Exchange(ref _compositionDirty, 0);
                    var result = await SynchronizeCompositionAsync(token).ConfigureAwait(false);
                    var failure = result.Failures.FirstOrDefault();
                    if (failure is not null)
                    {
                        if (failure.IsTransient || failure.Exception is RepositoryAccessRenewalRequiredException) throw failure.Exception;
                        // Non-transient errors are surfaced; a future change or explicit sync can recover.
                    }
                    else retry.Reset();
                    await WaitForCompositionChangeAsync(subscription, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested) break;
                if (!IsUnavailable(exception) && exception is not RepositoryAccessRenewalRequiredException)
                {
                    _compositionReadFailure = exception;
                    Connection.Set(RepositoryConnectionState.Faulted, exception);
                    ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Synchronize, null, exception);
                    break;
                }
                ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Synchronize, null, exception);
                Connection.Set(exception is RepositoryAccessRenewalRequiredException
                    ? RepositoryConnectionState.Faulted : RepositoryConnectionState.Offline, exception);
                // Only failed work / disconnected subscriptions are retried. Healthy idle sources never poll.
                try { await Task.Delay(retry.NextDelay(), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            finally
            {
                if (subscription is not null)
                {
                    subscription.Dispose();
                    try { await subscription.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    { ReportFailure(ResilientEndpoint.Peer, ResilientRepositoryOperation.Synchronize, null, exception); }
                    catch (Exception) when (token.IsCancellationRequested) { }
                }
            }
        }
    }
}
