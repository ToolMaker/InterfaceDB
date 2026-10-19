using System.Linq.Expressions;
using Idb.Diagnostics;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

internal sealed class RepositoryNotificationHub<T> : IDisposable where T : class, IStoredContract<T>
{
    private readonly ICommittedEnvelopeSnapshotSource<T> _source;
    private readonly RepositoryNotificationOptions _options;
    private readonly Func<CancellationToken, ValueTask> _initialize;
    private readonly IDatabaseTelemetry? _telemetry;
    private readonly NotificationMutationLocks<T> _mutations = new();
    private readonly SemaphoreSlim _callbacks;
    private readonly object _subscriptionsGate = new();
    private RepositoryNotificationSubscription<T>[] _subscriptions = [];
    private NotificationComparison<T>? _comparison;
    private bool _disposed;
    private Phase _phase = new(Task.CompletedTask, null);
    private static readonly AsyncLocal<Mutation?> CurrentMutation = new();

    // Subscription handover waits for preceding writes so Ready + snapshot cannot miss
    // a mutation admitted before registration. Writers never hold this lock across storage I/O.
    private sealed class Phase(Task ready, NotificationBaselineCache<T>? cache)
    {
        internal readonly Task Ready = ready;
        internal readonly NotificationBaselineCache<T>? Cache = cache;
        internal int Active;
        internal bool Sealed;
        private TaskCompletionSource? _drained;
        internal Task Seal()
        {
            Sealed = true;
            if (Active == 0) { Cache?.Clear(); return Ready; }
            _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return Task.WhenAll(Ready, _drained.Task);
        }
        internal void Exit()
        {
            if (--Active != 0 || !Sealed) return;
            Cache?.Clear(); _drained?.TrySetResult();
        }
    }

    private sealed class Mutation : IDisposable
    {
        internal readonly RepositoryNotificationHub<T> Owner;
        internal readonly Mutation? Previous = CurrentMutation.Value;
        internal bool Active = true;
        internal Mutation(RepositoryNotificationHub<T> owner) { Owner = owner; CurrentMutation.Value = this; }
        public void Dispose() { Volatile.Write(ref Active, false); CurrentMutation.Value = Previous; }
    }

    private (Phase Phase, RepositoryNotificationSubscription<T>[] Subscribers) Enter()
    {
        if (NotificationWriteContext.Comparing.Value)
            throw new InvalidOperationException("Notification comparers must be pure and cannot save or delete entities.");
        for (var mutation = CurrentMutation.Value; mutation is not null; mutation = mutation.Previous)
            if (ReferenceEquals(mutation.Owner, this) && Volatile.Read(ref mutation.Active))
                throw new InvalidOperationException("A storage callback cannot re-enter an observed repository mutation. Use repository notifications, which run after commit and outside mutation locks.");
        lock (_subscriptionsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _phase.Active++;
            return (_phase, _subscriptions);
        }
    }

    private void Exit(Phase phase) { lock (_subscriptionsGate) phase.Exit(); }

    internal RepositoryNotificationHub(IEnvelopeRepository<T> envelopes, RepositoryNotificationOptions options,
        Func<CancellationToken, ValueTask> initialize, IDatabaseTelemetry? telemetry = null)
    {
        _source = envelopes as ICommittedEnvelopeSnapshotSource<T>
            ?? throw new NotSupportedException("This storage provider does not support committed notification snapshots.");
        if (options.QueueCapacity < 1 || options.MaximumSubscribers < 1 || options.MaximumConcurrentCallbacks < 1
            || options.MaximumSnapshotBytes < 1 || options.MaximumCollectionItems < 1
            || options.MaximumQueuedBytesPerSubscriber < 1 || options.MaximumBaselineEntries < 0 || options.MaximumBaselineBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Notification limits must be positive.");
        if (options.MaximumCausalDepth < 1) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options with { PropertyComparers = new Dictionary<string, IEqualityComparer<object?>>(options.PropertyComparers) };
        _initialize = initialize;
        _telemetry = telemetry;
        _callbacks = new(options.MaximumConcurrentCallbacks, options.MaximumConcurrentCallbacks);
    }

    internal IRepositorySubscription Subscribe(RepositoryEventKind kind,
        Func<T, IReadOnlyList<string>, ValueTask> handler, Expression<Func<T, bool>>? filter,
        CancellationToken lifetime, Func<ValueTask<bool>>? keepAlive, bool receiveOwnWrites)
    {
        var compiled = NotificationFilterValidator.Compile(filter);
        RepositoryNotificationSubscription<T> subscription;
        lock (_subscriptionsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscriptions.Length >= _options.MaximumSubscribers)
                throw new InvalidOperationException("Repository subscriber limit reached.");
            if (kind == RepositoryEventKind.Changed) _comparison ??= new(_options);
            subscription = new(kind, handler, compiled, keepAlive, _options, _callbacks, Remove, receiveOwnWrites, _telemetry);
            _phase = new(_phase.Seal(), new(_options));
            subscription.Ready = _phase.Ready;
            _subscriptions = [.. _subscriptions, subscription];
        }
        subscription.SetLifetime(lifetime);
        return subscription;
    }

    private void Remove(RepositoryNotificationSubscription<T> subscription)
    {
        lock (_subscriptionsGate)
        {
            var previous = _subscriptions;
            _subscriptions = previous.Where(item => !ReferenceEquals(item, subscription)).ToArray();
            if (previous.Length != 0 && _subscriptions.Length == 0)
                _phase = new(_phase.Seal(), null);
        }
    }

    private ValueTask<ICommittedEnvelopeSnapshot<T>?> Capture(T entity, CancellationToken token)
        => entity.Envelope is { EnvelopeCacheKey.Length: > 0 } envelope
            ? _source.CaptureCommittedAsync(envelope, _options.MaximumSnapshotBytes, token)
            : ValueTask.FromResult<ICommittedEnvelopeSnapshot<T>?>(null);

    private ValueTask<ICommittedEnvelopeSnapshot<T>?> Before(T entity, NotificationBaselineCache<T> cache, CancellationToken token)
        // Explicit create conditions are checked atomically by the writer. A staged new identity
        // has no committed baseline (and a File provider has not assigned its locator yet).
        => entity.Envelope is { } staged && EnvelopeWriteCoordinator<T>.ExpectsMissing(staged)
            ? ValueTask.FromResult<ICommittedEnvelopeSnapshot<T>?>(null)
            : entity.Envelope is { } envelope && cache.Find(envelope.EnvelopeCacheKey) is { } snapshot
            ? ValueTask.FromResult<ICommittedEnvelopeSnapshot<T>?>(snapshot) : Capture(entity, token);

    internal async ValueTask<T> SaveAsync(T entity, Func<T, CancellationToken, ValueTask<T>> save, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var (phase, subscribers) = Enter();
        var mutation = new Mutation(this);
        IDisposable? lease = null;
        TaskCompletionSource? released = null;
        try
        {
            await phase.Ready.WaitAsync(token).ConfigureAwait(false);
            if (phase.Cache is not { } cache) return await save(entity, token).ConfigureAwait(false);
            lease = await _mutations.AcquireAsync(entity, token).ConfigureAwait(false);
            await _initialize(token).ConfigureAwait(false);
            var observe = subscribers.Any(item => item.Kind != RepositoryEventKind.Deleted && item.Accepts(NotificationWriteContext.Current));
            if (!observe) { cache.Forget(entity); return await save(entity, token).ConfigureAwait(false); }
            _comparison?.ValidateRuntime(entity);
            var before = await Before(entity, cache, token).ConfigureAwait(false);
            cache.Forget(entity);
            T saved;
            using var writeCapture = CommittedWriteCapture<T>.Begin();
            try { saved = await save(entity, token).ConfigureAwait(false); }
            catch (WriteConflictException<T>) { throw; }
            catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); throw; }
            released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try { await PublishSaveAsync(before, saved, cache, subscribers, released.Task).ConfigureAwait(false); }
            catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); } // Notification preparation cannot roll back a committed save.
            return saved;
        }
        finally { mutation.Dispose(); lease?.Dispose(); Exit(phase); released?.TrySetResult(); }
    }

    private async ValueTask PublishSaveAsync(ICommittedEnvelopeSnapshot<T>? before, T saved,
        NotificationBaselineCache<T> cache, RepositoryNotificationSubscription<T>[] subscribers, Task released)
    {
        if (EnvelopeWriteOutcome.IsNoChange(saved.Envelope)) return;
        before = CommittedWriteCapture<T>.FindBefore(saved.Envelope) ?? before;
        var kind = before is null ? RepositoryEventKind.Created : RepositoryEventKind.Changed;
        if (!subscribers.Any(item => item.Kind == kind && item.Accepts(NotificationWriteContext.Current))) return;
        var after = CommittedWriteCapture<T>.Find(saved.Envelope) ?? await Capture(saved, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("Committed notification result is unavailable.");
        if (after.ByteLength > _options.MaximumSnapshotBytes)
            throw new InvalidDataException("Notification snapshot exceeds the configured byte limit.");
        cache.Remember(saved, after);
        var item = new RepositoryEvent<T>(after, before, kind == RepositoryEventKind.Changed ? _comparison : null,
            released, NotificationWriteContext.Current);
        foreach (var subscriber in subscribers)
            if (subscriber.Kind == kind) subscriber.Enqueue(item);
    }

    internal async ValueTask<IReadOnlyList<T>> SaveManyAsync(IReadOnlyList<T> entities,
        Func<IEnumerable<T>, CancellationToken, ValueTask<IReadOnlyList<T>>> save, CancellationToken token)
    {
        var (phase, subscribers) = Enter();
        var mutation = new Mutation(this);
        IDisposable? lease = null;
        TaskCompletionSource? released = null;
        try
        {
            await phase.Ready.WaitAsync(token).ConfigureAwait(false);
            if (phase.Cache is not { } cache) return await save(entities, token).ConfigureAwait(false);
            lease = await _mutations.AcquireAsync(entities, token).ConfigureAwait(false);
            await _initialize(token).ConfigureAwait(false);
            if (!subscribers.Any(item => item.Kind != RepositoryEventKind.Deleted && item.Accepts(NotificationWriteContext.Current)))
            {
                foreach (var entity in entities) cache.Forget(entity);
                return await save(entities, token).ConfigureAwait(false);
            }
            var before = new ICommittedEnvelopeSnapshot<T>?[entities.Count];
            using var writeCapture = CommittedWriteCapture<T>.Begin();
            for (var index = 0; index < entities.Count; index++)
            {
                _comparison?.ValidateRuntime(entities[index]);
                before[index] = await Before(entities[index], cache, token).ConfigureAwait(false);
                cache.Forget(entities[index]);
            }
            released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<T> saved;
            try { saved = await save(entities, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IStorageBulkOperationResult)
            {
                var partial = (IStorageBulkOperationResult)exception;
                if (exception.Data.Contains("InterfaceDB.IndexRefreshFailure")) { Fault(subscribers); throw; }
                foreach (var index in partial.SucceededIndexes)
                {
                    try { await PublishSaveAsync(before[index], partial.CommittedItems.TryGetValue(index, out var committed) ? (T)committed : entities[index], cache, subscribers, released.Task).ConfigureAwait(false); }
                    catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); break; }
                }
                throw;
            }
            catch (WriteConflictException<T>) { throw; }
            catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); throw; }
            try
            {
                for (var index = 0; index < saved.Count; index++)
                    await PublishSaveAsync(before[index], saved[index], cache, subscribers, released.Task).ConfigureAwait(false);
            }
            catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); }
            return saved;
        }
        finally { mutation.Dispose(); lease?.Dispose(); Exit(phase); released?.TrySetResult(); }
    }

    internal async ValueTask<bool> DeleteAsync(T entity, Func<T, CancellationToken, ValueTask<bool>> delete, CancellationToken token)
    {
        var (phase, subscribers) = Enter();
        var mutation = new Mutation(this);
        IDisposable? lease = null;
        TaskCompletionSource? released = null;
        try
        {
            await phase.Ready.WaitAsync(token).ConfigureAwait(false);
            if (phase.Cache is not { } cache) return await delete(entity, token).ConfigureAwait(false);
            lease = await _mutations.AcquireAsync(entity, token).ConfigureAwait(false);
            await _initialize(token).ConfigureAwait(false);
            if (!subscribers.Any(item => item.Kind == RepositoryEventKind.Deleted && item.Accepts(NotificationWriteContext.Current)))
            { cache.Forget(entity); return await delete(entity, token).ConfigureAwait(false); }
            var before = await Before(entity, cache, token).ConfigureAwait(false);
            cache.Forget(entity);
            bool deleted;
            try { deleted = await delete(entity, token).ConfigureAwait(false); }
            catch (WriteConflictException<T>) { throw; }
            catch (Exception notificationError) { DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(notificationError); Fault(subscribers); throw; }
            if (deleted && before is not null)
            {
                released = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var item = new RepositoryEvent<T>(before, null, null, released.Task, NotificationWriteContext.Current);
                foreach (var subscriber in subscribers)
                    if (subscriber.Kind == RepositoryEventKind.Deleted) subscriber.Enqueue(item);
            }
            else if (deleted) Fault(subscribers);
            return deleted;
        }
        finally { mutation.Dispose(); lease?.Dispose(); Exit(phase); released?.TrySetResult(); }
    }

    private static void Fault(RepositoryNotificationSubscription<T>[] subscriptions)
    {
        foreach (var subscriber in subscriptions) subscriber.Stop(RepositorySubscriptionEndReason.ObservationFailed);
    }

    public void Dispose()
    {
        RepositoryNotificationSubscription<T>[] subscriptions;
        lock (_subscriptionsGate)
        {
            if (_disposed) return;
            _disposed = true; subscriptions = _subscriptions; _subscriptions = [];
            _phase.Seal();
        }
        foreach (var subscriber in subscriptions) subscriber.Stop(RepositorySubscriptionEndReason.RepositoryDisposed);
        // In-flight operations/callbacks may still finish. Do not dispose gates beneath them.
    }
}
