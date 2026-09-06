using Idb.Storage.SegmentCore.ObjectLog;
using Idb.Storage.SegmentCore;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// Production defaults and maintenance controls for the InterfaceDB SegmentCore provider.
/// </summary>
public sealed record SegmentCoreRepositoryOptions
{
    /// <summary>
    /// Profile used to seed these concrete settings. Every nested option remains available for overrides.
    /// </summary>
    public StorageHardwareProfile HardwareProfile { get; init; } = StorageHardwareProfile.Portable;

    public string DirectoryName { get; init; } = ".segment-core";

    public SegmentCoreOptions StoreOptions { get; init; } = new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 8,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(1);

    public int MinimumMutationsPerCheckpoint { get; init; } = 1_000;

    public TimeSpan CompactionInterval { get; init; } = TimeSpan.FromMinutes(15);

    public int MinimumMutationsPerCompaction { get; init; } = 100_000;

    public SegmentCoreCompactionOptions CompactionOptions { get; init; } = new();

    /// <summary>Opt-in policy replaces the legacy compaction interval/mutation admission. Polling
    /// uses CheckpointInterval; checkpoint safety remains independent. Null keeps legacy scheduling.
    /// Merely subscribing to maintenance summaries does not opt into this scheduling policy.</summary>
    public CompactionScheduleOptions? CompactionSchedule { get; init; }

    public bool EnableBackgroundMaintenance { get; init; } = true;

    public bool CheckpointOnDispose { get; init; } = true;

    /// <summary>
    /// Creates a complete, inspectable SegmentCore configuration for a hardware profile.
    /// </summary>
    public static SegmentCoreRepositoryOptions ForHardwareProfile(StorageHardwareProfile profile) =>
        profile switch
        {
            StorageHardwareProfile.Portable => new SegmentCoreRepositoryOptions(),
            StorageHardwareProfile.LowMemory => new SegmentCoreRepositoryOptions
            {
                HardwareProfile = profile,
                MinimumMutationsPerCheckpoint = 500,
                MinimumMutationsPerCompaction = 25_000,
                StoreOptions = CreateStoreOptions(
                    stripeCount: 2,
                    maximumBatchRecords: 32,
                    queueCapacityPerStripe: 256,
                    maxSegmentBytes: 16L * 1024 * 1024,
                    maximumCombinedWriteBytes: 256 * 1024,
                    durabilityStrategy: ObjectLogDurabilityStrategy.ExplicitFlush,
                    maximumParallelReads: 2,
                    maximumReadWindowBytes: 1024 * 1024,
                    maximumReadGapBytes: 16 * 1024),
                CompactionOptions = new SegmentCoreCompactionOptions
                {
                    MaximumRecordsPerSlice = 32,
                    MaximumStripesPerPreparationSlice = 1,
                    MinimumFreeSpaceReserveBytes = 16L * 1024 * 1024
                }
            },
            StorageHardwareProfile.HighThroughput => new SegmentCoreRepositoryOptions
            {
                HardwareProfile = profile,
                CheckpointInterval = TimeSpan.FromMinutes(2),
                MinimumMutationsPerCheckpoint = 10_000,
                CompactionInterval = TimeSpan.FromMinutes(30),
                MinimumMutationsPerCompaction = 250_000,
                StoreOptions = CreateStoreOptions(
                    stripeCount: 8,
                    maximumBatchRecords: 256,
                    queueCapacityPerStripe: 4_096,
                    maxSegmentBytes: 256L * 1024 * 1024,
                    maximumCombinedWriteBytes: 4 * 1024 * 1024,
                    durabilityStrategy: ObjectLogDurabilityStrategy.ExplicitFlush,
                    maximumParallelReads: 16,
                    maximumReadWindowBytes: 8 * 1024 * 1024,
                    maximumReadGapBytes: 128 * 1024),
                CompactionOptions = new SegmentCoreCompactionOptions
                {
                    MaximumRecordsPerSlice = 256,
                    MaximumStripesPerPreparationSlice = 1,
                    MinimumFreeSpaceReserveBytes = 512L * 1024 * 1024
                }
            },
            StorageHardwareProfile.WindowsNvme => new SegmentCoreRepositoryOptions
            {
                HardwareProfile = profile,
                StoreOptions = CreateStoreOptions(
                    stripeCount: 8,
                    maximumBatchRecords: 64,
                    queueCapacityPerStripe: 1_024,
                    maxSegmentBytes: 64L * 1024 * 1024,
                    maximumCombinedWriteBytes: 1024 * 1024,
                    durabilityStrategy: ObjectLogDurabilityStrategy.WriteThrough,
                    maximumParallelReads: 8,
                    maximumReadWindowBytes: 4 * 1024 * 1024,
                    maximumReadGapBytes: 64 * 1024)
            },
            StorageHardwareProfile.Custom => new SegmentCoreRepositoryOptions
            {
                HardwareProfile = profile
            },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

    internal static SegmentCoreRepositoryOptions Resolve(
        StoragePolicy storagePolicy,
        SegmentCoreRepositoryOptions? explicitOptions)
    {
        ArgumentNullException.ThrowIfNull(storagePolicy);
        if (explicitOptions is not null)
        {
            explicitOptions.Validate();
            return explicitOptions;
        }

        if (storagePolicy.HardwareProfile == StorageHardwareProfile.Custom)
        {
            throw new InvalidOperationException(
                "The Custom storage hardware profile requires explicit SegmentCoreRepositoryOptions.");
        }

        var resolved = ForHardwareProfile(storagePolicy.HardwareProfile);
        resolved = resolved with
        {
            StoreOptions = resolved.StoreOptions with
            {
                ReadOptions = resolved.StoreOptions.ReadOptions with
                {
                    MaximumParallelReads = Math.Min(
                        resolved.StoreOptions.ReadOptions.MaximumParallelReads,
                        Math.Clamp(storagePolicy.MaxReadConcurrency, 1, 256))
                }
            }
        };
        resolved.Validate();
        return resolved;
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(HardwareProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(HardwareProfile));
        }

        if (string.IsNullOrWhiteSpace(DirectoryName)
            || Path.IsPathRooted(DirectoryName)
            || DirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The SegmentCore directory name must be a valid relative directory name.",
                nameof(DirectoryName));
        }

        ArgumentNullException.ThrowIfNull(StoreOptions);
        ArgumentNullException.ThrowIfNull(CompactionOptions);
        StoreOptions.Validate();
        CompactionOptions.Validate();
        CompactionSchedule?.Validate();
        if (CheckpointInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CheckpointInterval));
        }

        if (CompactionInterval < CheckpointInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompactionInterval),
                "Compaction interval cannot be shorter than the checkpoint interval.");
        }

        if (MinimumMutationsPerCheckpoint < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumMutationsPerCheckpoint));
        }

        if (MinimumMutationsPerCompaction < MinimumMutationsPerCheckpoint)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumMutationsPerCompaction));
        }
    }

    private static SegmentCoreOptions CreateStoreOptions(
        int stripeCount,
        int maximumBatchRecords,
        int queueCapacityPerStripe,
        long maxSegmentBytes,
        int maximumCombinedWriteBytes,
        ObjectLogDurabilityStrategy durabilityStrategy,
        int maximumParallelReads,
        int maximumReadWindowBytes,
        int maximumReadGapBytes) => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = stripeCount,
            MaximumBatchRecords = maximumBatchRecords,
            QueueCapacityPerStripe = queueCapacityPerStripe,
            MaxSegmentBytes = maxSegmentBytes,
            MaximumCombinedWriteBytes = maximumCombinedWriteBytes,
            DurabilityStrategy = durabilityStrategy
        },
        ReadOptions = new SegmentCoreReadOptions
        {
            MaximumParallelReads = maximumParallelReads,
            MaximumReadWindowBytes = maximumReadWindowBytes,
            MaximumReadGapBytes = maximumReadGapBytes
        }
    };
}

public readonly record struct SegmentCoreProviderStatus(
    bool IsInitialized,
    long MutationsSinceCheckpoint,
    long MutationsSinceCompaction,
    DateTimeOffset? LastCheckpointUtc,
    DateTimeOffset? LastCompactionUtc,
    Exception? LastMaintenanceError,
    SegmentCoreStatistics? StoreStatistics)
{
    public StorageHardwareProfile HardwareProfile { get; init; }
    public long CompactionObservationFailures { get; init; }
}

public sealed record SegmentCoreMigrationOptions
{
    public SegmentCoreOptions? SourceStoreOptions { get; init; }

    public int BatchSize { get; init; } = 256;

    public bool ContinueOnError { get; init; }

    public bool VerifyAfterMigration { get; init; } = true;

    internal void Validate()
    {
        SourceStoreOptions?.Validate();
        if (BatchSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        }
    }
}

public sealed record SegmentCoreMigrationFailure(string PhysicalKey, string Error);

public readonly record struct SegmentCoreMigrationResult(
    string SourceDirectory,
    string SourceVersion,
    string TargetVersion,
    int Examined,
    int Migrated,
    int AlreadyPresent,
    IReadOnlyList<SegmentCoreMigrationFailure> Failures,
    SegmentCoreCheckpointResult? Checkpoint,
    TimeSpan Elapsed);

public interface ISegmentCoreRepositoryMaintenance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    StoragePolicy StoragePolicy { get; }

    SegmentCoreRepositoryOptions Options { get; }

    SegmentCoreProviderStatus Status { get; }

    ValueTask<SegmentCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default);

    ValueTask<SegmentCoreCompactionResult> CompactAsync(
        CancellationToken cancellationToken = default);

    ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default);

    ValueTask<SegmentCoreBackupResult> BackupAsync(
        string destinationDirectory,
        SegmentCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<SegmentCoreMigrationResult> MigrateFromAsync(
        string sourceStoreDirectory,
        string sourceVersion,
        SegmentCoreMigrationOptions? options = null,
        CancellationToken cancellationToken = default);
}
