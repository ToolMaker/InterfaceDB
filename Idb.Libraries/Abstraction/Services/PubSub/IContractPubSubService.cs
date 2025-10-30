using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.PubSub;

/// <summary>
/// Publishes notifications to subscribed handlers.
/// </summary>
/// <typeparam name="TContract">Event payload type.</typeparam>
public interface IContractPubSubService<TContract, TLocationType>
    where TContract : class, IRepoDto<TContract, TLocationType>
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
        Func<IObjectEnvelope<TContract, TLocationType>, ValueTask> handler,
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
        Func<IObjectEnvelope<TContract, TLocationType>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Registers a handler that should be invoked whenever a contract has been durably stored.
    /// </summary>
    IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract, TLocationType>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Registers a handler that should be invoked whenever a contract has been fully deleted from storage.
    /// </summary>
    IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract, TLocationType>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Publishes an event to all active subscribers.
    /// </summary>
    ValueTask ContractAdded(IObjectEnvelope<TContract, TLocationType> @event, CancellationToken cancellationToken = default);
    ValueTask ContractRemoved(IObjectEnvelope<TContract, TLocationType> @event, CancellationToken cancellationToken = default);
    ValueTask ContractStored(IObjectEnvelope<TContract, TLocationType> @event, CancellationToken cancellationToken = default);
    ValueTask ContractDeleted(IObjectEnvelope<TContract, TLocationType> @event, CancellationToken cancellationToken = default);
}
