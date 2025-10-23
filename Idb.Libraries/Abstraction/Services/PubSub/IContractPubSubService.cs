namespace Idb.Libraries.Abstractions.Services.PubSub;

/// <summary>
/// Publishes notifications to subscribed handlers.
/// </summary>
/// <typeparam name="TContract">Event payload type.</typeparam>
public interface IContractPubSubService<TContract>
{
    /// <summary>
    /// Registers a handler that should be invoked whenever a contract is added.
    /// </summary>
    /// <param name="handler">Async handler invoked for each added contract.</param>
    /// <param name="keepAlive">
    /// Optional heartbeat delegate invoked periodically; returning <c>false</c> causes the subscription to be removed.
    /// </param>
    /// <returns>An <see cref="IContractSubscription"/> that can be disposed to unsubscribe.</returns>
    IContractSubscription SubscribeToAdded(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Registers a handler that should be invoked whenever a contract is removed.
    /// </summary>
    /// <param name="handler">Async handler invoked for each removed contract.</param>
    /// <param name="keepAlive">
    /// Optional heartbeat delegate invoked periodically; returning <c>false</c> causes the subscription to be removed.
    /// </param>
    /// <returns>An <see cref="IContractSubscription"/> that can be disposed to unsubscribe.</returns>
    IContractSubscription SubscribeToRemoved(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Registers a handler that should be invoked whenever a contract has been durably stored.
    /// </summary>
    IContractSubscription SubscribeToStored(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Registers a handler that should be invoked whenever a contract has been fully deleted from storage.
    /// </summary>
    IContractSubscription SubscribeToDeleted(
        Func<TContract, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Publishes an event to all active subscribers.
    /// </summary>
    ValueTask ContractAdded(TContract @event, CancellationToken cancellationToken = default);
    ValueTask ContractRemoved(TContract @event, CancellationToken cancellationToken = default);
    ValueTask ContractStored(TContract @event, CancellationToken cancellationToken = default);
    ValueTask ContractDeleted(TContract @event, CancellationToken cancellationToken = default);
}
