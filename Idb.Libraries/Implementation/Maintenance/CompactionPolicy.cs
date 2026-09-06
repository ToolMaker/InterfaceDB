using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Libraries.Implementation.Maintenance;

public sealed class CompactionPolicy : ICompactionPolicy
{
    private readonly CompactionPolicyOptions _options;
    public CompactionPolicy(CompactionPolicyOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    public CompactionDecision Evaluate(in CompactionPolicyContext context)
    {
        if (context.PhysicalBytes < 0 || context.ReclaimableBytes < 0
            || context.ReclaimableBytes > context.PhysicalBytes || context.ForegroundOutstanding < 0
            || context.SinceLastAttempt < TimeSpan.Zero || context.SinceLastCompletion < TimeSpan.Zero
            || !Enum.IsDefined(context.Activity))
            throw new ArgumentOutOfRangeException(nameof(context));
        if (!context.HasWorkingSpace) return CompactionDecision.Defer(CompactionDeferReason.InsufficientWorkingSpace);
        // Explicit maintenance requests bypass scheduling, but not provider safety or resource admission.
        if (context.ManualRequest) return CompactionDecision.Run;
        if (_options.Mode == CompactionMode.ManualWindowed && !context.MaintenanceWindowOpen)
            return CompactionDecision.Defer(CompactionDeferReason.ManualOnly);
        if (context.SinceLastCompletion < _options.MinimumInterval || context.SinceLastAttempt < _options.RetryInterval)
            return CompactionDecision.Defer(CompactionDeferReason.MinimumInterval);
        if (context.ReclaimableBytes < _options.MinimumReclaimableBytes || context.PhysicalBytes == 0
            || (double)context.ReclaimableBytes / context.PhysicalBytes < _options.MinimumReclaimableRatio)
            return CompactionDecision.Defer(CompactionDeferReason.InsufficientBenefit);

        var busy = context.Activity == MaintenanceActivity.Busy
            || context.ForegroundOutstanding > _options.MaximumForegroundOutstanding;
        // Unknown is not idle. Explicit windows authorize cleanup without pretending load is absent.
        var waitForIdle = _options.Mode == CompactionMode.IdleIncremental
            && context.Activity != MaintenanceActivity.Idle && !context.MaintenanceWindowOpen;
        var canOverrideLoad = _options.Mode == CompactionMode.SpaceFirst
            || (_options.AllowBusyAfterMaximumDeferral && context.SinceLastCompletion >= _options.MaximumDeferral);
        if ((busy || waitForIdle) && !canOverrideLoad)
            return CompactionDecision.Defer(CompactionDeferReason.ForegroundBusy);
        return CompactionDecision.Run;
    }
}

/// <summary>Selects whole sealed candidates by estimated reclaimed bytes per copied byte.
/// This is selection, not permission to retire files. Providers revalidate each identity.</summary>
public static class CompactionCandidateSelector
{
    public static IReadOnlyList<CompactionCandidate> Select(IEnumerable<CompactionCandidate> candidates,
        CompactionSelectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        options ??= new(); options.Validate();
        var eligible = new List<CompactionCandidate>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var examined = 0;
        foreach (var candidate in candidates)
        {
            if (++examined > options.MaximumCandidatesExamined)
                throw new InvalidOperationException("Compaction candidate examination budget exceeded.");
            if (string.IsNullOrWhiteSpace(candidate.Id) || candidate.PhysicalBytes < 0 || candidate.LiveBytes < 0
                || candidate.ProtectedObsoleteBytes < 0 || candidate.LiveBytes > candidate.PhysicalBytes
                || candidate.ProtectedObsoleteBytes > candidate.PhysicalBytes - candidate.LiveBytes
                || !identities.Add(candidate.Id))
                throw new ArgumentException("Invalid or duplicate compaction candidate.", nameof(candidates));
            if (!candidate.IsSealed || candidate.PhysicalBytes == 0
                || candidate.ReclaimableBytes < options.MinimumReclaimableBytes
                || (double)candidate.ReclaimableBytes / candidate.PhysicalBytes < options.MinimumReclaimableRatio
                || candidate.RequiredBytes > options.MaximumCopyBytes) continue;
            eligible.Add(candidate);
        }
        eligible.Sort(static (a, b) =>
        {
            var score = ((double)b.ReclaimableBytes / Math.Max(1, b.RequiredBytes))
                .CompareTo((double)a.ReclaimableBytes / Math.Max(1, a.RequiredBytes));
            return score != 0 ? score : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        var selected = new List<CompactionCandidate>(Math.Min(options.MaximumCandidates, eligible.Count));
        long remaining = options.MaximumCopyBytes;
        foreach (var candidate in eligible)
        {
            if (candidate.RequiredBytes > remaining) continue;
            selected.Add(candidate); remaining -= candidate.RequiredBytes;
            if (selected.Count == options.MaximumCandidates) break;
        }
        return selected.AsReadOnly();
    }
}
