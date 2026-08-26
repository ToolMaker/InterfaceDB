using Idb.Storage.SegmentCore.ObjectLog;

internal sealed class StripedObjectLogPhaseOneStore : IPhaseOneStore
{
    private readonly string _root;
    private readonly StripedObjectLogProbeOptions _options;
    private StripedObjectLogProbeWriter? _writer;

    public StripedObjectLogPhaseOneStore(string root, StripedObjectLogProbeOptions options)
    {
        _root = root;
        _options = options;
    }

    public string EngineName => "Striped checksummed object log";

    public string Durability => _options.DurabilityStrategy.ToString();

    public int StripeCount => _options.StripeCount;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _writer = await StripedObjectLogProbeWriter.OpenAsync(
            _root,
            _options,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var payload = PhaseZeroCodec.Encode(record);
        _ = await Writer.AppendAsync(record.Id, payload, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recovery = StripedObjectLogRecovery.Recover(_root, repairActiveTails: false);
        long checksum = 0;
        foreach (var recovered in recovery.Records.Values)
        {
            checksum += PhaseZeroCodec.Decode(recovered.Payload).MaterializedChecksum;
        }

        return ValueTask.FromResult((recovery.Records.Count, checksum));
    }

    public PhaseOneStoreStatistics GetStatistics()
    {
        var storage = PhaseZeroStoreUtilities.GetDirectoryStats(_root);
        var writer = Writer.Statistics;
        return new PhaseOneStoreStatistics(
            storage.Bytes,
            storage.Files,
            writer.DurableBatches,
            writer.DurabilityFlushes,
            writer.AverageBatchSize,
            writer.LargestBatch);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
    }

    private StripedObjectLogProbeWriter Writer =>
        _writer ?? throw new InvalidOperationException("Striped object-log store is not initialized.");
}

internal sealed class PhaseOneBaselineStore : IPhaseOneStore
{
    private readonly IPhaseZeroStore _inner;

    public PhaseOneBaselineStore(IPhaseZeroStore inner)
    {
        _inner = inner;
    }

    public string EngineName => _inner.EngineName;

    public string Durability => _inner.DurabilityName;

    public int StripeCount => 0;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    public ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default) =>
        _inner.AppendAsync(record, cancellationToken);

    public ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default) =>
        _inner.VerifyAsync(cancellationToken);

    public PhaseOneStoreStatistics GetStatistics()
    {
        var storage = _inner.GetStorageStats();
        return new PhaseOneStoreStatistics(storage.Bytes, storage.Files, 0, 0, 0, 0);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
