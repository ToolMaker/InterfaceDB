using System;
using System.Threading;
using System.Threading.Tasks;

namespace Idb.Libraries.Abstractions.Services.PubSub;

/// <summary>
/// Publishes notifications to subscribed handlers.
/// </summary>
/// <typeparam name="TEvent">Event payload type.</typeparam>
public interface IPubSubButton<TEvent>
{
    /// <summary>
    /// Registers a handler that should be invoked whenever an event is published.
    /// </summary>
    /// <param name="handler">Async handler invoked for each event.</param>
    /// <param name="keepAlive">
    /// Optional heartbeat delegate invoked periodically; returning <c>false</c> causes the subscription to be removed.
    /// </param>
    /// <returns>An <see cref="IPubSubSubscription"/> that can be disposed to unsubscribe.</returns>
    IPubSubSubscription Subscribe(
        Func<TEvent, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null);

    /// <summary>
    /// Publishes an event to all active subscribers.
    /// </summary>
    ValueTask PublishAsync(TEvent @event, CancellationToken cancellationToken = default);
}
