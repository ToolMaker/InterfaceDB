using System;

namespace Idb.Libraries.Abstractions.Services.PubSub;

/// <summary>
/// Represents an active subscription to a pub/sub channel.
/// Disposing the subscription unsubscribes the handler.
/// </summary>
public interface IContractSubscription : IDisposable
{
    /// <summary>
    /// True once the subscription has been disposed and the handler detached.
    /// </summary>
    bool IsDisposed { get; }
}
