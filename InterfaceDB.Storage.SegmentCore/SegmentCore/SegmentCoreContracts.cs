using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

public enum SegmentCoreTransactionProtocol
{
    ParticipantCommit = 0,
    MutationWriteAheadLog = 1
}

public enum SegmentCorePrimaryIndexMode
{
    Resident = 0,
    Paged = 1,
    Automatic = 2
}

public sealed record SegmentCoreOptions
{
    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; init; }
    public Idb.Libraries.Abstractions.Options.RetainedVersionOptions? RetainedVersions { get; init; }
    public StripedObjectLogProbeOptions LogOptions { get; init; } = new()
    {
        StripeCount = 8,
        DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
    };

    public int CheckpointGenerationsToKeep { get; init; } = 2;

    /// <summary>
    /// Defers physical WAL replacement until at least this many prefix bytes can be reclaimed.
    /// Logical recovery remains checkpoint-bounded while small checkpoints avoid a file swap.
    /// </summary>
    public long WalRecycleMinimumReclaimBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// Forces a recycle attempt once the WAL reaches this size, even when the normal reclaim
    /// threshold has not been met. A non-advancing checkpoint watermark can still retain data.
    /// </summary>
    public long WalMaximumBytesBeforeForcedRecycle { get; init; } = 64L * 1024 * 1024;

    public SegmentCoreReadOptions ReadOptions { get; init; } = new();

    public SegmentCorePrimaryIndexOptions PrimaryIndexOptions { get; init; } = new();

    public SegmentCoreTransactionProtocol TransactionProtocol { get; init; } =
        SegmentCoreTransactionProtocol.ParticipantCommit;

    public IDurableFileSystem DurableFileSystem { get; init; } = PlatformDurableFileSystem.Instance;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(LogOptions);
        ArgumentNullException.ThrowIfNull(ReadOptions);
        ArgumentNullException.ThrowIfNull(PrimaryIndexOptions);
        ArgumentNullException.ThrowIfNull(DurableFileSystem);
        LogOptions.Validate();
        ReadOptions.Validate();
        PrimaryIndexOptions.Validate();
        if (CheckpointGenerationsToKeep is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CheckpointGenerationsToKeep),
                "Checkpoint generation retention must be between 1 and 16.");
        }

        if (WalRecycleMinimumReclaimBytes < 0
            || WalRecycleMinimumReclaimBytes > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(WalRecycleMinimumReclaimBytes));
        }

        if (WalMaximumBytesBeforeForcedRecycle < FileHeaderMinimumBytes
            || WalMaximumBytesBeforeForcedRecycle > 64L * 1024 * 1024 * 1024
            || WalMaximumBytesBeforeForcedRecycle < WalRecycleMinimumReclaimBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(WalMaximumBytesBeforeForcedRecycle));
        }

        if (!Enum.IsDefined(TransactionProtocol))
        {
            throw new ArgumentOutOfRangeException(nameof(TransactionProtocol));
        }
    }

    private const long FileHeaderMinimumBytes = 64;
}

/// <summary>
/// Controls whether checkpoint locators are materialized as strings and hash buckets at reopen,
/// or retained as a validated on-disk lookup table and read on demand.
/// </summary>
public sealed record SegmentCorePrimaryIndexOptions
{
    public SegmentCorePrimaryIndexMode Mode { get; init; } = SegmentCorePrimaryIndexMode.Automatic;

    public int AutomaticPagingMinimumEntries { get; init; } = 100_000;

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (AutomaticPagingMinimumEntries is < 1_000 or > 100_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(AutomaticPagingMinimumEntries));
        }
    }
}

/// <summary>
/// Controls physical read coalescing and parallelism without changing the on-disk format.
/// </summary>
public sealed record SegmentCoreReadOptions
{
    public int MaximumParallelReads { get; init; } = 8;

    public int MaximumReadWindowBytes { get; init; } = 4 * 1024 * 1024;

    public int MaximumReadGapBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum frame size eligible for an allocation-light synchronous random read.
    /// Local point reads below this threshold normally complete faster than scheduling
    /// an overlapped operation. Set to zero to force asynchronous point reads.
    /// </summary>
    public int SynchronousPointReadMaximumBytes { get; init; } = 64 * 1024;

    public int StreamingBatchRecords { get; init; } = 4_096;

    internal void Validate()
    {
        if (MaximumParallelReads is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumParallelReads));
        }

        if (MaximumReadWindowBytes is < 4 * 1024 or > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReadWindowBytes));
        }

        if (MaximumReadGapBytes < 0 || MaximumReadGapBytes > MaximumReadWindowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReadGapBytes));
        }

        if (SynchronousPointReadMaximumBytes < 0
            || SynchronousPointReadMaximumBytes > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(SynchronousPointReadMaximumBytes));
        }

        if (StreamingBatchRecords is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(nameof(StreamingBatchRecords));
        }
    }
}

public readonly record struct SegmentCoreCheckpointResult(
    long Generation,
    int Entries,
    long Bytes,
    TimeSpan Elapsed);

public sealed record SegmentCoreBackupOptions
{
    public bool VerifyAfterWrite { get; init; } = true;
}

/// <summary>
/// Admission limits applied before a SegmentCore backup is hashed or restored.
/// </summary>
public sealed record SegmentCoreRestoreOptions
{
    /// <summary>Maximum serialized backup-manifest size.</summary>
    public long MaximumManifestBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Maximum number of manifest-listed store files.</summary>
    public int MaximumFiles { get; init; } = 100_000;

    /// <summary>Maximum aggregate size of all manifest-listed store files.</summary>
    public long MaximumTotalBytes { get; init; } = 1024L * 1024 * 1024 * 1024;

    /// <summary>Maximum size of any one manifest-listed store file.</summary>
    public long MaximumFileBytes { get; init; } = 256L * 1024 * 1024 * 1024;

    /// <summary>Maximum wall-clock duration for manifest validation, copying, and verification.</summary>
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Whether restore must reserve destination capacity before staging begins.</summary>
    public bool RequireFreeSpaceAdmission { get; init; } = true;

    /// <summary>Free space that must remain after all manifest-listed bytes are staged.</summary>
    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum relative path depth accepted from the manifest. The current SegmentCore backup
    /// layout uses at most four components.
    /// </summary>
    public int MaximumPathDepth { get; init; } = 4;

    internal void Validate()
    {
        if (MaximumManifestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumManifestBytes));
        }

        if (MaximumFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFiles));
        }

        if (MaximumTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        }

        if (MaximumFileBytes <= 0 || MaximumFileBytes > MaximumTotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        }

        if (MinimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceReserveBytes));
        }

        if (MaximumPathDepth is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPathDepth));
        }
    }
}

public readonly record struct SegmentCoreBackupResult(
    string BackupDirectory,
    long CheckpointGeneration,
    long TransactionWatermark,
    int Entries,
    int Files,
    long Bytes,
    TimeSpan Elapsed);

public readonly record struct SegmentCoreRestoreResult(
    string StoreDirectory,
    long CheckpointGeneration,
    int Entries,
    int Files,
    long Bytes,
    TimeSpan Elapsed);

public sealed record SegmentCoreRecord(string Key, byte[] Payload);

internal readonly record struct SegmentCoreSearchIndexStorageIdentity(
    int EntryCount,
    byte[] Fingerprint);

public readonly record struct SegmentCoreCompactionResult(
    int LiveEntries,
    long FramesCopied,
    long BytesBefore,
    long BytesAfter,
    long BytesReclaimed,
    TimeSpan Elapsed)
{
    public int Slices { get; init; }

    public TimeSpan LongestExclusiveSlice { get; init; }

    public TimeSpan PreparationPause { get; init; }

    public TimeSpan LongestCopySlice { get; init; }

    public TimeSpan LongestCheckpointCapturePause { get; init; }

    public TimeSpan FinalizationPause { get; init; }
    public long BytesCopied { get; init; }
    public int FilesSelected { get; init; }
    public int FilesReclaimed { get; init; }
    public int DeletionFailures { get; init; }
    public long RetiredFileBytes { get; init; }
    public long RetainedBytesCopied { get; init; }
    public bool IsPartial { get; init; }
}

public enum SegmentCoreCompactionMode { SlicedAppend, IndependentOutput }

public sealed record SegmentCoreCompactionOptions
{
    /// <summary>IndependentOutput opts into a backward-incompatible storage capability marker;
    /// it copies sealed files without holding the foreground mutation gate during I/O.</summary>
    public SegmentCoreCompactionMode Mode { get; init; }
    public Idb.Libraries.Abstractions.Options.CompactionSelectionOptions Selection { get; init; } = new();
    public Idb.Libraries.Abstractions.Services.Maintenance.IMaintenanceBudget? MaintenanceBudget { get; init; }
    public int MaximumCopyConcurrency { get; init; } = 1;
    public int CopyBufferBytes { get; init; } = 64 * 1024;
    public int MaximumMetadataEntries { get; init; } = 1_000_000;
    public int MaximumRecordsPerSlice { get; init; } = 64;

    public int MaximumStripesPerPreparationSlice { get; init; } = 1;

    public TimeSpan DelayBetweenSlices { get; init; } = TimeSpan.Zero;

    public bool RequireFreeSpaceAdmission { get; init; } = true;

    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        ArgumentNullException.ThrowIfNull(Selection); Selection.Validate();
        if (MaximumCopyConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumCopyConcurrency));
        if (CopyBufferBytes is < 128 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(CopyBufferBytes));
        if (MaximumMetadataEntries is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumMetadataEntries));
        if (MaintenanceBudget is { MaximumWorkUnitBytes: < 128 })
            throw new ArgumentException("Segment Core copy admission requires a work unit of at least 128 bytes.", nameof(MaintenanceBudget));
        if (Mode == SegmentCoreCompactionMode.SlicedAppend && MaintenanceBudget is not null)
            throw new ArgumentException("A maintenance I/O budget requires IndependentOutput; the legacy path copies under mutation exclusion.", nameof(MaintenanceBudget));
        if (MaximumRecordsPerSlice is < 1 or > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRecordsPerSlice),
                "The compaction slice size must be between 1 and 65,536 records.");
        }

        if (MaximumStripesPerPreparationSlice is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumStripesPerPreparationSlice));
        }

        if (DelayBetweenSlices < TimeSpan.Zero || DelayBetweenSlices > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DelayBetweenSlices),
                "The delay between compaction slices must be between zero and five seconds.");
        }

        if (MinimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceReserveBytes));
        }
    }
}

public readonly record struct SegmentCoreIntegrityResult(
    bool IsValid,
    int EntriesChecked,
    long PayloadBytesChecked,
    long CheckpointGeneration,
    TimeSpan Elapsed,
    IReadOnlyList<string> Failures);

public readonly record struct SegmentCoreStatistics(
    int LiveEntries,
    long CheckpointGeneration,
    bool CheckpointInProgress,
    int TailRecordsReplayed,
    long FullRecoveryFramesScanned,
    long RepairedTailBytes,
    StripedObjectLogStatistics LogStatistics,
    long CommittedTransactions,
    long RolledBackTransactions,
    long FaultedTransactions)
{
    public long WalRecoveryPrefixBytesSkipped { get; init; }

    public bool PrimaryIndexIsPaged { get; init; }

    public int DirtyStripes { get; init; }

    public int PendingCheckpointChanges { get; init; }

    public bool CompactionInProgress { get; init; }

    public TimeSpan LastCheckpointElapsed { get; init; }

    public TimeSpan LastCompactionElapsed { get; init; }
}
