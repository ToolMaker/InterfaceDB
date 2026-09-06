using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Repositories;

namespace Idb.Libraries.Implementation.Maintenance;

/// <summary>Optional bounded observation channel. Publish only AFTER provider locks are released.
/// Idle/empty hubs have no worker or timer. Callbacks are asynchronous and cannot roll back storage.
/// Per-subscription FIFO order is publication order, not a distributed ordering guarantee.</summary>
public sealed class MaintenanceNotificationHub : IMaintenanceNotifications, IDisposable
{
    private readonly object _gate = new();
    private readonly MaintenanceNotificationOptions _options;
    private readonly SemaphoreSlim _callbacks;
    private Subscription[] _subscriptions = [];
    private bool _disposed;

    public MaintenanceNotificationHub(MaintenanceNotificationOptions? options = null)
    {
        _options = options ?? new(); _options.Validate();
        _callbacks = new(_options.MaximumConcurrentCallbacks, _options.MaximumConcurrentCallbacks);
    }

    public bool HasSubscribers => Volatile.Read(ref _subscriptions).Length != 0;

    public IMaintenanceSubscription SubscribeMaintenance(
        Func<MaintenanceNotification, CancellationToken, ValueTask> handler, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Subscription subscription;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscriptions.Length >= _options.MaximumSubscribers)
                throw new InvalidOperationException("Maintenance subscriber limit reached.");
            subscription = new(this, handler);
            Volatile.Write(ref _subscriptions, [.. _subscriptions, subscription]);
        }
        subscription.SetLifetime(lifetime);
        return subscription;
    }

    public void Publish(in MaintenanceNotification notification)
    {
        var subscriptions = Volatile.Read(ref _subscriptions);
        if (subscriptions.Length == 0) return;
        if (!Enum.IsDefined(notification.Kind) || notification.Elapsed < TimeSpan.Zero
            || notification.RecordsCopied < 0 || notification.BytesCopied < 0 || notification.BytesReclaimed < 0
            || notification.RetainedBytes < 0 || notification.ReasonCode?.Length > 96)
            throw new ArgumentOutOfRangeException(nameof(notification));
        var origin = NotificationWriteContext.Current;
        foreach (var subscription in subscriptions) subscription.Enqueue(new(notification, origin));
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            Volatile.Write(ref _subscriptions, _subscriptions.Where(item => item != subscription).ToArray());
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; subscriptions = _subscriptions;
            Volatile.Write(ref _subscriptions, []);
        }
        foreach (var subscription in subscriptions) subscription.Stop(MaintenanceSubscriptionEndReason.PublisherDisposed);
        // Semaphore has no kernel wait handle. Admitted callbacks may still release it after detachment.
    }

    private readonly record struct Notice(MaintenanceNotification Value, NotificationWriteContext.Origin? Origin);

    private sealed class Subscription(MaintenanceNotificationHub owner,
        Func<MaintenanceNotification, CancellationToken, ValueTask> handler) : IMaintenanceSubscription
    {
        private MaintenanceNotificationHub? _owner = owner;
        private readonly SemaphoreSlim _callbacks = owner._callbacks;
        private readonly int _capacity = owner._options.QueueCapacityPerSubscriber;
        private readonly int _maximumCausalDepth = owner._options.MaximumCausalDepth;
        private readonly object _gate = new();
        private readonly Queue<Notice> _queue = new();
        private readonly Guid _id = Guid.NewGuid();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<MaintenanceSubscriptionEndReason> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<MaintenanceNotification, CancellationToken, ValueTask>? _handler = handler;
        private CancellationTokenRegistration _registration;
        private TaskCompletionSource? _idle;
        private bool _running;
        private int _disposed;
        private long _delivered;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public Task<MaintenanceSubscriptionEndReason> Completion => _completion.Task;
        public long DeliveredCount => Interlocked.Read(ref _delivered);

        internal void SetLifetime(CancellationToken lifetime)
        {
            var registration = lifetime.UnsafeRegister(static state =>
                ((Subscription)state!).Stop(MaintenanceSubscriptionEndReason.Cancelled), this);
            lock (_gate)
            {
                if (!IsDisposed) { _registration = registration; return; }
            }
            registration.Unregister();
        }

        internal void Enqueue(Notice notice)
        {
            bool start = false, overflow = false;
            lock (_gate)
            {
                if (IsDisposed || notice.Origin?.Contains(_id) == true) return;
                if (_queue.Count >= _capacity) overflow = true;
                else
                {
                    _queue.Enqueue(notice);
                    if (!_running) { _running = true; start = true; }
                }
            }
            if (overflow) Stop(MaintenanceSubscriptionEndReason.QueueOverflow);
            else if (start) _ = Task.Run(DispatchAsync);
        }

        private async Task DispatchAsync()
        {
            while (true)
            {
                Notice notice;
                lock (_gate)
                {
                    if (IsDisposed || !_queue.TryDequeue(out notice))
                    { _running = false; _idle?.TrySetResult(); _idle = null; return; }
                }
                try { await _callbacks.WaitAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { continue; }
                var previous = NotificationWriteContext.ExecutingSubscription.Value;
                try
                {
                    Func<MaintenanceNotification, CancellationToken, ValueTask>? callback;
                    lock (_gate) { if (IsDisposed) continue; callback = _handler; }
                    if ((notice.Origin?.Depth ?? 0) >= _maximumCausalDepth)
                    { Stop(MaintenanceSubscriptionEndReason.CausalDepthExceeded); continue; }
                    NotificationWriteContext.ExecutingSubscription.Value = this;
                    using var scope = NotificationWriteContext.Enter(_id, notice.Origin);
                    await callback!(notice.Value, _stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _delivered);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch { Stop(MaintenanceSubscriptionEndReason.CallbackFailed); }
                finally
                {
                    NotificationWriteContext.ExecutingSubscription.Value = previous;
                    _callbacks.Release();
                }
            }
        }

        internal void Stop(MaintenanceSubscriptionEndReason reason)
        {
            CancellationTokenRegistration registration;
            lock (_gate)
            {
                if (IsDisposed) return;
                Volatile.Write(ref _disposed, 1); _handler = null; _queue.Clear();
                registration = _registration; _registration = default;
            }
            registration.Unregister();
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
            // Even cancellation callbacks are consumer code: do not run them on the maintenance caller.
            _ = CancelCallbacksAsync();
            _completion.TrySetResult(reason);
        }

        private async Task CancelCallbacksAsync()
        { try { await _stop.CancelAsync().ConfigureAwait(false); } catch (AggregateException) { } }

        public void Dispose() => Stop(MaintenanceSubscriptionEndReason.Disposed);

        public ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            if (NotificationWriteContext.ExecutingSubscription.Value is not null)
                throw new InvalidOperationException("A notification callback cannot await maintenance dispatch completion.");
            lock (_gate)
            {
                if (!_running) return ValueTask.CompletedTask;
                _idle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new(_idle.Task.WaitAsync(cancellationToken));
            }
        }
    }
}
