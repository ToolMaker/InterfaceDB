using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;

namespace Idb.Storage.AtlasCore;

/// <summary>
/// Initial defaults and maintenance controls for the commercial Atlas hybrid provider.
/// </summary>
public sealed record AtlasCoreRepositoryOptions
{
    /// <summary>
    /// Profile used to seed these concrete settings. Every nested option remains available for overrides.
    /// </summary>
    public StorageHardwareProfile HardwareProfile { get; init; } = StorageHardwareProfile.Portable;

    public string DirectoryName { get; init; } = ".atlas-core";

    public AtlasCoreOptions StoreOptions { get; init; } = new();

    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(1);

    public int MinimumMutationsPerCheckpoint { get; init; } = 1_000;

    public TimeSpan CompactionInterval { get; init; } = TimeSpan.FromMinutes(15);

    public int MinimumMutationsPerCompaction { get; init; } = 100_000;

    public AtlasCoreCompactionOptions CompactionOptions { get; init; } = new();

    /// <summary>Opt-in independent whole-set cleanup for scheduled/common maintenance. Existing CompactAsync remains the legacy manual fallback.</summary>
    public AtlasConcurrentCompactionOptions? ConcurrentCompaction { get; init; }
    public CompactionScheduleOptions? CompactionSchedule { get; init; }


    public bool EnableBackgroundMaintenance { get; init; } = true;

    public bool CheckpointOnDispose { get; init; } = true;

    /// <summary>
    /// Creates a complete, inspectable AtlasCore configuration for a hardware profile.
    /// </summary>
    public static AtlasCoreRepositoryOptions ForHardwareProfile(StorageHardwareProfile profile) =>
        profile switch
        {
            StorageHardwareProfile.Portable => new AtlasCoreRepositoryOptions(),
            StorageHardwareProfile.LowMemory => new AtlasCoreRepositoryOptions
            {
                HardwareProfile = profile,
                MinimumMutationsPerCheckpoint = 500,
                MinimumMutationsPerCompaction = 25_000,
                StoreOptions = CreateStoreOptions(
                    maximumConcurrentPartitionWrites: 2,
                    maximumBatchRecords: 32,
                    queueCapacityPerGroup: 256,
                    maximumSegmentBytes: 16L * 1024 * 1024,
                    maximumCombinedWriteBytes: 256 * 1024,
                    durabilityStrategy: AtlasCoreDurabilityStrategy.ExplicitFlush,
                    maximumParallelReads: 2,
                    maximumOpenReadHandles: 16,
                    maximumResolvedRouteCacheEntries: 16_384),
                CompactionOptions = new AtlasCoreCompactionOptions
                {
                    MaximumCopyConcurrency = 8,
                    RecordsPerBatch = 512,
                    MinimumFreeSpaceReserveBytes = 16L * 1024 * 1024
                }
            },
            StorageHardwareProfile.HighThroughput => new AtlasCoreRepositoryOptions
            {
                HardwareProfile = profile,
                CheckpointInterval = TimeSpan.FromMinutes(2),
                MinimumMutationsPerCheckpoint = 10_000,
                CompactionInterval = TimeSpan.FromMinutes(30),
                MinimumMutationsPerCompaction = 250_000,
                StoreOptions = CreateStoreOptions(
                    maximumConcurrentPartitionWrites: 16,
                    maximumBatchRecords: 256,
                    queueCapacityPerGroup: 4_096,
                    maximumSegmentBytes: 256L * 1024 * 1024,
                    maximumCombinedWriteBytes: 4 * 1024 * 1024,
                    durabilityStrategy: AtlasCoreDurabilityStrategy.ExplicitFlush,
                    maximumParallelReads: 16,
                    maximumOpenReadHandles: 512,
                    maximumResolvedRouteCacheEntries: 262_144),
                CompactionOptions = new AtlasCoreCompactionOptions
                {
                    MaximumCopyConcurrency = 128,
                    RecordsPerBatch = 8_192,
                    MinimumFreeSpaceReserveBytes = 512L * 1024 * 1024
                }
            },
            StorageHardwareProfile.WindowsNvme => new AtlasCoreRepositoryOptions
            {
                HardwareProfile = profile,
                StoreOptions = CreateStoreOptions(
                    maximumConcurrentPartitionWrites: 8,
                    maximumBatchRecords: 64,
                    queueCapacityPerGroup: 1_024,
                    maximumSegmentBytes: 64L * 1024 * 1024,
                    maximumCombinedWriteBytes: 1024 * 1024,
                    durabilityStrategy: AtlasCoreDurabilityStrategy.WriteThrough,
                    maximumParallelReads: 8,
                    maximumOpenReadHandles: 256,
                    maximumResolvedRouteCacheEntries: 65_536)
            },
            StorageHardwareProfile.Custom => new AtlasCoreRepositoryOptions
            {
                HardwareProfile = profile
            },
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

    internal static AtlasCoreRepositoryOptions Resolve(
        StoragePolicy storagePolicy,
        AtlasCoreRepositoryOptions? explicitOptions)
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
                "The Custom storage hardware profile requires explicit AtlasCoreRepositoryOptions.");
        }

        var resolved = ForHardwareProfile(storagePolicy.HardwareProfile);
        resolved = resolved with
        {
            StoreOptions = resolved.StoreOptions with
            {
                MaximumParallelReads = Math.Min(
                    resolved.StoreOptions.MaximumParallelReads,
                    Math.Clamp(storagePolicy.MaxReadConcurrency, 1, 256))
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
            || DirectoryName is "." or ".."
            || DirectoryName.Contains('/')
            || DirectoryName.Contains('\\')
            || DirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The AtlasCore directory name must be a valid relative directory name.",
                nameof(DirectoryName));
        }

        ArgumentNullException.ThrowIfNull(StoreOptions);
        ArgumentNullException.ThrowIfNull(CompactionOptions);
        StoreOptions.Validate();
        CompactionOptions.Validate();
        ConcurrentCompaction?.Validate(); CompactionSchedule?.Validate();
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

    private static AtlasCoreOptions CreateStoreOptions(
        int maximumConcurrentPartitionWrites,
        int maximumBatchRecords,
        int queueCapacityPerGroup,
        long maximumSegmentBytes,
        int maximumCombinedWriteBytes,
        AtlasCoreDurabilityStrategy durabilityStrategy,
        int maximumParallelReads,
        int maximumOpenReadHandles,
        int maximumResolvedRouteCacheEntries) => new()
    {
        MaximumConcurrentPartitionWrites = maximumConcurrentPartitionWrites,
        MaximumBatchRecords = maximumBatchRecords,
        QueueCapacityPerGroup = queueCapacityPerGroup,
        MaximumSegmentBytes = maximumSegmentBytes,
        MaximumCombinedWriteBytes = maximumCombinedWriteBytes,
        DurabilityStrategy = durabilityStrategy,
        CheckpointGenerationsToKeep = 2,
        MaximumParallelReads = maximumParallelReads,
        MaximumOpenReadHandles = maximumOpenReadHandles,
        MaximumResolvedRouteCacheEntries = maximumResolvedRouteCacheEntries
    };
}

public readonly record struct AtlasCoreProviderStatus(
    bool IsInitialized,
    long MutationsSinceCheckpoint,
    long MutationsSinceCompaction,
    DateTimeOffset? LastCheckpointUtc,
    DateTimeOffset? LastCompactionUtc,
    Exception? LastMaintenanceError,
    AtlasCoreStatistics? StoreStatistics)
{
    public StorageHardwareProfile HardwareProfile { get; init; }
}

public sealed record AtlasCoreMigrationOptions
{
    public AtlasCoreOptions? SourceStoreOptions { get; init; }

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

public sealed record AtlasCoreMigrationFailure(string PhysicalKey, string Error);

public readonly record struct AtlasCoreMigrationResult(
    string SourceDirectory,
    string SourceVersion,
    string TargetVersion,
    int Examined,
    int Migrated,
    int AlreadyPresent,
    IReadOnlyList<AtlasCoreMigrationFailure> Failures,
    AtlasCoreCheckpointResult? Checkpoint,
    TimeSpan Elapsed);

public interface IAtlasCoreRepositoryMaintenance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    StoragePolicy StoragePolicy { get; }

    AtlasCoreRepositoryOptions Options { get; }

    AtlasCoreProviderStatus Status { get; }

    /// <summary>Physical ownership only; keys passed here are envelope cache keys, not query/index values.</summary>
    IReadOnlyList<AtlasPartitionInfo> Partitions { get; }
    AtlasPartitionMaintenanceStatistics PartitionMaintenanceStatistics { get; }
    ValueTask FlushPartitionMaintenanceAsync(CancellationToken cancellationToken = default);
    ValueTask<AtlasPartitionSplitResult> SplitBucketAsync(string physicalKey, CancellationToken cancellationToken = default);

    ValueTask<AtlasCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AtlasCoreCompactionResult> CompactAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AtlasCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default);

    ValueTask<AtlasCoreBackupResult> BackupAsync(
        string destinationDirectory,
        AtlasCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<AtlasCoreMigrationResult> MigrateFromAsync(
        string sourceStoreDirectory,
        string sourceVersion,
        AtlasCoreMigrationOptions? options = null,
        CancellationToken cancellationToken = default);
}
