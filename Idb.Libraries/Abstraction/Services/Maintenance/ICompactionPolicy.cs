namespace Idb.Libraries.Abstractions.Services.Maintenance;

/// <summary>Off-write-path policy only. The provider remains responsible for retention, readers,
/// transactions, free-space admission and crash-safe publication. Evaluate outside engine locks.</summary>
public interface ICompactionPolicy
{
    CompactionDecision Evaluate(in CompactionPolicyContext context);
}

public enum MaintenanceActivity { Unknown, Idle, Busy }
public enum CompactionMode { ThroughputFirst, IdleIncremental, SpaceFirst, ManualWindowed }
public enum CompactionDeferReason
{
    None, ManualOnly, MinimumInterval, InsufficientBenefit, ForegroundBusy, InsufficientWorkingSpace, AlreadyRunning
}

/// <summary>ReclaimableBytes excludes bytes protected by retention/recovery. Values may be conservative
/// estimates; a positive decision never substitutes for the engine's final safety checks.</summary>
public readonly record struct CompactionPolicyContext(
    long PhysicalBytes,
    long ReclaimableBytes,
    TimeSpan SinceLastCompletion,
    TimeSpan SinceLastAttempt,
    MaintenanceActivity Activity = MaintenanceActivity.Unknown,
    int ForegroundOutstanding = 0,
    bool ManualRequest = false,
    bool MaintenanceWindowOpen = false,
    bool HasWorkingSpace = true);

public readonly record struct CompactionDecision(bool ShouldRun, CompactionDeferReason Reason)
{
    public static CompactionDecision Run => new(true, CompactionDeferReason.None);
    public static CompactionDecision Defer(CompactionDeferReason reason) => new(false, reason);
}

public interface IMaintenanceActivitySource
{
    MaintenanceActivity Activity { get; }
    bool MaintenanceWindowOpen { get; }
}

/// <summary>Metadata-only approximate cost, obtained outside the write path. The probe must exclude
/// protected obsolete bytes from ReclaimableBytes and must not materialize application objects.</summary>
public readonly record struct CompactionEstimate(long PhysicalBytes, long ReclaimableBytes,
    int ForegroundOutstanding = 0, bool HasWorkingSpace = true);

/// <summary>Confirmed engine result. Never report an estimated reclaim value as actual reclamation.</summary>
public readonly record struct CompactionWorkResult(long RecordsCopied, long BytesCopied,
    long BytesReclaimed, long RetainedBytes = 0, bool IsPartial = false)
{
    /// <summary>Execution may defer after discovery/catch-up without publishing a replacement.
    /// Copy counters can still be nonzero for discarded work. No reclamation is implied.</summary>
    public bool Deferred { get; init; }
    public string? ReasonCode { get; init; }
}

public readonly record struct CompactionRunResult(CompactionDecision Decision, CompactionWorkResult? Work = null);

/// <summary>Bounded metadata supplied off the write path. LiveBytes and ProtectedObsoleteBytes must
/// describe disjoint physical bytes. Id is an opaque provider identity, not an object key.</summary>
public readonly record struct CompactionCandidate(
    string Id, long PhysicalBytes, long LiveBytes, long ProtectedObsoleteBytes, bool IsSealed)
{
    public long RequiredBytes => checked(LiveBytes + ProtectedObsoleteBytes);
    public long ReclaimableBytes => Math.Max(0, PhysicalBytes - RequiredBytes);
}
