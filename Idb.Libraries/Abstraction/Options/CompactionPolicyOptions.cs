using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Libraries.Abstractions.Options;

/// <summary>Optional scheduling configuration; does not change existing providers unless explicitly
/// selected. Activity hints are advisory. Force-progress only overrides load deferral, never safety.</summary>
public sealed record CompactionPolicyOptions
{
    public CompactionMode Mode { get; init; } = CompactionMode.ThroughputFirst;
    public long MinimumReclaimableBytes { get; init; } = 4L * 1024 * 1024;
    public double MinimumReclaimableRatio { get; init; } = 0.20;
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumDeferral { get; init; } = TimeSpan.FromHours(6);
    public int MaximumForegroundOutstanding { get; init; }
    public bool AllowBusyAfterMaximumDeferral { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (MinimumReclaimableBytes < 1) throw new ArgumentOutOfRangeException(nameof(MinimumReclaimableBytes));
        if (!double.IsFinite(MinimumReclaimableRatio) || MinimumReclaimableRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumReclaimableRatio));
        if (MinimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinimumInterval));
        if (RetryInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RetryInterval));
        if (MaximumDeferral <= TimeSpan.Zero || MaximumDeferral < MinimumInterval)
            throw new ArgumentOutOfRangeException(nameof(MaximumDeferral));
        if (MaximumForegroundOutstanding < 0) throw new ArgumentOutOfRangeException(nameof(MaximumForegroundOutstanding));
    }
}

public sealed record CompactionSelectionOptions
{
    public int MaximumCandidates { get; init; } = 8;
    public int MaximumCandidatesExamined { get; init; } = 65_536;
    public long MaximumCopyBytes { get; init; } = 64L * 1024 * 1024;
    public long MinimumReclaimableBytes { get; init; } = 1024 * 1024;
    public double MinimumReclaimableRatio { get; init; } = 0.20;

    public void Validate()
    {
        if (MaximumCandidates is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumCandidates));
        if (MaximumCandidatesExamined < MaximumCandidates || MaximumCandidatesExamined > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumCandidatesExamined));
        if (MaximumCopyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaximumCopyBytes));
        if (MinimumReclaimableBytes < 1) throw new ArgumentOutOfRangeException(nameof(MinimumReclaimableBytes));
        if (!double.IsFinite(MinimumReclaimableRatio) || MinimumReclaimableRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumReclaimableRatio));
    }
}
