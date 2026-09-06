using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;

namespace Idb.Libraries.Abstractions.Options;

/// <summary>Explicitly constructed optional maintenance scheduling. No timer or background loop is
/// started by these options or the scheduler. Providers/applications own polling and shutdown.</summary>
public sealed record CompactionScheduleOptions
{
    public ICompactionPolicy Policy { get; init; } = new CompactionPolicy();
    public IMaintenanceActivitySource? ActivitySource { get; init; }
    public TimeSpan MinimumProbeInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public MaintenanceNotificationOptions Notifications { get; init; } = new();

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Policy);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentNullException.ThrowIfNull(Notifications);
        Notifications.Validate();
        if (MinimumProbeInterval < TimeSpan.Zero || MinimumProbeInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(MinimumProbeInterval));
    }
}
