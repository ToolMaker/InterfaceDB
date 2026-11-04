namespace Idb.Commercial.Repositories;

/// <summary>
/// Persists remote invalidation and pull-checkpoint state. Production mobile clients should
/// provide a durable implementation; the default implementation is process-local.
/// </summary>
public interface IResilientSynchronizationStateStore
{
    ValueTask<ResilientSynchronizationState> GetAsync(
        string datasetId,
        CancellationToken cancellationToken = default);

    ValueTask MarkChangedAsync(
        RepositoryChangeSignal signal,
        CancellationToken cancellationToken = default);

    ValueTask MarkStorageKeySynchronizedAsync(
        string datasetId,
        string storageKey,
        long throughCheckpoint,
        CancellationToken cancellationToken = default);

    ValueTask MarkDatasetSynchronizedAsync(
        string datasetId,
        long throughCheckpoint,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the durable pull checkpoint only after a complete ordered change-feed page has been applied.
    /// Receiving a push hint or refreshing one key must not call this method.
    /// </summary>
    ValueTask AdvanceAppliedCheckpointAsync(
        string datasetId,
        long throughCheckpoint,
        CancellationToken cancellationToken = default);
}
