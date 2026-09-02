using FASTER.core;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

internal sealed class ObjectLogPhaseZeroStore : IPhaseZeroStore
{
    private readonly string _root;
    private readonly ObjectLogProbeOptions _options;
    private ObjectLogProbeWriter? _writer;

    public ObjectLogPhaseZeroStore(string root, ObjectLogProbeOptions options)
    {
        _root = root;
        _options = options;
    }

    public string EngineName => "Phase 0 checksummed object log";

    public string DurabilityName => _options.DurabilityMode == ObjectLogDurabilityMode.Durable
        ? $"Durable/{_options.DurabilityStrategy}"
        : _options.DurabilityMode.ToString();

    public string Scope => "object codec + key + framed append";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _writer = await ObjectLogProbeWriter.OpenAsync(_root, _options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var payload = PhaseZeroCodec.Encode(record);
        _ = await Writer.AppendAsync(record.Id, payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        var recovery = ObjectLogRecovery.Recover(_root, repairActiveTail: false);
        long checksum = 0;
        foreach (var recovered in recovery.Records.Values)
        {
            checksum += PhaseZeroCodec.Decode(recovered.Payload).MaterializedChecksum;
        }

        return (recovery.Records.Count, checksum);
    }

    public PhaseZeroStorageStats GetStorageStats() => PhaseZeroStoreUtilities.GetDirectoryStats(_root);

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
    }

    private ObjectLogProbeWriter Writer =>
        _writer ?? throw new InvalidOperationException("Object-log store is not initialized.");
}

internal sealed class RawAppendPhaseZeroStore : IPhaseZeroStore
{
    private readonly string _root;
    private SafeFileHandle? _handle;
    private long _offset;

    public RawAppendPhaseZeroStore(string root)
    {
        _root = root;
    }

    public string EngineName => "Raw reused append handle";

    public string DurabilityName => "FlushToDisk per object";

    public string Scope => "object codec + length + append (no recovery framing)";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        _handle = File.OpenHandle(
            Path.Combine(_root, "raw.log"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var payload = PhaseZeroCodec.Encode(record);
        var framed = new byte[sizeof(int) + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(sizeof(int)));
        await RandomAccess.WriteAsync(Handle, framed, _offset, cancellationToken).ConfigureAwait(false);
        RandomAccess.FlushToDisk(Handle);
        _offset += framed.Length;
    }

    public async ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var lengthBuffer = new byte[sizeof(int)];
        var position = 0L;
        var count = 0;
        var checksum = 0L;
        while (position < _offset)
        {
            await ReadExactlyAsync(Handle, lengthBuffer, position, cancellationToken).ConfigureAwait(false);
            position += lengthBuffer.Length;
            var payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (payloadLength <= 0 || payloadLength > _offset - position)
            {
                throw new InvalidDataException(
                    $"Raw append log contains invalid payload length {payloadLength} at offset {position - lengthBuffer.Length}.");
            }

            var payload = new byte[payloadLength];
            await ReadExactlyAsync(Handle, payload, position, cancellationToken).ConfigureAwait(false);
            position += payloadLength;
            var record = PhaseZeroCodec.Decode(payload);
            count++;
            checksum += record.MaterializedChecksum;
            GC.KeepAlive(record);
        }

        if (position != _offset)
        {
            throw new InvalidDataException($"Raw append verification stopped at {position}; expected {_offset} bytes.");
        }

        return (count, checksum);
    }

    public PhaseZeroStorageStats GetStorageStats() => PhaseZeroStoreUtilities.GetDirectoryStats(_root);

    public ValueTask DisposeAsync()
    {
        _handle?.Dispose();
        _handle = null;
        return ValueTask.CompletedTask;
    }

    private SafeFileHandle Handle => _handle ?? throw new InvalidOperationException("Raw store is not initialized.");

    private static async ValueTask ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var current = await RandomAccess.ReadAsync(
                handle,
                buffer[read..],
                offset + read,
                cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                throw new EndOfStreamException(
                    $"Raw append log ended at offset {offset + read}; expected {buffer.Length - read} more bytes.");
            }

            read += current;
        }
    }
}

internal sealed class FasterLogPhaseZeroStore : IPhaseZeroStore
{
    private readonly string _root;
    private FasterLogSettings? _settings;
    private FasterLog? _log;

    public FasterLogPhaseZeroStore(string root)
    {
        _root = root;
    }

    public string EngineName => "Microsoft FASTER Log 2.6.5";

    public string DurabilityName => "Per-entry checksum + CommitAsync per object";

    public string Scope => "object codec + FASTER log append/commit";

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_root);
        _settings = new FasterLogSettings(_root, deleteDirOnDispose: false)
        {
            LogChecksum = LogChecksumType.PerEntry,
            FastCommitMode = true,
            RemoveOutdatedCommits = false,
            TryRecoverLatest = false,
            AutoCommit = false,
            PageSize = 1L << 20,
            MemorySize = 1L << 22,
            SegmentSize = 1L << 24
        };
        _log = new FasterLog(_settings);
        return ValueTask.CompletedTask;
    }

    public async ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var payload = PhaseZeroCodec.Encode(record);
        _ = await Log.EnqueueAsync(payload, cancellationToken).ConfigureAwait(false);
        await Log.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0;
        long checksum = 0;
        using var iterator = Log.Scan(Log.BeginAddress, Log.CommittedUntilAddress);
        while (iterator.GetNext(out var entry, out var entryLength, out _))
        {
            var record = PhaseZeroCodec.Decode(entry.AsSpan(0, entryLength));
            count++;
            checksum += record.MaterializedChecksum;
        }

        return ValueTask.FromResult((count, checksum));
    }

    public PhaseZeroStorageStats GetStorageStats() => PhaseZeroStoreUtilities.GetDirectoryStats(_root);

    public ValueTask DisposeAsync()
    {
        _log?.Dispose();
        _log = null;
        _settings?.Dispose();
        _settings = null;
        return ValueTask.CompletedTask;
    }

    private FasterLog Log => _log ?? throw new InvalidOperationException("FASTER Log store is not initialized.");
}

internal sealed class ProductionGatePhaseZeroStore : IPhaseZeroStore
{
    private readonly IProductionGateStore _inner;

    public ProductionGatePhaseZeroStore(IProductionGateStore inner, string scope)
    {
        _inner = inner;
        Scope = scope;
    }

    public string EngineName => _inner.EngineName;

    public string DurabilityName => _inner.DurabilityModeName;

    public string Scope { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _inner.InitializeAsync(cancellationToken);

    public ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default) =>
        _inner.InsertAsync(record, cancellationToken);

    public async ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _inner.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return (snapshot.Count, snapshot.Values.Sum(static record => record.MaterializedChecksum));
    }

    public PhaseZeroStorageStats GetStorageStats()
    {
        var stats = _inner.GetStorageStats();
        return new PhaseZeroStorageStats(stats.Bytes, stats.Files);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

internal static class PhaseZeroStoreUtilities
{
    public static PhaseZeroStorageStats GetDirectoryStats(string root)
    {
        if (!Directory.Exists(root))
        {
            return default;
        }

        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(path).Length;
            files++;
        }

        return new PhaseZeroStorageStats(bytes, files);
    }
}
