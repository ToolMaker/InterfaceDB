using System;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Options;

/// <summary>
/// Defines tunable settings that influence how storage repositories persist payloads.
/// </summary>
public sealed record StoragePolicy
{
    /// <summary>Borrowed, optional diagnostics supplied at the composition root. Null preserves the disabled fast path.</summary>
    public Idb.Diagnostics.IDatabaseTelemetry? Telemetry { get; init; }
    /// <summary>Null disables historical editing. Finite leases do not survive process/store restart.</summary>
    public RetainedVersionOptions? RetainedVersions { get; init; }
    /// <summary>
    /// Singleton instance representing the default bounded-concurrency, metadata-cache, Performance-mode policy.
    /// </summary>
    public static StoragePolicy Default { get; } = ForHardwareProfile(StorageHardwareProfile.Portable);

    /// <summary>
    /// Creates a complete provider-neutral policy for a documented hardware profile.
    /// Provider packages may use the same profile to resolve their physical I/O defaults.
    /// </summary>
    public static StoragePolicy ForHardwareProfile(StorageHardwareProfile profile)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        var processorCount = Math.Max(1, Environment.ProcessorCount);
        return profile switch
        {
            StorageHardwareProfile.LowMemory => new StoragePolicy
            {
                HardwareProfile = profile,
                MaxConcurrency = Math.Min(2, processorCount),
                MaxReadConcurrency = Math.Min(2, processorCount),
                BulkWriteBatchSize = 256,
                FileWriteBufferSize = 4 * 1024,
                HydrateOnInitialize = false
            },
            StorageHardwareProfile.HighThroughput => new StoragePolicy
            {
                HardwareProfile = profile,
                MaxConcurrency = Math.Clamp(processorCount * 2, 4, 32),
                MaxReadConcurrency = Math.Clamp(processorCount, 4, 16),
                BulkWriteBatchSize = 8_192,
                UseSingleWriteBuffer = true,
                FileWriteBufferSize = 64 * 1024
            },
            StorageHardwareProfile.WindowsNvme => new StoragePolicy
            {
                HardwareProfile = profile,
                MaxConcurrency = Math.Clamp(processorCount * 2, 8, 32),
                MaxReadConcurrency = Math.Clamp(processorCount, 8, 16),
                BulkWriteBatchSize = 8_192,
                UseSingleWriteBuffer = true,
                FileWriteBufferSize = 64 * 1024
            },
            _ => new StoragePolicy { HardwareProfile = profile }
        };
    }

    /// <summary>
    /// Hardware/workload starting point used to resolve provider-specific physical options.
    /// Use <see cref="ForHardwareProfile"/> to seed the associated provider-neutral values.
    /// </summary>
    public StorageHardwareProfile HardwareProfile { get; init; } = StorageHardwareProfile.Portable;

    /// <summary>
    /// Maximum number of concurrent write operations. A value of 1 preserves sequential behavior.
    /// Values less than 1 are coerced to 1.
    /// </summary>
    public int MaxConcurrency { get; init; } = 12;

    /// <summary>
    /// Maximum number of concurrent physical read groups or repository read operations.
    /// A provider may reduce this further when fewer independent storage groups are available.
    /// </summary>
    public int MaxReadConcurrency { get; init; } = 12;

    /// <summary>
    /// Controls whether writes favor throughput or durable out-of-process coordination.
    /// </summary>
    public StorageDurabilityMode DurabilityMode { get; init; } = StorageDurabilityMode.Performance;

    /// <summary>
    /// When false, change notifications such as <c>ContractAdded</c> are skipped during writes.
    /// </summary>
    public bool RaiseNotifications { get; init; } = true;

    /// <summary>Null preserves the notification-disabled repository fast path. Opting in does not
    /// change envelope notifications, disk format, durability or external-writer coverage.</summary>
    public RepositoryNotificationOptions? RepositoryNotifications { get; init; }

    /// <summary>
    /// Controls the default object-instance retention behavior for cached envelopes.
    /// MetadataOnly keeps only envelope metadata, WeakObject lets the GC collect objects when the app releases them,
    /// and StrongObject retains object instances until cache removal/update.
    /// </summary>
    public StorageObjectCacheMode ObjectCacheMode { get; init; } = StorageObjectCacheMode.MetadataOnly;

    /// <summary>
    /// When true, writer pre-serializes the payload to a single byte[] and writes it in one operation
    /// (avoids per-file FileStream internal buffers). Recommended for small/medium payloads and bulk writes.
    /// </summary>
    public bool UseSingleWriteBuffer { get; init; } = false;

    /// <summary>
    /// When true and MaxConcurrency > 1, the file writer uses an internal semaphore to limit its own concurrency.
    /// Disable when bulk orchestration already bounds concurrency.
    /// </summary>
    public bool UseWriterSemaphore { get; init; } = true;

    /// <summary>
    /// Maximum number of objects a bulk writer should process in one bounded window.
    /// Lower values reduce peak memory pressure; higher values can improve throughput on fast storage.
    /// </summary>
    public int BulkWriteBatchSize { get; init; } = 4096;

    /// <summary>
    /// Maximum serialized size of one stored envelope. The limit is enforced before file-backed
    /// readers allocate payload buffers.
    /// </summary>
    public int MaximumStoredObjectBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// When true, file writes acquire a sidecar lock file before committing payload bytes.
    /// This protects the same payload from concurrent writers across repository instances and processes.
    /// </summary>
    public bool UseSidecarWriteLocks { get; init; } = true;

    /// <summary>
    /// Maximum time to wait for a sidecar write lock before failing the write.
    /// </summary>
    public TimeSpan WriteLockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between sidecar write lock acquisition attempts.
    /// </summary>
    public TimeSpan WriteLockRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Age after which an unheld sidecar lock file can be treated as stale and removed.
    /// </summary>
    public TimeSpan StaleWriteLockTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When true, file-system repositories watch committed payload files for external changes and keep the envelope cache in sync.
    /// </summary>
    public bool WatchStorageChanges { get; init; } = true;

    /// <summary>
    /// Delay used to coalesce repeated file-system notifications for the same payload path.
    /// </summary>
    public TimeSpan StorageChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Reserved compatibility setting for revision-aware local watcher-echo suppression.
    /// The file provider currently favors at-least-once delivery because a path-only
    /// acknowledgement cannot safely distinguish a local commit from a later external commit.
    /// </summary>
    public TimeSpan LocalStorageChangeSuppression { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Buffer size (in bytes) used when streaming payloads to disk.
    /// Values less than or equal to zero fall back to <see cref="DefaultBufferSize"/>.
    /// </summary>
    public int FileWriteBufferSize { get; init; } = DefaultBufferSize;

    /// <summary>
    /// Default buffer size applied when <see cref="FileWriteBufferSize"/> is not specified.
    /// </summary>
    public const int DefaultBufferSize = 8 * 1024;

    internal int NormalizedBufferSize
    {
        get
        {
            var candidate = FileWriteBufferSize;
            return candidate <= 0 ? DefaultBufferSize : candidate;
        }
    }

    /// <summary>
    /// When true, repository initialization hydrates all cached envelopes into memory in parallel after seeding metadata.
    /// Set to false to skip warmup hydration for write-heavy scenarios.
    /// </summary>
    public bool HydrateOnInitialize { get; init; } = true;

    internal int NormalizedConcurrency
    {
        get
        {
            var candidate = MaxConcurrency;
            return candidate < 1 ? 1 : candidate;
        }
    }

    internal int NormalizedReadConcurrency
    {
        get
        {
            var candidate = MaxReadConcurrency;
            return candidate < 1 ? 1 : candidate;
        }
    }

    internal int NormalizedBulkWriteBatchSize
    {
        get
        {
            var candidate = BulkWriteBatchSize;
            return candidate < 1 ? 4096 : candidate;
        }
    }

    internal int NormalizedMaximumStoredObjectBytes
        => MaximumStoredObjectBytes <= 0 ? 256 * 1024 * 1024 : MaximumStoredObjectBytes;

    internal TimeSpan NormalizedWriteLockTimeout => WriteLockTimeout <= TimeSpan.Zero
        ? TimeSpan.FromSeconds(30)
        : WriteLockTimeout;

    internal TimeSpan NormalizedWriteLockRetryDelay => WriteLockRetryDelay <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(50)
        : WriteLockRetryDelay;

    internal TimeSpan NormalizedStaleWriteLockTimeout => StaleWriteLockTimeout <= TimeSpan.Zero
        ? TimeSpan.FromMinutes(5)
        : StaleWriteLockTimeout;

    internal TimeSpan NormalizedStorageChangeDebounce => StorageChangeDebounce <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(100)
        : StorageChangeDebounce;

    internal TimeSpan NormalizedLocalStorageChangeSuppression => LocalStorageChangeSuppression <= TimeSpan.Zero
        ? TimeSpan.FromMilliseconds(750)
        : LocalStorageChangeSuppression;
}
