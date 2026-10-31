using System.Text;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Transport;

/// <summary>
/// Owns a durable SegmentCore store of opaque values. All access is serialized through this
/// adapter. Enumeration is ordinal keyset pagination of current state, not a durable snapshot.
/// </summary>
public sealed partial class SegmentCoreEncodedEnvelopeStore : IEncodedEnvelopeStore, IPartitionIndexDiagnostics
{
    // Covers the wire envelope, fixed response fields and continuation key. Per-record variable
    // UTF-8 key bytes and binary lengths are accounted separately below.
    private const int FrameOverheadReserve = 2048;
    private const int RecordOverheadReserve = 32;
    private readonly SegmentCoreStore _store;
    private readonly TransportLimits _limits;
    private readonly int _maximumEnumerationKeys;
    private readonly int _maximumPartitionKeys;
    private readonly IPartitionKeyIndex _partitionIndex;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _disposed;

    private SegmentCoreEncodedEnvelopeStore(SegmentCoreStore store, TransportLimits limits,
        int maximumEnumerationKeys, int maximumPartitionKeys, IPartitionKeyIndex partitionIndex)
        => (_store, _limits, _maximumEnumerationKeys, _maximumPartitionKeys, _partitionIndex)
            = (store, limits, maximumEnumerationKeys, maximumPartitionKeys, partitionIndex);

    public EncodedStoreCapabilities Capabilities => EncodedStoreCapabilities.All & ~EncodedStoreCapabilities.ReserveRevisions;
    public PartitionIndexStatistics PartitionIndexStatistics => _partitionIndex.Statistics;

    /// <summary>Opens an exclusively owned store with durable writes; no relaxed durability override is exposed.</summary>
    // Preserve the original six-parameter method for already compiled consumers.
    public static ValueTask<SegmentCoreEncodedEnvelopeStore> OpenAsync(
        string path, TransportLimits? limits, int maximumEnumerationKeys,
        CancellationToken cancellationToken, int retainedChangeCount, int? maximumPartitionKeys)
        => OpenAsync(path, limits, maximumEnumerationKeys, cancellationToken, retainedChangeCount, maximumPartitionKeys, telemetry: null);

    // Preserve the previous seven-parameter binary entry point.
    public static ValueTask<SegmentCoreEncodedEnvelopeStore> OpenAsync(
        string path, TransportLimits? limits, int maximumEnumerationKeys, CancellationToken cancellationToken,
        int retainedChangeCount, int? maximumPartitionKeys, Idb.Diagnostics.IDatabaseTelemetry? telemetry)
        => OpenAsync(path, limits, maximumEnumerationKeys, cancellationToken, retainedChangeCount,
            maximumPartitionKeys, telemetry, keyIndexFactory: null);

    public static async ValueTask<SegmentCoreEncodedEnvelopeStore> OpenAsync(
        string path, TransportLimits? limits = null, int maximumEnumerationKeys = 100_000,
        CancellationToken cancellationToken = default, int retainedChangeCount = 2048,
        int? maximumPartitionKeys = null, Idb.Diagnostics.IDatabaseTelemetry? telemetry = null,
        IPartitionKeyIndexFactory? keyIndexFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        limits ??= new TransportLimits();
        limits.Validate();
        if (retainedChangeCount is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(retainedChangeCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEnumerationKeys, 1);
        maximumPartitionKeys ??= Math.Min(maximumEnumerationKeys, 10_000);
        if (maximumPartitionKeys is < 1 || maximumPartitionKeys > maximumEnumerationKeys)
            throw new ArgumentOutOfRangeException(nameof(maximumPartitionKeys));
        cancellationToken.ThrowIfCancellationRequested();
        var store = await SegmentCoreStore.OpenAsync(path, new SegmentCoreOptions
        {
            Telemetry = telemetry,
            LogOptions = new StripedObjectLogProbeOptions
            {
                DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
            }
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            var index = (keyIndexFactory ?? BoundedPartitionKeyIndexFactory.Default).Create()
                ?? throw new InvalidOperationException("The partition index factory returned no index.");
            index.Initialize(store.GetKeysSnapshot, cancellationToken);
            return new SegmentCoreEncodedEnvelopeStore(store, limits, maximumEnumerationKeys, maximumPartitionKeys.Value, index)
                { RetainedChangeCount = retainedChangeCount };
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<EncodedStoreResponse> ExecuteAsync(
        EncodedStoreRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var owned = SnapshotRequest(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            switch (owned.Command)
            {
                case EncodedStoreCommand.Get:
                    var payload = await _store.GetAsync(owned.Key!, cancellationToken).ConfigureAwait(false);
                    if (payload is not null) ValidateResponseRecord(owned.Key!, payload);
                    return new() { Found = payload is not null, Payload = payload };
                case EncodedStoreCommand.Put:
                    return await ApplyTrackedBatchAsync([new(owned.Key!, owned.Payload!)], owned.Partition, cancellationToken).ConfigureAwait(false);
                case EncodedStoreCommand.Delete:
                    var removed = await ApplyTrackedBatchAsync([new(owned.Key!, null)], owned.Partition, cancellationToken).ConfigureAwait(false);
                    return removed with { Found = removed.AffectedCount != 0 };
                case EncodedStoreCommand.Changes:
                    return await ReadChangesAsync(owned, cancellationToken).ConfigureAwait(false);
                case EncodedStoreCommand.GetMany:
                    return await ReadManyAsync(owned.Keys!, cancellationToken).ConfigureAwait(false);
                case EncodedStoreCommand.ApplyBatch:
                    return await ApplyTrackedBatchAsync(owned.Mutations!, owned.Partition, cancellationToken).ConfigureAwait(false);
                case EncodedStoreCommand.Enumerate:
                    return await EnumerateAsync(owned.PageSize, owned.AfterKey, owned.Partition,
                        owned.UnpartitionedOnly, cancellationToken).ConfigureAwait(false);
                case EncodedStoreCommand.Checkpoint:
                    await _store.CheckpointAsync(cancellationToken).ConfigureAwait(false);
                    return new();
                case EncodedStoreCommand.Ping:
                    return new();
                default:
                    throw new ArgumentException("Unknown storage command.", nameof(request));
            }
        }
        finally { _gate.Release(); }
    }

    private EncodedStoreRequest SnapshotRequest(EncodedStoreRequest request)
    {
        TransportValidation.Request(request, _limits);
        static void ReservedPartition(string? key, string partition)
        {
            if (key is not null && ScopedEncodedStore.TryGetPrefix(key, out var prefix)
                && !string.Equals(prefix, partition, StringComparison.Ordinal))
                throw new ArgumentException("The subject partition namespace is reserved.");
        }
        ReservedPartition(request.Key, request.Partition);
        if (request.Keys is not null)
            foreach (var key in request.Keys) ReservedPartition(key, request.Partition);
        if (request.Mutations is not null)
            foreach (var mutation in request.Mutations) ReservedPartition(mutation.Key, request.Partition);
        if ((request.Key?.StartsWith(FeedPrefix, StringComparison.Ordinal) ?? false)
            || request.Keys?.Any(key => key.StartsWith(FeedPrefix, StringComparison.Ordinal)) == true
            || request.Mutations?.Any(item => item.Key.StartsWith(FeedPrefix, StringComparison.Ordinal)) == true)
            throw new ArgumentException("Reserved storage key.");
        // Snapshot the arrays of immutable records first, so changing a caller's array slots
        // cannot replace a validated payload before its owned copy is made.
        var snapshot = request with { Keys = request.Keys?.ToArray(), Mutations = request.Mutations?.ToArray() };
        TransportValidation.Request(snapshot, _limits);
        long bytes = FrameOverheadReserve + _limits.MaximumKeyBytes;
        if (snapshot.Key is not null) bytes += Encoding.UTF8.GetByteCount(snapshot.Key);
        if (snapshot.Payload is not null) bytes += snapshot.Payload.Length;
        if (snapshot.Keys is not null)
            foreach (var key in snapshot.Keys) bytes += RecordOverheadReserve + Encoding.UTF8.GetByteCount(key);
        if (snapshot.Mutations is not null)
            foreach (var mutation in snapshot.Mutations)
                bytes += RecordOverheadReserve + Encoding.UTF8.GetByteCount(mutation.Key) + (mutation.Payload?.Length ?? 0);
        if (bytes > _limits.MaximumFrameBytes)
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.LimitExceeded,
                "The aggregate request exceeds the frame budget.");
        return snapshot with
        {
            Payload = snapshot.Payload?.ToArray(),
            Mutations = snapshot.Mutations?.Select(m => new EncodedMutation(m.Key, m.Payload?.ToArray())).ToArray()
        };
    }

    private async ValueTask<EncodedStoreResponse> ReadManyAsync(string[] keys, CancellationToken token)
    {
        var records = new List<EncodedRecord>(keys.Length);
        long bytes = ResponseOverhead;
        foreach (var key in keys)
        {
            var payload = await _store.GetAsync(key, token).ConfigureAwait(false);
            if (payload is null) continue;
            bytes += ValidateResponseRecord(key, payload);
            if (bytes > _limits.MaximumFrameBytes) throw LimitExceeded("The aggregate read response exceeds the frame budget.");
            records.Add(new(key, payload));
        }
        return new() { Records = records.ToArray() };
    }

    private async ValueTask<EncodedStoreResponse> EnumerateAsync(
        int pageSize, string? afterKey, string partition, bool unpartitionedOnly, CancellationToken token)
    {
        string[] keys;
        int start;
        PartitionKeyPage? indexed;
        try
        {
            // A partition index's empty-scope contract describes physical enumeration, not
            // shared-only keys. Do not let an injected index hide shared records behind tenants.
            indexed = unpartitionedOnly && partition.Length == 0 ? null
                : _partitionIndex.Select(partition, afterKey, pageSize + 1, _store.GetKeysSnapshot, token);
            if (indexed is not null)
            {
                if (indexed.PartitionCount < indexed.Keys.Count || indexed.Keys.Count > pageSize + 1)
                    throw new InvalidDataException("Invalid partition index page.");
                string? previous = afterKey;
                foreach (var key in indexed.Keys)
                {
                    if (!key.StartsWith(partition, StringComparison.Ordinal)
                        || (previous is not null && StringComparer.Ordinal.Compare(key, previous) <= 0))
                        throw new InvalidDataException("The partition index returned an out-of-scope or unordered key.");
                    previous = key;
                }
            }
        }
        catch (UnchangedPartitionIndexCancellationException) { throw; }
        catch
        {
            // A custom index can partially mutate derived state before failing selection.
            _partitionIndex.Invalidate();
            throw;
        }
        if (indexed is not null)
        {
            if (indexed.PartitionCount > _maximumPartitionKeys)
                throw LimitExceeded("This store exceeds the configured enumeration key ceiling.");
            keys = indexed.Keys.ToArray(); start = 0;
        }
        else
        {
            var snapshot = _store.GetKeysSnapshot();
            var ceiling = partition.Length == 0 ? _maximumEnumerationKeys : _maximumPartitionKeys;
            if (unpartitionedOnly && partition.Length == 0)
            {
                var visible = new List<string>();
                var physicalCount = 0;
                foreach (var key in snapshot)
                {
                    token.ThrowIfCancellationRequested();
                    if (key.StartsWith(FeedPrefix, StringComparison.Ordinal)) continue;
                    // The hint changes work performed, not the existing physical key ceiling.
                    if (++physicalCount > ceiling)
                        throw LimitExceeded("This store exceeds the configured enumeration key ceiling.");
                    if (ScopedEncodedStore.IsUnpartitioned(key)) visible.Add(key);
                }
                keys = visible.ToArray();
            }
            else
            {
                keys = snapshot.Where(key => !key.StartsWith(FeedPrefix, StringComparison.Ordinal)
                    && (partition.Length == 0 || key.StartsWith(partition, StringComparison.Ordinal))).ToArray();
                if (keys.Length > ceiling)
                    throw LimitExceeded("This store exceeds the configured enumeration key ceiling.");
            }
            Array.Sort(keys, StringComparer.Ordinal);
            start = afterKey is null ? 0 : Array.BinarySearch(keys, afterKey, StringComparer.Ordinal);
            if (afterKey is not null) start = start < 0 ? ~start : start + 1;
        }
        var records = new List<EncodedRecord>(Math.Min(pageSize, keys.Length - start));
        long bytes = ResponseOverhead;
        var next = start;
        while (next < keys.Length && records.Count < pageSize)
        {
            token.ThrowIfCancellationRequested();
            var key = keys[next];
            var payload = await _store.GetAsync(key, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("An exclusively owned store lost an enumerated key.");
            var recordBytes = ValidateResponseRecord(key, payload);
            if (bytes + recordBytes > _limits.MaximumFrameBytes)
            {
                if (records.Count == 0) throw LimitExceeded("A stored record cannot fit in a response frame.");
                break;
            }
            records.Add(new(key, payload));
            bytes += recordBytes;
            next++;
        }
        return new()
        {
            Records = records.ToArray(),
            NextAfterKey = next < keys.Length && records.Count > 0 ? records[^1].Key : null
        };
    }

    private int ResponseOverhead => FrameOverheadReserve + _limits.MaximumKeyBytes;

    private long ValidateResponseRecord(string key, byte[] payload)
    {
        var keyBytes = Encoding.UTF8.GetByteCount(key);
        var size = (long)RecordOverheadReserve + keyBytes + payload.Length;
        if (payload.Length > _limits.MaximumPayloadBytes || keyBytes > _limits.MaximumKeyBytes
            || ResponseOverhead + size > _limits.MaximumFrameBytes)
            throw LimitExceeded("A stored record exceeds the configured response limits.");
        return size;
    }

    private static RemoteStoreException LimitExceeded(string message) => new(RemoteErrorCode.LimitExceeded, message);

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync();
            }
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _store.DisposeAsync().ConfigureAwait(false); }
        finally { _partitionIndex.Invalidate(); _gate.Release(); }
    }
}
