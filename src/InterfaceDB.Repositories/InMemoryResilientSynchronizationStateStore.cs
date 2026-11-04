using System.Collections.Concurrent;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Process-local synchronization-state store intended for development and hosts that supply
/// checkpoint replay independently. Use a durable implementation when missed push hints matter.
/// </summary>
public sealed class InMemoryResilientSynchronizationStateStore : IResilientSynchronizationStateStore
{
    private const int MaximumDatasetIdLength = 256;
    private const int MaximumStorageKeyLength = 1024;
    private readonly ConcurrentDictionary<string, DatasetState> _datasets = new(StringComparer.Ordinal);
    private readonly int _maximumDirtyStorageKeys;

    public InMemoryResilientSynchronizationStateStore(int maximumDirtyStorageKeys = 10_000)
    {
        if (maximumDirtyStorageKeys <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDirtyStorageKeys));
        }

        _maximumDirtyStorageKeys = maximumDirtyStorageKeys;
    }

    public ValueTask<ResilientSynchronizationState> GetAsync(
        string datasetId,
        CancellationToken cancellationToken = default)
    {
        ValidateDatasetId(datasetId);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _datasets.GetOrAdd(datasetId, static _ => new DatasetState());
        lock (state.Gate)
        {
            return ValueTask.FromResult(new ResilientSynchronizationState(
                state.LatestObservedCheckpoint,
                state.LastAppliedCheckpoint,
                state.DatasetDirtyThroughCheckpoint,
                new Dictionary<string, ResilientDirtyKey>(state.DirtyStorageKeys, StringComparer.Ordinal)));
        }
    }

    public ValueTask MarkChangedAsync(
        RepositoryChangeSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ValidateDatasetId(signal.DatasetId);
        ValidateCheckpoint(signal.Checkpoint);
        if (signal.RevisionTimestampUtcMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signal), "A revision timestamp cannot be negative.");
        }

        if (signal.StorageKey is not null
            && (string.IsNullOrWhiteSpace(signal.StorageKey)
                || signal.StorageKey.Length > MaximumStorageKeyLength))
        {
            throw new ArgumentException("A storage key cannot be whitespace.", nameof(signal));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = _datasets.GetOrAdd(signal.DatasetId, static _ => new DatasetState());
        lock (state.Gate)
        {
            state.LatestObservedCheckpoint = Math.Max(state.LatestObservedCheckpoint, signal.Checkpoint);
            if (state.LastAppliedCheckpoint > 0
                && signal.Checkpoint <= state.LastAppliedCheckpoint)
            {
                return ValueTask.CompletedTask;
            }

            if (signal.StorageKey is null)
            {
                state.DatasetDirtyThroughCheckpoint = Math.Max(
                    state.DatasetDirtyThroughCheckpoint ?? 0,
                    signal.Checkpoint);
            }
            else
            {
                var key = signal.StorageKey;
                if (state.DirtyStorageKeys.TryGetValue(key, out var existing))
                {
                    state.DirtyStorageKeys[key] = new ResilientDirtyKey(
                        Math.Max(existing.RevisionTimestampUtcMicroseconds, signal.RevisionTimestampUtcMicroseconds),
                        Math.Max(existing.Checkpoint, signal.Checkpoint));
                }
                else
                {
                    state.DirtyStorageKeys[key] = new ResilientDirtyKey(
                        signal.RevisionTimestampUtcMicroseconds,
                        signal.Checkpoint);
                }

                if (state.DirtyStorageKeys.Count > _maximumDirtyStorageKeys)
                {
                    state.DatasetDirtyThroughCheckpoint = Math.Max(
                        state.DatasetDirtyThroughCheckpoint ?? 0,
                        state.LatestObservedCheckpoint);
                    state.DirtyStorageKeys.Clear();
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MarkStorageKeySynchronizedAsync(
        string datasetId,
        string storageKey,
        long throughCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateDatasetId(datasetId);
        ValidateStorageKey(storageKey);
        ValidateCheckpoint(throughCheckpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _datasets.GetOrAdd(datasetId, static _ => new DatasetState());
        lock (state.Gate)
        {
            if (state.DirtyStorageKeys.TryGetValue(storageKey, out var dirty)
                && dirty.Checkpoint <= throughCheckpoint)
            {
                state.DirtyStorageKeys.Remove(storageKey);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDatasetSynchronizedAsync(
        string datasetId,
        long throughCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateDatasetId(datasetId);
        ValidateCheckpoint(throughCheckpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _datasets.GetOrAdd(datasetId, static _ => new DatasetState());
        lock (state.Gate)
        {
            if (state.DatasetDirtyThroughCheckpoint is { } dirtyCheckpoint
                && dirtyCheckpoint <= throughCheckpoint)
            {
                state.DatasetDirtyThroughCheckpoint = null;
            }

            foreach (var storageKey in state.DirtyStorageKeys
                         .Where(pair => pair.Value.Checkpoint <= throughCheckpoint)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                state.DirtyStorageKeys.Remove(storageKey);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AdvanceAppliedCheckpointAsync(
        string datasetId,
        long throughCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ValidateDatasetId(datasetId);
        ValidateCheckpoint(throughCheckpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var state = _datasets.GetOrAdd(datasetId, static _ => new DatasetState());
        lock (state.Gate)
        {
            if (throughCheckpoint < state.LastAppliedCheckpoint)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(throughCheckpoint),
                    "An applied checkpoint cannot move backwards.");
            }

            state.LastAppliedCheckpoint = throughCheckpoint;
            state.LatestObservedCheckpoint = Math.Max(
                state.LatestObservedCheckpoint,
                throughCheckpoint);

            if (state.DatasetDirtyThroughCheckpoint is { } datasetDirty
                && datasetDirty <= throughCheckpoint)
            {
                state.DatasetDirtyThroughCheckpoint = null;
            }

            foreach (var storageKey in state.DirtyStorageKeys
                         .Where(pair => pair.Value.Checkpoint <= throughCheckpoint)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                state.DirtyStorageKeys.Remove(storageKey);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static void ValidateDatasetId(string datasetId)
    {
        if (string.IsNullOrWhiteSpace(datasetId) || datasetId.Length > MaximumDatasetIdLength)
        {
            throw new ArgumentException("A dataset identifier is required.", nameof(datasetId));
        }
    }

    private static void ValidateStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaximumStorageKeyLength)
        {
            throw new ArgumentException("A storage key is required.", nameof(storageKey));
        }
    }

    private static void ValidateCheckpoint(long checkpoint)
    {
        if (checkpoint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }
    }

    private sealed class DatasetState
    {
        internal object Gate { get; } = new();

        internal long LatestObservedCheckpoint { get; set; }

        internal long LastAppliedCheckpoint { get; set; }

        internal long? DatasetDirtyThroughCheckpoint { get; set; }

        internal Dictionary<string, ResilientDirtyKey> DirtyStorageKeys { get; }
            = new(StringComparer.Ordinal);
    }
}
