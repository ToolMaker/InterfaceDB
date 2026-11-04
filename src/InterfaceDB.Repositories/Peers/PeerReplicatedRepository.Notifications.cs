using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Commercial.Repositories.Peers;

public sealed partial class PeerReplicatedRepository<T> where T : class, IStoredContract<T>
{
    private static readonly AsyncLocal<CallbackScope?> Callback = new();
    private sealed class CallbackScope(PeerReplicatedRepository<T> owner)
    {
        internal PeerReplicatedRepository<T> Owner { get; } = owner;
        internal bool Active = true;
    }
    private async ValueTask ObserveAsync(Func<ValueTask> handler)
    {
        var previous = Callback.Value;
        var scope = new CallbackScope(this); Callback.Value = scope;
        try { await handler().ConfigureAwait(false); }
        finally { scope.Active = false; Callback.Value = previous; }
    }
    public IRepositorySubscription SubscribeCreated(Func<T, ValueTask> handler, Expression<Func<T, bool>>? filter = null,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => _notifications.SubscribeCreated(entity => ObserveAsync(() => handler(entity)), filter, lifetime, keepAlive, receiveOwnWrites);
    public IRepositorySubscription SubscribeChanged(Func<RepositoryChange<T>, ValueTask> handler, Expression<Func<T, bool>> filter,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => _notifications.SubscribeChanged(change => ObserveAsync(() => handler(change)), filter, lifetime, keepAlive, receiveOwnWrites);
    public IRepositorySubscription SubscribeDeleted(Func<T, ValueTask> handler, Expression<Func<T, bool>>? filter = null,
        CancellationToken lifetime = default, Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
        => _notifications.SubscribeDeleted(entity => ObserveAsync(() => handler(entity)), filter, lifetime, keepAlive, receiveOwnWrites);
}
