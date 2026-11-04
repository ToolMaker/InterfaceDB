using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Commercial.Repositories;

/// <summary>Bridges existing InterfaceDB Created/Changed/Deleted subscriptions into coalesced refresh hints.</summary>
internal sealed class RepositoryNotificationAdapter<T> : IRepositoryChangeSubscription where T : class, IStoredContract<T>
{
    private readonly IRepositorySubscription[] _subscriptions;
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public Task Ready { get; }
    public Task Completion { get; }
    internal RepositoryNotificationAdapter(IRepositoryNotifications<T> source, Func<ValueTask> handler, CancellationToken lifetime)
    {
        var subscriptions = new List<IRepositorySubscription>();
        try
        {
            subscriptions.Add(source.SubscribeCreated(_ => handler(), lifetime: lifetime));
            subscriptions.Add(source.SubscribeChanged(_ => handler(), _ => true, lifetime: lifetime));
            subscriptions.Add(source.SubscribeDeleted(_ => handler(), lifetime: lifetime));
            _subscriptions = [.. subscriptions];
        }
        catch { foreach (var item in subscriptions) item.Dispose(); throw; }
        Ready = Task.WhenAll(_subscriptions.Select(item => item.Ready));
        Completion = ObserveAsync();
    }
    private async Task ObserveAsync()
    {
        var ended = await Task.WhenAny(_subscriptions.Select(item => item.Completion)).ConfigureAwait(false);
        var reason = await ended.ConfigureAwait(false);
        var requested = IsDisposed;
        Dispose();
        if (!requested && reason != RepositorySubscriptionEndReason.Cancelled)
            throw new ResilientEndpointUnavailableException($"Repository observation ended: {reason}. Re-register and reconcile.");
    }
    internal IDisposable BeginWriteScope() => new Scopes(_subscriptions.Select(item => item.BeginWriteScope()).ToArray());
    private sealed class Scopes(IDisposable[] scopes) : IDisposable
    { public void Dispose() { for (var index = scopes.Length - 1; index >= 0; index--) scopes[index].Dispose(); } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var item in _subscriptions) item.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
