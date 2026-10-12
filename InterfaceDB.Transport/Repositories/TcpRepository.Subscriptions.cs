using System.Collections.Concurrent;
using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Transport.Repositories;

public sealed partial class TcpRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    private readonly ConcurrentDictionary<long, ChangeSubscription> _changeSubscriptions = new();
    private long _nextSubscription;

    /// <summary>Registers an account-scoped push listener. The first hint requests catch-up after registration.</summary>
    public IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextSubscription);
        var subscription = new ChangeSubscription(this, id, handler, lifetime);
        _changeSubscriptions[id] = subscription;
        subscription.Start();
        if (Volatile.Read(ref _disposed) != 0) subscription.Dispose();
        return subscription;
    }

    private sealed class ChangeSubscription : IRepositoryChangeSubscription
    {
        private readonly TcpRepository<TContract> _owner;
        private readonly long _id;
        private readonly CancellationTokenSource _stop;
        private readonly RepositoryChangePublisher _publisher = new();
        private readonly IRepositoryChangeSubscription _delivery;
        private int _disposed;
        private readonly object _disposeGate = new();
        internal Task Drained { get; private set; } = Task.CompletedTask;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => _ready.Task;
        public Task Completion => Drained;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal ChangeSubscription(TcpRepository<TContract> owner, long id, Func<ValueTask> handler, CancellationToken lifetime)
        {
            _owner = owner; _id = id;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _delivery = _publisher.SubscribeChanges(handler, _stop.Token);
        }
        internal void Start() => Drained = Task.Run(RunAsync);
        private async Task RunAsync()
        {
            try
            {
                await using var connection = await TcpEncodedEnvelopeStore.ConnectCoreAsync(_owner._options.Connection with
                {
                    TokenProvider = _owner._tokens,
                    RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate
                        | EncodedStoreCapabilities.DurableCommits | EncodedStoreCapabilities.SubjectIsolation
                }, true, _stop.Token).ConfigureAwait(false);
                _ready.TrySetResult();
                // Acknowledge-before-snapshot ordering closes the registration/snapshot race.
                _publisher.PublishChanged();
                var receive = connection.ReadChangesAsync(_publisher.PublishChanged, _stop.Token);
                var completed = await Task.WhenAny(receive, _delivery.Completion).ConfigureAwait(false);
                if (completed == _delivery.Completion)
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                    try { await receive.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    await _delivery.Completion.ConfigureAwait(false);
                }
                else await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _ready.TrySetCanceled(); }
            catch (Exception exception) { _ready.TrySetException(exception); throw; }
            finally
            {
                Dispose();
                _owner._changeSubscriptions.TryRemove(_id, out _);
                lock (_disposeGate) _stop.Dispose();
            }
        }
        public void Dispose()
        {
            lock (_disposeGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _stop.Cancel();
                _publisher.Dispose();
            }
        }
        public async ValueTask DisposeAsync()
        {
            Dispose();
            try { await Drained.ConfigureAwait(false); }
            finally { await _delivery.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
