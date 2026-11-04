namespace Idb.Commercial.Repositories;

/// <summary>
/// Authenticated hint that a peer key, or the whole dataset, may contain a later revision.
/// The notification is a hint; reconnect logic must still pull from the durable checkpoint.
/// </summary>
public sealed record RepositoryChangeSignal(
    string DatasetId,
    string? StorageKey,
    long RevisionTimestampUtcMicroseconds,
    long Checkpoint);

/// <summary>
/// One key awaiting a peer revision check.
/// </summary>
public sealed record ResilientDirtyKey(
    long RevisionTimestampUtcMicroseconds,
    long Checkpoint);

/// <summary>
/// Snapshot of invalidation and checkpoint state for one dataset.
/// </summary>
public sealed record ResilientSynchronizationState(
    long LatestObservedCheckpoint,
    long LastAppliedCheckpoint,
    long? DatasetDirtyThroughCheckpoint,
    IReadOnlyDictionary<string, ResilientDirtyKey> DirtyStorageKeys)
{
    public bool IsDatasetDirty => DatasetDirtyThroughCheckpoint.HasValue;
}
