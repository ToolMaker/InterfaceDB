using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Libraries.Implementation.Maintenance;

/// <summary>Explicit application activity, not inferred subscriber liveness. May be shared across
/// schedulers; engine queue depth and retention safety are still checked independently.</summary>
public sealed class MaintenanceActivityHints : IMaintenanceActivitySource
{
    private int _activity, _window;
    public MaintenanceActivity Activity
    {
        get => (MaintenanceActivity)Volatile.Read(ref _activity);
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _activity, (int)value);
        }
    }
    public bool MaintenanceWindowOpen
    { get => Volatile.Read(ref _window) != 0; set => Volatile.Write(ref _window, value ? 1 : 0); }
}
