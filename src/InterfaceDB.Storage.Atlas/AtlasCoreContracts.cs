using Idb.Storage.SegmentCore.ObjectLog;
using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore;

public enum AtlasCoreDurabilityStrategy
{
    ExplicitFlush = 0,
    WriteThrough = 1
}

public enum AtlasCoreTransactionProtocol
{
    ParticipantCommitJournal = 0,
    MutationWriteAheadLog = 1
}

/// <summary>
/// Stable, provider-level controls for the AtlasCore physical store.
/// Placement details intentionally remain internal to the format.
/// </summary>
public sealed record AtlasCoreOptions
{
    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; init; }
    public AtlasPartitionOptions Partitioning { get; init; } = new();
    public int MaximumConcurrentPartitionWrites { get; init; } = 8;
    internal PartitionWriteScheduler? WriteScheduler { get; init; }
    internal Action<string>? SplitTestHook { get; init; }
    public AtlasCoreTransactionProtocol TransactionProtocol { get; init; } =
        AtlasCoreTransactionProtocol.MutationWriteAheadLog;

    // WAL format capacity is stable across deployment budget changes; actual allocation and
    // ownership are bounded by Partitioning.MaximumPartitions and the committed map.
    internal const int MaximumAddressablePartitions = 65_536;

    public long MaximumSegmentBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumBatchRecords { get; init; } = 64;

    public int QueueCapacityPerGroup { get; init; } = 1_024;

    public int MaximumCombinedWriteBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum encoded admission budget for one atomic transaction. It bounds the durable
    /// mutation-WAL record and also caps aggregate staged bytes for participant transactions.
    /// Each mutation must additionally fit the Atlas group-frame limit.
    /// </summary>
    public int MaximumTransactionLogRecordBytes { get; init; } = 512 * 1024 * 1024;

    /// <summary>Maximum distinct keys staged in one atomic transaction.</summary>
    public int MaximumTransactionMutations { get; init; } = 16_384;

    /// <summary>Maximum size of one recovery metadata file before it is read into memory.</summary>
    public int MaximumRecoveryMetadataBytes { get; init; } = 256 * 1024 * 1024;

    public TimeSpan GroupCommitDelay { get; init; } = TimeSpan.Zero;

    public AtlasCoreDurabilityStrategy DurabilityStrategy { get; init; } =
        AtlasCoreDurabilityStrategy.WriteThrough;

    public int CheckpointGenerationsToKeep { get; init; } = 2;

    public int MaximumParallelReads { get; init; } = 8;

    /// <summary>
    /// Maximum number of adjacent bytes read by one coalesced bulk-read operation.
    /// A single frame larger than this limit is still read in its own window.
    /// </summary>
    public int MaximumReadWindowBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum unused gap between adjacent frames that may be included in one read window.
    /// </summary>
    public int MaximumReadGapBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum number of live records materialized by each streaming read batch.
    /// </summary>
    public int StreamingBatchRecords { get; init; } = 4_096;

    /// <summary>
    /// Maximum number of cached segment file handles kept open by one AtlasCore store.
    /// The bound is shared by all segment groups and includes synchronous and asynchronous
    /// random-access handles. Active writer and transaction-log handles are not included.
    /// </summary>
    public int MaximumOpenReadHandles { get; init; } = 128;

    public int MaximumResolvedRouteCacheEntries { get; init; } = 65_536;

    internal IDurableFileSystem DurableFileSystem { get; init; } =
        PlatformDurableFileSystem.Instance;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(DurableFileSystem);

        ArgumentNullException.ThrowIfNull(Partitioning);
        Partitioning.Validate();
        if (MaximumConcurrentPartitionWrites is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentPartitionWrites));

        if (MaximumSegmentBytes < 4 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSegmentBytes));
        }

        if (MaximumBatchRecords is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBatchRecords));
        }

        if (QueueCapacityPerGroup is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(QueueCapacityPerGroup));
        }

        if (MaximumCombinedWriteBytes is < 4 * 1024 or > 256 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCombinedWriteBytes));
        }

        if (MaximumTransactionLogRecordBytes is < 1024 * 1024 or > 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTransactionLogRecordBytes));
        }

        if (MaximumTransactionMutations is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTransactionMutations));
        }

        if (MaximumRecoveryMetadataBytes < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRecoveryMetadataBytes));
        }

        if (GroupCommitDelay < TimeSpan.Zero || GroupCommitDelay > TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(GroupCommitDelay));
        }

        if (!Enum.IsDefined(DurabilityStrategy))
        {
            throw new ArgumentOutOfRangeException(nameof(DurabilityStrategy));
        }

        if (!Enum.IsDefined(TransactionProtocol))
        {
            throw new ArgumentOutOfRangeException(nameof(TransactionProtocol));
        }

        if (CheckpointGenerationsToKeep is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(CheckpointGenerationsToKeep));
        }

        if (MaximumParallelReads is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumParallelReads));
        }

        if (MaximumReadWindowBytes is < 4 * 1024 or > AtlasGroupLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReadWindowBytes));
        }

        if (MaximumReadGapBytes < 0 || MaximumReadGapBytes > MaximumReadWindowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReadGapBytes));
        }

        if (StreamingBatchRecords is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(StreamingBatchRecords));
        }

        if (MaximumOpenReadHandles is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOpenReadHandles));
        }

        if (MaximumResolvedRouteCacheEntries is < 0 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResolvedRouteCacheEntries));
        }

    }
}

public readonly record struct AtlasCoreCheckpointResult(
    int SegmentGroupsCheckpointed,
    long Entries,
    long Bytes,
    TimeSpan Elapsed);

public readonly record struct AtlasCoreStatistics(
    int ConfiguredSegmentGroups,
    int OpenSegmentGroups,
    long PlacementGeneration,
    int PlacementSplits,
    long PlacementPublicationFailures,
    long Saves,
    long Deletes,
    long PointReads)
{
    public int LogicalPartitionCount { get; init; }
    public int MaximumConcurrentPartitionWrites { get; init; }
    public int PeakConcurrentPartitionWrites { get; init; }
    public long CommittedTransactions { get; init; }

    public long RolledBackTransactions { get; init; }

    public long FaultedTransactions { get; init; }

    public int RetainedTransactionCommits { get; init; }

    public bool CompactionInProgress { get; init; }

    public long GroupSetGeneration { get; init; }

    public AtlasCoreTransactionProtocol TransactionProtocol { get; init; }

    public AtlasCoreDurabilityStrategy DurabilityStrategy { get; init; }

    public long TransactionLogBytes { get; init; }

    public long TransactionLogRecycledThrough { get; init; }

    public long OpenGroupEntries { get; init; }

    public long OpenGroupSegmentFiles { get; init; }

    public long OpenGroupPhysicalBytes { get; init; }

    public long RecoveryScannedFrames { get; init; }

    public long RepairedTailBytes { get; init; }

    public long ReplayedWalMutations { get; init; }

    public long MinimumOpenGroupCheckpointTransactionWatermark { get; init; }

    /// <summary>Currently open cached segment read handles.</summary>
    public int CurrentOpenReadHandles { get; init; }

    /// <summary>High-water mark for open cached segment read handles.</summary>
    public int PeakOpenReadHandles { get; init; }

    public long ReadHandleCacheHits { get; init; }

    public long ReadHandleCacheMisses { get; init; }

    public long ReadHandleCacheEvictions { get; init; }

    /// <summary>Total validated frames completed through coalesced bulk reads.</summary>
    public long BulkReadFrames { get; init; }

    /// <summary>Total physical read windows completed through coalesced bulk reads.</summary>
    public long BulkReadWindows { get; init; }

    /// <summary>Total physical bytes read by completed coalesced bulk-read windows.</summary>
    public long BulkReadBytes { get; init; }
}

public sealed record AtlasCoreRecord(string Key, byte[] Payload);

internal readonly record struct AtlasCoreSearchIndexStorageIdentity(
    int EntryCount,
    byte[] Fingerprint);

public readonly record struct AtlasCoreIntegrityResult(
    bool IsValid,
    int LiveEntriesChecked,
    long FramesChecked,
    long PayloadBytesChecked,
    TimeSpan Elapsed,
    IReadOnlyList<string> Failures);

public sealed record AtlasCoreBackupOptions
{
    public bool VerifyAfterWrite { get; init; } = true;
    public int MaximumFiles { get; init; } = 1_000_000;
    public long MaximumTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 1024L * 1024 * 1024 * 1024;
    public int MaximumDirectoryDepth { get; init; } = 12;
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromHours(2);
    public bool RequireFreeSpaceAdmission { get; init; } = true;
    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate() => AtlasCoreTransferAdmission.Validate(
        MaximumFiles, MaximumTotalBytes, MaximumFileBytes, MaximumDirectoryDepth, MaximumDuration,
        MinimumFreeSpaceReserveBytes);
}

public sealed record AtlasCoreRestoreOptions
{
    public int MaximumFiles { get; init; } = 1_000_000;
    public long MaximumTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 1024L * 1024 * 1024 * 1024;
    public int MaximumDirectoryDepth { get; init; } = 12;
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromHours(2);
    public bool RequireFreeSpaceAdmission { get; init; } = true;
    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate() => AtlasCoreTransferAdmission.Validate(
        MaximumFiles, MaximumTotalBytes, MaximumFileBytes, MaximumDirectoryDepth, MaximumDuration,
        MinimumFreeSpaceReserveBytes);
}

internal static class AtlasCoreTransferAdmission
{
    internal static void Validate(
        int maximumFiles,
        long maximumTotalBytes,
        long maximumFileBytes,
        int maximumDirectoryDepth,
        TimeSpan maximumDuration,
        long minimumFreeSpaceReserveBytes)
    {
        if (maximumFiles < 1) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (maximumTotalBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        if (maximumFileBytes < 1 || maximumFileBytes > maximumTotalBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (maximumDirectoryDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumDirectoryDepth));
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        if (minimumFreeSpaceReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumFreeSpaceReserveBytes));
    }
}

public readonly record struct AtlasCoreBackupResult(
    string BackupDirectory,
    int LiveEntries,
    int Files,
    long Bytes,
    TimeSpan Elapsed);

public readonly record struct AtlasCoreRestoreResult(
    string StoreDirectory,
    int LiveEntries,
    int Files,
    long Bytes,
    TimeSpan Elapsed);

public sealed record AtlasCoreCompactionOptions
{
    public int MaximumCopyConcurrency { get; init; } = 64;

    public int RecordsPerBatch { get; init; } = 4_096;

    public bool RequireFreeSpaceAdmission { get; init; } = true;

    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumCopyConcurrency is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCopyConcurrency));
        }

        if (RecordsPerBatch is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(RecordsPerBatch));
        }

        if (MinimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceReserveBytes));
        }
    }
}

public readonly record struct AtlasCoreCompactionResult(
    int LiveEntries,
    long FramesCopied,
    long BytesBefore,
    long BytesAfter,
    long BytesReclaimed,
    TimeSpan Elapsed)
{
    public long BytesCopied { get; init; }
}
