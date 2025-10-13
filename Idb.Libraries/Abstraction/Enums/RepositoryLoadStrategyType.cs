namespace Idb.Libraries.Abstraction.Enums;

/// <summary>
/// Controls how a repository hydrates data when it starts.
/// </summary>
public enum RepositoryLoadStrategyType
{
    /// <summary>
    /// Do not perform any automatic loading beyond essential setup.
    /// </summary>
    None = 0,

    /// <summary>
    /// Lazily load data on first access; no eager work is done during startup.
    /// </summary>
    Lazy,

    /// <summary>
    /// Read all persisted records during startup so that subsequent calls hit a warm cache.
    /// </summary>
    EagerWarmCache,

    /// <summary>
    /// Materialise repository indexes without loading full payloads.
    /// </summary>
    IndexWarmup,

    /// <summary>
    /// Prefetch a subset of data (e.g., hot partitions or tenants) while leaving the rest lazy.
    /// </summary>
    PartialPrefetch,

    /// <summary>
    /// Start with lazy behaviour and trigger a background task to hydrate the store asynchronously.
    /// </summary>
    BackgroundWarmup,

    /// <summary>
    /// Adaptively load data as usage patterns emerge, expanding the warm cache over time.
    /// </summary>
    AdaptiveCaching,

    /// <summary>
    /// Import a pre-generated snapshot into the cache instead of reading individual records.
    /// </summary>
    SnapshotImport,

    /// <summary>
    /// Load only metadata (such as manifests or directory listings) and defer payload reads entirely.
    /// </summary>
    MetadataOnly
}
