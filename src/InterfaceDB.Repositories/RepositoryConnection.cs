using Idb.Libraries.Repositories;
namespace Idb.Commercial.Repositories;

public enum RepositoryConnectionState { Starting, Reconnecting, Synchronizing, Online, Offline, Faulted, Disposed }

/// <summary>Client-owned observable connection state. Read preference remains configured independently.</summary>
public sealed class RepositoryConnection : IRepositoryChangeNotifications, IDisposable
{
    private readonly RepositoryChangePublisher _changes = new();
    private readonly object _gate = new();
    private RepositoryConnectionState _state;
    private Exception? _failure;
    public RepositoryConnectionState State { get { lock (_gate) return _state; } }
    public Exception? LastFailure { get { lock (_gate) return _failure; } }
    public bool IsDowntime
    {
        get { lock (_gate) return _state == RepositoryConnectionState.Offline
            || _state == RepositoryConnectionState.Reconnecting && _failure is not null; }
    }
    public IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default)
        => _changes.SubscribeChanges(handler, lifetime);
    internal void Set(RepositoryConnectionState state, Exception? failure = null)
    {
        lock (_gate)
        {
            if (_state == RepositoryConnectionState.Disposed) return;
            if (state == RepositoryConnectionState.Reconnecting && failure is null
                && _state is RepositoryConnectionState.Offline or RepositoryConnectionState.Reconnecting)
                failure = _failure;
            if (_state == state && ReferenceEquals(_failure, failure)) return;
            _state = state; _failure = failure;
        }
        _changes.PublishChanged();
    }
    public void Dispose() { Set(RepositoryConnectionState.Disposed); _changes.Dispose(); }
}
