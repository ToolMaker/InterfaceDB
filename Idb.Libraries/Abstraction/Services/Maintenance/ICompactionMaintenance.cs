namespace Idb.Libraries.Abstractions.Services.Maintenance;

/// <summary>Optional local provider capability. Estimates are metadata-only and advisory. Manual
/// requests bypass scheduling preferences, never engine safety. No entity events are emitted for
/// physical relocation. A provider without segment cleanup (such as file-per-object) need not implement it.</summary>
public interface ICompactionMaintenance<TContract> : IMaintenanceNotifications where TContract : class
{
    ValueTask<CompactionEstimate> EstimateCompactionAsync(CancellationToken cancellationToken = default);
    ValueTask<CompactionRunResult> RunCompactionAsync(bool manual = false, CancellationToken cancellationToken = default);
}
