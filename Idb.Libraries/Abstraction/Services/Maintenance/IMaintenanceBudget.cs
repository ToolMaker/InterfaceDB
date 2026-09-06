namespace Idb.Libraries.Abstractions.Services.Maintenance;

/// <summary>Share one instance across participating stores on a device. Not an OS-wide limiter.
/// Acquire outside store locks, keep work units bounded, and account for both copied reads and writes.</summary>
public interface IMaintenanceBudget
{
    long MaximumWorkUnitBytes { get; }
    ValueTask<IDisposable> AcquireAsync(long ioBytes, CancellationToken cancellationToken = default);
    MaintenanceBudgetStatistics Statistics { get; }
}

public readonly record struct MaintenanceBudgetStatistics(
    int ActiveUnits, int WaitingUnits, long AdmittedUnits, long AdmittedIoBytes, TimeSpan AdmissionWait);
