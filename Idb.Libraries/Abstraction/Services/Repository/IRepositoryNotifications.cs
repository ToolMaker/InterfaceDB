using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.PubSub;

namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>Optional in-process, non-durable observation of repository-owned mutations.</summary>
public interface IRepositoryNotifications<TContract> where TContract : class, IStoredContract<TContract>
{
    IRepositorySubscription SubscribeCreated(Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false);
    IRepositorySubscription SubscribeChanged(Func<RepositoryChange<TContract>, ValueTask> handler,
        Expression<Func<TContract, bool>> filter, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false);
    IRepositorySubscription SubscribeDeleted(Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false);
}

/// <summary>Isolated committed entity and top-level changed contract-property names.</summary>
public sealed record RepositoryChange<TContract>(TContract Entity, IReadOnlyList<string> ChangedProperties);

public enum RepositorySubscriptionEndReason
{
    Disposed, Cancelled, RepositoryDisposed, KeepAliveExpired, CallbackFailed, QueueOverflow, ObservationFailed, CausalDepthExceeded
}

/// <summary>Completion reports detachment, not forced termination of an in-flight callback.</summary>
public interface IRepositorySubscription : IContractSubscription
{
    /// <summary>Await before taking an initial snapshot. Writes admitted before registration have settled.</summary>
    Task Ready { get; }
    Task<RepositorySubscriptionEndReason> Completion { get; }
    long DeliveredCount { get; }
    long FilteredCount { get; }
    /// <summary>Marks writes in this async flow as originating from this subscriber. Such writes
    /// and their notification descendants do not return to this subscriber by default. Callback
    /// writes are marked automatically. Dispose using a lexical using scope; do not share scopes.</summary>
    IDisposable BeginWriteScope();
    /// <summary>Waits for the current mailbox to become idle. Do not await from a notification handler.</summary>
    ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default);
}
