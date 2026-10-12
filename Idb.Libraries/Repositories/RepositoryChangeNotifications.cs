using System.Threading.Channels;
using Idb.Libraries.Abstractions.Services.PubSub;

namespace Idb.Libraries.Repositories;

/// <summary>Coalesced current-state hints. Subscribe before reading a snapshot; rescan after reconnect.</summary>
public interface IRepositoryChangeNotifications
{
    IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default);
}

/// <summary>Disposal detaches the callback. Completion also reports source termination or observation failure.</summary>
public interface IRepositoryChangeSubscription : IContractSubscription, IAsyncDisposable
{
    /// <summary>Registration has completed. Await before taking the initial catch-up snapshot.</summary>
    Task Ready { get; }
    Task Completion { get; }
}

/// <summary>Bounded, disposable pub/sub for dataset invalidations, without retaining entity payloads.</summary>
public sealed class RepositoryChangePublisher : IRepositoryChangeNotifications, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private bool _disposed;
    /// <summary>True while this publisher invokes a callback in the current async flow.</summary>
    public bool IsDispatchingCallback => ReferenceEquals(Subscription.Current.Value?.Owner, this);

    public IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Subscription subscription;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscriptions.Count >= 1024) throw new InvalidOperationException("Subscriber limit reached.");
            subscription = new(this, handler, Remove);
            _subscriptions.Add(subscription);
        }
        subscription.SetLifetime(lifetime);
        return subscription;
    }

    private void Remove(Subscription subscription) { lock (_gate) _subscriptions.Remove(subscription); }

    public void PublishChanged()
    {
        lock (_gate)
            foreach (var subscription in _subscriptions) subscription.Signal();
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private sealed class Subscription : IRepositoryChangeSubscription
    {
        internal static readonly AsyncLocal<Subscription?> Current = new();
        internal readonly RepositoryChangePublisher Owner;
        private readonly Channel<byte> _queue = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<Subscription> _remove;
        private Func<ValueTask>? _handler;
        private CancellationTokenRegistration _registration;
        private readonly Task _worker;
        private int _disposed;
        internal Subscription(RepositoryChangePublisher owner, Func<ValueTask> handler, Action<Subscription> remove)
        { Owner = owner; _handler = handler; _remove = remove; _worker = Task.Run(DispatchAsync); }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public Task Ready => Task.CompletedTask;
        public Task Completion => _completion.Task;
        internal void Signal() => _queue.Writer.TryWrite(0);
        internal void SetLifetime(CancellationToken token)
        {
            _registration = token.UnsafeRegister(static state => ((Subscription)state!).Dispose(), this);
            if (IsDisposed) _registration.Unregister();
        }
        private async Task DispatchAsync()
        {
            try
            {
                await foreach (var _ in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (IsDisposed) break;
                    var callback = _handler;
                    if (callback is null) break;
                    Current.Value = this;
                    try { await callback().ConfigureAwait(false); }
                    finally { Current.Value = null; }
                }
            }
            catch (Exception exception) { Stop(exception); }
        }
        private void Stop(Exception? exception = null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _handler = null;
            _registration.Unregister();
            _queue.Writer.TryComplete();
            _remove(this);
            if (exception is null) _completion.TrySetResult(); else _completion.TrySetException(exception);
        }
        public void Dispose() => Stop();
        public async ValueTask DisposeAsync()
        {
            Dispose();
            if (!ReferenceEquals(Current.Value, this)) await _worker.ConfigureAwait(false);
        }
    }
}
