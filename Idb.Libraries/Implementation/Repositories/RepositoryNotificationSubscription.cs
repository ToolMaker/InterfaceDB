using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

internal enum RepositoryEventKind { Created, Changed, Deleted }
internal sealed class RepositoryEvent<T> where T : class, IStoredContract<T>
{
    internal readonly ICommittedEnvelopeSnapshot<T> Snapshot;
    internal readonly Task Released;
    internal readonly NotificationWriteContext.Origin? Origin;
    internal readonly long ByteLength;
    private readonly Lazy<IReadOnlyList<string>>? _changes;

    internal RepositoryEvent(ICommittedEnvelopeSnapshot<T> snapshot, ICommittedEnvelopeSnapshot<T>? before,
        NotificationComparison<T>? comparison, Task released, NotificationWriteContext.Origin? origin)
    {
        Snapshot = snapshot; Released = released; Origin = origin;
        ByteLength = (long)snapshot.ByteLength + (before?.ByteLength ?? 0);
        if (comparison is not null)
            _changes = new(() =>
            {
                NotificationWriteContext.Comparing.Value = true;
                try { return comparison.Changes(before!.Materialize(false), snapshot.Materialize(false)); }
                finally { NotificationWriteContext.Comparing.Value = false; }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // Executed once per committed event by a background dispatcher, never by the writer.
    internal IReadOnlyList<string> Changes() => _changes?.Value ?? Array.Empty<string>();
}

internal sealed class RepositoryNotificationSubscription<T> : IRepositorySubscription where T : class, IStoredContract<T>
{
    private readonly object _gate = new();
    private readonly Queue<RepositoryEvent<T>> _queue = new();
    private readonly TaskCompletionSource<RepositorySubscriptionEndReason> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _concurrency;
    private readonly int _capacity;
    private readonly int _byteCapacity;
    private readonly CancellationTokenSource _stop = new();
    private long _queuedBytes;
    private Action<RepositoryNotificationSubscription<T>>? _remove;
    private Func<T, bool>? _filter;
    private Func<T, IReadOnlyList<string>, ValueTask>? _handler;
    private Func<ValueTask<bool>>? _keepAlive;
    private CancellationTokenRegistration _registration;
    private TaskCompletionSource? _idle;
    private bool _running;
    private int _disposed;
    private long _delivered, _filtered;
    private readonly Guid _id = Guid.NewGuid();
    private readonly bool _receiveOwnWrites;
    private readonly int _maximumCausalDepth;

    internal RepositoryNotificationSubscription(RepositoryEventKind kind, Func<T, IReadOnlyList<string>, ValueTask> handler,
        Func<T, bool>? filter, Func<ValueTask<bool>>? keepAlive, RepositoryNotificationOptions options,
        SemaphoreSlim concurrency, Action<RepositoryNotificationSubscription<T>> remove, bool receiveOwnWrites)
    {
        Kind = kind; _handler = handler; _filter = filter; _keepAlive = keepAlive;
        _capacity = options.QueueCapacity; _byteCapacity = options.MaximumQueuedBytesPerSubscriber;
        _concurrency = concurrency; _remove = remove;
        _receiveOwnWrites = receiveOwnWrites; _maximumCausalDepth = options.MaximumCausalDepth;
    }

    internal RepositoryEventKind Kind { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public Task<RepositorySubscriptionEndReason> Completion => _completion.Task;
    public long DeliveredCount => Interlocked.Read(ref _delivered);
    public long FilteredCount => Interlocked.Read(ref _filtered);

    internal bool Accepts(NotificationWriteContext.Origin? origin)
        => !IsDisposed && (_receiveOwnWrites || origin?.Contains(_id) != true);

    public IDisposable BeginWriteScope()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if ((NotificationWriteContext.Current?.Depth ?? 0) >= _maximumCausalDepth)
            throw new InvalidOperationException("Notification write scope depth limit reached.");
        return NotificationWriteContext.Enter(_id, NotificationWriteContext.Current);
    }

    internal void SetLifetime(CancellationToken lifetime)
    {
        var registration = lifetime.UnsafeRegister(static state =>
            ((RepositoryNotificationSubscription<T>)state!).Stop(RepositorySubscriptionEndReason.Cancelled), this);
        lock (_gate)
        {
            if (!IsDisposed) { _registration = registration; return; }
        }
        registration.Unregister();
    }

    internal void Enqueue(RepositoryEvent<T> item)
    {
        var start = false;
        lock (_gate)
        {
            if (!Accepts(item.Origin)) return;
            if (_queue.Count < _capacity && _queuedBytes + item.ByteLength <= _byteCapacity)
            {
                _queue.Enqueue(item);
                _queuedBytes += item.ByteLength;
                if (!_running) { _running = true; start = true; }
            }
            else { Stop(RepositorySubscriptionEndReason.QueueOverflow); return; }
        }
        if (start) _ = Task.Run(DispatchAsync);
    }

    private async Task DispatchAsync()
    {
        while (true)
        {
            RepositoryEvent<T> item;
            Func<T, IReadOnlyList<string>, ValueTask>? handler;
            Func<T, bool>? filter;
            Func<ValueTask<bool>>? keepAlive;
            lock (_gate)
            {
                if (IsDisposed || !_queue.TryDequeue(out item!))
                {
                    _running = false; _idle?.TrySetResult(); _idle = null; return;
                }
                _queuedBytes -= item.ByteLength;
            }
            await item.Released.ConfigureAwait(false);
            if (IsDisposed) continue;
            try { await _concurrency.WaitAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { continue; }
            try
            {
                lock (_gate)
                {
                    if (IsDisposed) continue;
                    handler = _handler; filter = _filter; keepAlive = _keepAlive;
                }
                NotificationWriteContext.ExecutingSubscription.Value = this;
                if ((item.Origin?.Depth ?? 0) >= _maximumCausalDepth)
                { Stop(RepositorySubscriptionEndReason.CausalDepthExceeded); continue; }
                using var originScope = NotificationWriteContext.Enter(_id, item.Origin);
                if (keepAlive is not null && !await keepAlive().ConfigureAwait(false))
                { Stop(RepositorySubscriptionEndReason.KeepAliveExpired); continue; }
                if (IsDisposed) continue;
                IReadOnlyList<string> changes;
                T entity;
                try
                {
                    changes = item.Changes();
                    if (Kind == RepositoryEventKind.Changed && changes.Count == 0) continue;
                    entity = item.Snapshot.Materialize(attachEnvelope: Kind != RepositoryEventKind.Deleted);
                }
                catch { Stop(RepositorySubscriptionEndReason.ObservationFailed); continue; }
                if (filter is not null && !filter(entity)) { Interlocked.Increment(ref _filtered); continue; }
                if (IsDisposed) continue;
                await handler!(entity, changes).ConfigureAwait(false);
                Interlocked.Increment(ref _delivered);
            }
            catch { Stop(RepositorySubscriptionEndReason.CallbackFailed); }
            finally { NotificationWriteContext.ExecutingSubscription.Value = null; _concurrency.Release(); }
        }
    }

    public ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (NotificationWriteContext.ExecutingSubscription.Value is not null)
            throw new InvalidOperationException("Do not drain notification mailboxes from a callback; that can deadlock callback scheduling.");
        lock (_gate)
        {
            if (!_running) return ValueTask.CompletedTask;
            _idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(_idle.Task.WaitAsync(cancellationToken));
        }
    }

    public void Dispose() => Stop(RepositorySubscriptionEndReason.Disposed);

    internal void Stop(RepositorySubscriptionEndReason reason)
    {
        Action<RepositoryNotificationSubscription<T>>? remove;
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.Clear(); _queuedBytes = 0; _handler = null; _filter = null; _keepAlive = null;
            remove = _remove; _remove = null; registration = _registration; _registration = default;
        }
        registration.Unregister();
        _stop.Cancel();
        remove?.Invoke(this);
        _completion.TrySetResult(reason);
    }
}
