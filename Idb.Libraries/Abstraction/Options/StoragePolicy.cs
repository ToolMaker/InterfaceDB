using System;

namespace Idb.Libraries.Abstractions.Options;

/// <summary>
/// Defines tunable settings that influence how storage repositories persist payloads.
/// </summary>
public sealed record StoragePolicy
{
    /// <summary>
    /// Singleton instance representing the default policy (sequential writes, notifications enabled).
    /// </summary>
    public static StoragePolicy Default { get; } = new();

    /// <summary>
    /// Maximum number of concurrent write operations. A value of 1 preserves sequential behavior.
    /// Values less than 1 are coerced to 1.
    /// </summary>
    public int MaxConcurrency { get; init; } = 12;

    /// <summary>
    /// When false, change notifications such as <c>ContractAdded</c> are skipped during writes.
    /// </summary>
    public bool RaiseNotifications { get; init; } = true;

    /// <summary>
    /// When true, the writer adds/updates the envelope in the in-memory cache after a successful write.
    /// Disable in bulk-write scenarios to reduce memory retention and GC pressure.
    /// </summary>
    public bool CacheEnvelopeOnWrite { get; init; } = true;

    /// <summary>
    /// When adding to cache on write, controls whether the envelope's ObjectInstance should be retained.
    /// Set to false to store a lightweight skeleton (ObjectInstance = null).
    /// </summary>
    public bool KeepObjectInstanceInCacheOnWrite { get; init; } = true;

    /// <summary>
    /// When true, writer pre-serializes the payload to a single byte[] and writes it in one operation
    /// (avoids per-file FileStream internal buffers). Recommended for small/medium payloads and bulk writes.
    /// </summary>
    public bool UseSingleWriteBuffer { get; init; } = false;

    /// <summary>
    /// When true and MaxConcurrency > 1, the file writer uses an internal semaphore to limit its own concurrency.
    /// Disable when bulk orchestration already bounds concurrency (e.g., SaveMany channel pipeline).
    /// </summary>
    public bool UseWriterSemaphore { get; init; } = true;

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

    /// <summary>
    /// Legacy compatibility flag from when sharding was selected through storage policy.
    /// Repository layout is now chosen via <c>ContractRepositoryType</c> and DI registration.
    /// Flat and sharded repositories ignore this flag.
    /// </summary>
    public bool UseShardedDirectories { get; init; } = false;

    /// <summary>
    /// When true, the sharded file-system repository pre-creates 00-ff shard subfolders during initialization.
    /// Flat file-system repositories ignore this setting.
    /// </summary>
    public bool PrecreateShardFolders { get; init; } = true;

    internal int NormalizedConcurrency
    {
        get
        {
            var candidate = MaxConcurrency;
            return candidate < 1 ? 1 : candidate;
        }
    }
}
