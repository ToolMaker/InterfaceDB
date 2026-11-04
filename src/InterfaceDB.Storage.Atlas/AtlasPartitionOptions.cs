using System.Collections.Immutable;
using System.Text;

namespace Idb.Storage.AtlasCore;

/// <summary>Explicit hybrid partition controls. Performance-driven adaptation and merging are not enabled by these options.</summary>
public sealed record AtlasPartitionOptions
{
    public int InitialPrefixBytes { get; init; } = 3;
    public int MaximumPrefixBytes { get; init; } = 64;
    /// <summary>Optional, immutable fixed root prefixes (UTF-8, exactly InitialPrefixBytes each).
    /// Open/Initialize pre-creates all owners without inserting entities. Unknown prefixes are
    /// rejected on write and no automatic or manual split can change this persisted layout.
    /// Requires EnableAutomaticSplitting=false. Empty retains the normal dynamic layout.</summary>
    public ImmutableArray<string> FixedPrefixes { get; init; } = [];
    public bool IsFixedLayout => !FixedPrefixes.IsDefaultOrEmpty;
    /// <summary>Includes historical routing parents. IDs are never reused.</summary>
    public int MaximumPartitions { get; init; } = 1_024;
    public bool EnableAutomaticSplitting { get; init; } = true;
    public int SplitAboveLiveRecords { get; init; } = 100_000;
    public long SplitAboveLiveBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>At least two children must meet this count; smaller additional children are still preserved.</summary>
    public int MinimumChildRecords { get; init; } = 16;
    public int CheckEveryMutations { get; init; } = 64;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumSplitRecords { get; init; } = 1_000_000;
    public long MaximumSplitBytes { get; init; } = 256L * 1024 * 1024;
    public TimeSpan MaximumSplitDuration { get; init; } = TimeSpan.FromSeconds(30);
    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (InitialPrefixBytes is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(InitialPrefixBytes));
        if (MaximumPrefixBytes < InitialPrefixBytes || MaximumPrefixBytes > 128) throw new ArgumentOutOfRangeException(nameof(MaximumPrefixBytes));
        if (MaximumPartitions is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaximumPartitions));
        if (FixedPrefixes.IsDefault) throw new ArgumentException("Use an empty array for a dynamic layout.", nameof(FixedPrefixes));
        if (IsFixedLayout)
        {
            if (EnableAutomaticSplitting) throw new ArgumentException("A fixed layout requires automatic splitting to be disabled.", nameof(EnableAutomaticSplitting));
            if (FixedPrefixes.Length > MaximumPartitions) throw new ArgumentException("Fixed prefixes exceed partition capacity.", nameof(FixedPrefixes));
            var encoding = new UTF8Encoding(false, true);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prefix in FixedPrefixes)
            {
                if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > InitialPrefixBytes
                    || encoding.GetByteCount(prefix) != InitialPrefixBytes || !unique.Add(prefix))
                    throw new ArgumentException("Fixed prefixes must be distinct, valid UTF-8 prefixes of exactly InitialPrefixBytes bytes.", nameof(FixedPrefixes));
            }
        }
        if (SplitAboveLiveRecords < 1 || SplitAboveLiveBytes < 1) throw new ArgumentOutOfRangeException(nameof(SplitAboveLiveRecords));
        if (MinimumChildRecords < 1 || MaximumSplitRecords < 1) throw new ArgumentOutOfRangeException(nameof(MinimumChildRecords));
        if (CheckEveryMutations is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(CheckEveryMutations));
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaximumSplitBytes < 1 || MinimumFreeSpaceReserveBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaximumSplitBytes));
        if (MaximumSplitDuration <= TimeSpan.Zero || MaximumSplitDuration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(MaximumSplitDuration));
    }

    internal string[] FixedPrefixHex() => FixedPrefixes.Select(p => Convert.ToHexString(Encoding.UTF8.GetBytes(p)))
        .Order(StringComparer.Ordinal).ToArray();
}

public sealed record AtlasPartitionInfo(int Id, int ParentId, string PrefixHex, int SplitDepth);
public readonly record struct AtlasPartitionSplitResult(bool Published, string Reason, int ParentId,
    int ChildrenCreated, long RecordsCopied, long BytesCopied, TimeSpan Elapsed);
