using Idb.Libraries.Abstractions.Services.PubSub;

namespace Idb.Libraries.Abstractions.Services.Maintenance;

public enum MaintenanceEventKind { CompactionCompleted, CompactionDeferred, CompactionFailed, CompactionCancelled }
public enum MaintenanceSubscriptionEndReason { Disposed, Cancelled, PublisherDisposed, CallbackFailed, QueueOverflow, CausalDepthExceeded }

/// <summary>In-process operational summary, never a logical entity mutation or durable replay record.
/// ReasonCode must be a bounded, non-sensitive code, not exception text or a filesystem path.
/// BytesReclaimed represents confirmed physical reclamation; copied/retained bytes are separate.</summary>
public readonly record struct MaintenanceNotification(
    Guid OperationId, MaintenanceEventKind Kind, TimeSpan Elapsed,
    long RecordsCopied = 0, long BytesCopied = 0, long BytesReclaimed = 0,
    long RetainedBytes = 0, string? ReasonCode = null, bool IsPartial = false);

public interface IMaintenanceNotifications
{
    IMaintenanceSubscription SubscribeMaintenance(Func<MaintenanceNotification, CancellationToken, ValueTask> handler,
        CancellationToken lifetime = default);
}

/// <summary>Completion means detached; it does not forcibly terminate an admitted callback.
/// Handlers receive cancellation on detachment. Slow handlers have bounded queues; overflow detaches.
/// A handler's maintenance descendants do not return to that same subscription.</summary>
public interface IMaintenanceSubscription : IContractSubscription
{
    Task<MaintenanceSubscriptionEndReason> Completion { get; }
    long DeliveredCount { get; }
    ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default);
}
