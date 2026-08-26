using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal sealed class SegmentCorePhaseTwoStore : IPhaseTwoStore
{
    private readonly string _root;
    private readonly SegmentCoreOptions _options;
    private SegmentCoreStore? _store;

    public SegmentCorePhaseTwoStore(
        string root,
        int stripeCount,
        long maxSegmentBytes,
        int queueCapacityPerStripe,
        int checkpointGenerationsToKeep)
    {
        _root = root;
        _options = new SegmentCoreOptions
        {
            CheckpointGenerationsToKeep = checkpointGenerationsToKeep,
            LogOptions = new StripedObjectLogProbeOptions
            {
                StripeCount = stripeCount,
                MaximumBatchRecords = 64,
                QueueCapacityPerStripe = queueCapacityPerStripe,
                MaxSegmentBytes = maxSegmentBytes,
                DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
            }
        };
    }

    public string EngineName => "SegmentCore";

    public int StripeCount => _options.LogOptions.StripeCount;

    public int Count => Store.Count;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _store = await SegmentCoreStore.OpenAsync(_root, _options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SaveAsync(GateRecord record, CancellationToken cancellationToken = default) =>
        Store.SaveAsync(record.Id, PhaseZeroCodec.Encode(record), cancellationToken);

    public async ValueTask<GateQueryResult> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var payload = await Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return default;
        }

        var record = PhaseZeroCodec.Decode(payload);
        return new GateQueryResult(1, record.MaterializedChecksum);
    }

    public async ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Store.DeleteAsync(key, cancellationToken);

    public async ValueTask<PhaseTwoCheckpointStats> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        var checkpoint = await Store.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return new PhaseTwoCheckpointStats(checkpoint.Bytes, checkpoint.Elapsed.TotalMilliseconds);
    }

    public async ValueTask ReopenAsync(CancellationToken cancellationToken = default)
    {
        await Store.DisposeAsync().ConfigureAwait(false);
        _store = await SegmentCoreStore.OpenAsync(_root, _options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, GateRecord>(StringComparer.Ordinal);
        foreach (var key in Store.GetKeysSnapshot())
        {
            var payload = await Store.GetAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Primary key '{key}' disappeared during snapshot verification.");
            result.Add(key, PhaseZeroCodec.Decode(payload));
        }

        return result;
    }

    public PhaseTwoStorageStats GetStorageStats()
    {
        var stats = PhaseZeroStoreUtilities.GetDirectoryStats(_root);
        return new PhaseTwoStorageStats(stats.Bytes, stats.Files);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
            _store = null;
        }
    }

    private SegmentCoreStore Store =>
        _store ?? throw new InvalidOperationException("SegmentCore is not initialized.");
}

internal sealed class ProductionGatePhaseTwoStore : IPhaseTwoStore
{
    private readonly IProductionGateStore _inner;

    public ProductionGatePhaseTwoStore(IProductionGateStore inner)
    {
        _inner = inner;
    }

    public string EngineName => _inner.EngineName;

    public int StripeCount => 0;

    public int Count => Volatile.Read(ref _count);

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    public async ValueTask SaveAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.InsertAsync(record, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _count);
    }

    public ValueTask<GateQueryResult> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _inner.QueryAsync(GateQueryKind.Exact, key, cancellationToken);

    public ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default) =>
        _inner.UpdateAsync(record, cancellationToken);

    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var deleted = await _inner.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            Interlocked.Decrement(ref _count);
        }

        return deleted;
    }

    public ValueTask<PhaseTwoCheckpointStats> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(default(PhaseTwoCheckpointStats));
    }

    public ValueTask ReopenAsync(CancellationToken cancellationToken = default) =>
        _inner.ReopenAsync(cancellationToken);

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _inner.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _count, snapshot.Count);
        return snapshot;
    }

    public PhaseTwoStorageStats GetStorageStats()
    {
        var stats = _inner.GetStorageStats();
        return new PhaseTwoStorageStats(stats.Bytes, stats.Files);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private int _count;
}
