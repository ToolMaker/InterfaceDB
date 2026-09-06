namespace Idb.Libraries.Abstractions.Options;

/// <summary>Opt-in local repository notifications; independent of physical RaiseNotifications.</summary>
public sealed record RepositoryNotificationOptions
{
    /// <summary>Pending event candidates per subscriber. Changed candidates may later be suppressed as no-ops.</summary>
    public int QueueCapacity { get; init; } = 256;
    public int MaximumSubscribers { get; init; } = 1024;
    public int MaximumConcurrentCallbacks { get; init; } = 8;
    public int MaximumSnapshotBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumCollectionItems { get; init; } = 65_536;
    /// <summary>Encoded bytes retained by pending candidates; Changed charges both before and after snapshots.</summary>
    public int MaximumQueuedBytesPerSubscriber { get; init; } = 16 * 1024 * 1024;
    /// <summary>Bounded committed baselines avoid rereading hot entities. Zero disables retention.
    /// Correctness requires mutations to pass through this owning repository.</summary>
    public int MaximumBaselineEntries { get; init; } = 256;
    public int MaximumBaselineBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumCausalDepth { get; init; } = 64;
    /// <summary>Comparers for otherwise unsupported persisted top-level contract property types.
    /// Comparers must be pure, bounded and thread-safe. Snapshotting still uses the store serializer.</summary>
    public IReadOnlyDictionary<string, IEqualityComparer<object?>> PropertyComparers { get; init; }
        = new Dictionary<string, IEqualityComparer<object?>>();
}
