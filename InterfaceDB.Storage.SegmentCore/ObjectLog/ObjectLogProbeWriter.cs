using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

public sealed class ObjectLogProbeWriter : IAsyncDisposable, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> ActiveWriterRoots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ObjectLogProbeOptions _options;
    private readonly IDurableFileSystem _durableFileSystem;
    private readonly string _rootDirectory;
    private readonly string _segmentsDirectory;
    private readonly FileStream _writerLease;
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly List<ObjectLogFormat.PooledFrame> _pendingBatchFrames = [];
    private readonly List<ObjectLogRecordAddress> _pendingBatchAddresses = [];
    private SafeFileHandle _segmentHandle;
    private string _activeSegmentPath;
    private long _activeSegmentId;
    private uint _activeSegmentHeaderChecksum;
    private long _activeOffset;
    private long _lastLsn;
    private long _highestSegmentId;
    private readonly long _repairedTailBytesOnOpen;
    private bool _faulted;
    private bool _disposed;

    private ObjectLogProbeWriter(
        ObjectLogProbeOptions options,
        IDurableFileSystem durableFileSystem,
        string rootDirectory,
        string segmentsDirectory,
        FileStream writerLease,
        SafeFileHandle segmentHandle,
        string activeSegmentPath,
        long activeSegmentId,
        uint activeSegmentHeaderChecksum,
        long activeOffset,
        long lastLsn,
        long highestSegmentId,
        long repairedTailBytesOnOpen)
    {
        _options = options;
        _durableFileSystem = durableFileSystem;
        _rootDirectory = rootDirectory;
        _segmentsDirectory = segmentsDirectory;
        _writerLease = writerLease;
        _segmentHandle = segmentHandle;
        _activeSegmentPath = activeSegmentPath;
        _activeSegmentId = activeSegmentId;
        _activeSegmentHeaderChecksum = activeSegmentHeaderChecksum;
        _activeOffset = activeOffset;
        _lastLsn = lastLsn;
        _highestSegmentId = highestSegmentId;
        _repairedTailBytesOnOpen = repairedTailBytesOnOpen;
    }

    public string DirectoryFlushStrategy => _durableFileSystem.DirectoryFlushStrategy;

    public long ActiveSegmentId => _activeSegmentId;

    public long LastLsn => _lastLsn;

    public ObjectLogWritePosition WritePosition => new(
        _activeSegmentId,
        _activeOffset,
        _lastLsn,
        _activeSegmentHeaderChecksum);

    public bool IsFaulted => _faulted;

    public long RepairedTailBytesOnOpen => _repairedTailBytesOnOpen;

    public static async ValueTask<ObjectLogProbeWriter> OpenAsync(
        string rootDirectory,
        ObjectLogProbeOptions? options = null,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(
            rootDirectory,
            options,
            durableFileSystem,
            recoveryPosition: null,
            replayRecord: null,
            cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<ObjectLogProbeWriter> OpenFromCheckpointAsync(
        string rootDirectory,
        ObjectLogWritePosition recoveryPosition,
        Action<ObjectLogReplayRecord> replayRecord,
        ObjectLogProbeOptions? options = null,
        IDurableFileSystem? durableFileSystem = null,
        CancellationToken cancellationToken = default) =>
        await OpenCoreAsync(
            rootDirectory,
            options,
            durableFileSystem,
            recoveryPosition,
            replayRecord,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<ObjectLogProbeWriter> OpenCoreAsync(
        string rootDirectory,
        ObjectLogProbeOptions? options,
        IDurableFileSystem? durableFileSystem,
        ObjectLogWritePosition? recoveryPosition,
        Action<ObjectLogReplayRecord>? replayRecord,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new ObjectLogProbeOptions();
        durableFileSystem ??= PlatformDurableFileSystem.Instance;
        var fullRoot = Path.GetFullPath(rootDirectory);
        var segmentsDirectory = Path.Combine(fullRoot, "segments");
        Directory.CreateDirectory(fullRoot);
        Directory.CreateDirectory(segmentsDirectory);
        if (!ActiveWriterRoots.TryAdd(fullRoot, 0))
        {
            throw new IOException($"Another Phase 0 object-log writer already owns '{fullRoot}'.");
        }

        FileStream writerLease;
        try
        {
            writerLease = new FileStream(
                Path.Combine(fullRoot, "writer.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            try
            {
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException(
                        "The Phase 0 writer lease is implemented only for Windows and Linux.");
                }

#pragma warning disable CA1416 // Phase 0 explicitly rejects platforms other than Windows and Linux above.
                writerLease.Lock(0, 1);
#pragma warning restore CA1416
            }
            catch
            {
                writerLease.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            ActiveWriterRoots.TryRemove(fullRoot, out _);
            if (exception is IOException)
            {
                throw new IOException(
                    $"Another Phase 0 object-log writer already owns '{fullRoot}'.",
                    exception);
            }

            throw;
        }

        try
        {
            var recovery = recoveryPosition is null
                ? ObjectLogRecovery.Recover(fullRoot, repairActiveTail: true, durableFileSystem)
                : ObjectLogTailRecovery.Recover(
                    fullRoot,
                    recoveryPosition.Value,
                    replayRecord ?? throw new ArgumentNullException(nameof(replayRecord)),
                    repairActiveTail: true,
                    durableFileSystem);
            string activePath;
            long activeSegmentId;
            uint activeHeaderChecksum;
            long activeOffset;
            var highestSegmentId = ObjectLogRecovery.FindHighestSegmentId(fullRoot);

            if (recovery.ActiveSegmentPath is null)
            {
                activeSegmentId = 1;
                activePath = Path.Combine(segmentsDirectory, ObjectLogFormat.SegmentFileName(activeSegmentId));
                activeHeaderChecksum = await CreateSegmentAsync(
                    activePath,
                    activeSegmentId,
                    previousSegmentId: 0,
                    previousHeaderChecksum: 0,
                    options,
                    durableFileSystem,
                    cancellationToken).ConfigureAwait(false);
                activeOffset = ObjectLogFormat.SegmentHeaderSize;
                highestSegmentId = Math.Max(highestSegmentId, activeSegmentId);
            }
            else
            {
                activePath = recovery.ActiveSegmentPath;
                activeSegmentId = recovery.ActiveSegmentId;
                activeHeaderChecksum = recovery.ActiveSegmentHeaderChecksum;
                activeOffset = recovery.ActiveLength;
            }

            var segmentHandle = OpenSegmentHandle(activePath, options);
            return new ObjectLogProbeWriter(
                options,
                durableFileSystem,
                fullRoot,
                segmentsDirectory,
                writerLease,
                segmentHandle,
                activePath,
                activeSegmentId,
                activeHeaderChecksum,
                activeOffset,
                recovery.LastLsn,
                highestSegmentId,
                recovery.RepairedTailBytes);
        }
        catch
        {
            await writerLease.DisposeAsync().ConfigureAwait(false);
            ActiveWriterRoots.TryRemove(fullRoot, out _);
            throw;
        }
    }

    public ValueTask<ObjectLogRecordAddress> AppendAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        AppendCoreAsync(ObjectLogRecordKind.Put, key, payload, transactionId: 0, cancellationToken);

    public ValueTask<ObjectLogRecordAddress> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default) =>
        AppendCoreAsync(ObjectLogRecordKind.Delete, key, ReadOnlyMemory<byte>.Empty, transactionId: 0, cancellationToken);

    internal ValueTask<ObjectLogRecordAddress> AppendTransactionalAsync(
        ObjectLogRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long transactionId,
        CancellationToken cancellationToken = default)
    {
        if (kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (transactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        return AppendCoreAsync(kind, key, payload, transactionId, cancellationToken);
    }

    internal async ValueTask<ObjectLogRecordAddress[]> AppendBatchAsync(
        IReadOnlyList<ObjectLogAppendRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var results = new ObjectLogRecordAddress[requests.Count];
        await AppendBatchIntoAsync(
            requests,
            requests.Count,
            results,
            cancellationToken).ConfigureAwait(false);
        return results;
    }

    internal async ValueTask AppendBatchIntoAsync(
        IReadOnlyList<ObjectLogAppendRequest> requests,
        int requestCount,
        ObjectLogRecordAddress[] results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(results);
        if (requestCount < 0 || requestCount > requests.Count || results.Length < requestCount)
        {
            throw new ArgumentOutOfRangeException(nameof(requestCount));
        }

        if (requestCount == 0)
        {
            return;
        }

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pendingFrames = _pendingBatchFrames;
        var pendingAddresses = _pendingBatchAddresses;
        var pendingBytes = 0;
        try
        {
            ThrowIfUnavailable();
            for (var index = 0; index < requestCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
                if (request.Kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete)
                    || request.TransactionId < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(requests));
                }

                var frame = ObjectLogFormat.CreateFrame(
                    request.Kind,
                    checked(_lastLsn + pendingFrames.Count + 1),
                    request.Key,
                    request.Payload.Span,
                    request.TransactionId);
                if ((_activeOffset > ObjectLogFormat.SegmentHeaderSize || pendingFrames.Count > 0)
                    && _activeOffset + pendingBytes + frame.Length > _options.NormalizedMaxSegmentBytes)
                {
                    frame.Dispose();
                    await FlushPendingFramesAsync(pendingFrames, pendingAddresses).ConfigureAwait(false);
                    pendingBytes = 0;
                    await RotateAsync().ConfigureAwait(false);
                    frame = ObjectLogFormat.CreateFrame(
                        request.Kind,
                        checked(_lastLsn + 1),
                        request.Key,
                        request.Payload.Span,
                        request.TransactionId);
                }
                else if (pendingFrames.Count > 0
                         && pendingBytes + frame.Length > _options.NormalizedMaximumCombinedWriteBytes)
                {
                    frame.Dispose();
                    await FlushPendingFramesAsync(pendingFrames, pendingAddresses).ConfigureAwait(false);
                    pendingBytes = 0;
                    frame = ObjectLogFormat.CreateFrame(
                        request.Kind,
                        checked(_lastLsn + 1),
                        request.Key,
                        request.Payload.Span,
                        request.TransactionId);
                }

                var address = new ObjectLogRecordAddress(
                    _activeSegmentId,
                    checked(_activeOffset + pendingBytes),
                    frame.Length,
                    checked(_lastLsn + pendingFrames.Count + 1));
                pendingFrames.Add(frame);
                pendingAddresses.Add(address);
                pendingBytes = checked(pendingBytes + frame.Length);
                results[index] = address;
            }

            await FlushPendingFramesAsync(pendingFrames, pendingAddresses).ConfigureAwait(false);
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            foreach (var frame in pendingFrames)
            {
                frame.Dispose();
            }

            pendingFrames.Clear();
            pendingAddresses.Clear();
            _appendGate.Release();
        }
    }

    internal async ValueTask ForceRotateAsync(CancellationToken cancellationToken = default)
    {
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            if (_activeOffset > ObjectLogFormat.SegmentHeaderSize)
            {
                await RotateAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _durableFileSystem.FlushFile(_segmentHandle);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _appendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_faulted
                    && _options.DurabilityMode == ObjectLogDurabilityMode.Performance
                    && _options.FlushOnDispose)
                {
                    _durableFileSystem.FlushFile(_segmentHandle);
                }
            }
            finally
            {
                _segmentHandle.Dispose();
                try
                {
                    await _writerLease.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    ActiveWriterRoots.TryRemove(_rootDirectory, out _);
                    _disposed = true;
                }
            }
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private async ValueTask<ObjectLogRecordAddress> AppendCoreAsync(
        ObjectLogRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long transactionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();

            var frame = ObjectLogFormat.CreateFrame(
                kind,
                checked(_lastLsn + 1),
                key,
                payload.Span,
                transactionId);
            try
            {
                if (_activeOffset > ObjectLogFormat.SegmentHeaderSize
                    && _activeOffset + frame.Length > _options.NormalizedMaxSegmentBytes)
                {
                    frame.Dispose();
                    await RotateAsync().ConfigureAwait(false);
                    frame = ObjectLogFormat.CreateFrame(
                        kind,
                        checked(_lastLsn + 1),
                        key,
                        payload.Span,
                        transactionId);
                }

                var address = new ObjectLogRecordAddress(
                    _activeSegmentId,
                    _activeOffset,
                    frame.Length,
                    checked(_lastLsn + 1));
                await WriteFrameAsync(frame, forceDurable: false).ConfigureAwait(false);
                _lastLsn = address.Lsn;
                return address;
            }
            catch
            {
                _faulted = true;
                throw;
            }
            finally
            {
                frame.Dispose();
            }
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private async ValueTask RotateAsync()
    {
        var nextSegmentId = checked(_highestSegmentId + 1);
        var nextPath = Path.Combine(_segmentsDirectory, ObjectLogFormat.SegmentFileName(nextSegmentId));
        var nextHeaderChecksum = await CreateSegmentAsync(
            nextPath,
            nextSegmentId,
            _activeSegmentId,
            _activeSegmentHeaderChecksum,
            _options,
            _durableFileSystem,
            CancellationToken.None).ConfigureAwait(false);

        using var seal = ObjectLogFormat.CreateFrame(
            ObjectLogRecordKind.Seal,
            checked(_lastLsn + 1),
            key: null,
            ReadOnlySpan<byte>.Empty,
            auxiliary: nextSegmentId);
        await WriteFrameAsync(seal, forceDurable: true).ConfigureAwait(false);
        _lastLsn++;

        _segmentHandle.Dispose();
        _segmentHandle = OpenSegmentHandle(nextPath, _options);
        _activeSegmentPath = nextPath;
        _activeSegmentId = nextSegmentId;
        _activeSegmentHeaderChecksum = nextHeaderChecksum;
        _activeOffset = ObjectLogFormat.SegmentHeaderSize;
        _highestSegmentId = nextSegmentId;
    }

    private async ValueTask FlushPendingFramesAsync(
        List<ObjectLogFormat.PooledFrame> frames,
        List<ObjectLogRecordAddress> addresses)
    {
        if (frames.Count == 0)
        {
            return;
        }

        var length = frames.Sum(static frame => frame.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var destination = buffer.AsMemory(0, length);
            var copied = 0;
            foreach (var frame in frames)
            {
                frame.Memory.CopyTo(destination[copied..]);
                copied += frame.Length;
            }

            await RandomAccess.WriteAsync(
                _segmentHandle,
                destination,
                _activeOffset,
                CancellationToken.None).ConfigureAwait(false);
            _activeOffset = checked(_activeOffset + length);
            _lastLsn = addresses[^1].Lsn;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            foreach (var frame in frames)
            {
                frame.Dispose();
            }

            frames.Clear();
            addresses.Clear();
        }
    }

    private async ValueTask WriteFrameAsync(ObjectLogFormat.PooledFrame frame, bool forceDurable)
    {
        await RandomAccess.WriteAsync(
            _segmentHandle,
            frame.Memory,
            _activeOffset,
            CancellationToken.None).ConfigureAwait(false);
        _activeOffset += frame.Length;
        var writeAlreadySuppliesBarrier = UsesWriteThrough(_options);
        if ((forceDurable && !writeAlreadySuppliesBarrier)
            || (_options.DurabilityMode == ObjectLogDurabilityMode.Durable
                && _options.DurabilityStrategy == ObjectLogDurabilityStrategy.ExplicitFlush))
        {
            _durableFileSystem.FlushFile(_segmentHandle);
        }
    }

    private static async ValueTask<uint> CreateSegmentAsync(
        string finalPath,
        long segmentId,
        long previousSegmentId,
        uint previousHeaderChecksum,
        ObjectLogProbeOptions options,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var header = ObjectLogFormat.CreateSegmentHeader(
            segmentId,
            previousSegmentId,
            previousHeaderChecksum,
            out var headerChecksum);
        var temporaryPath = $"{finalPath}.creating-{Guid.NewGuid():N}";

        try
        {
            using (var handle = File.OpenHandle(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       UsesWriteThrough(options) ? FileOptions.WriteThrough : FileOptions.None))
            {
                await RandomAccess.WriteAsync(handle, header, 0, cancellationToken).ConfigureAwait(false);
                if (!UsesWriteThrough(options))
                {
                    durableFileSystem.FlushFile(handle);
                }
            }

            durableFileSystem.PublishFile(temporaryPath, finalPath);
            return headerChecksum;
        }
        catch
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
    }

    private static SafeFileHandle OpenSegmentHandle(string path, ObjectLogProbeOptions options) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous
            | FileOptions.RandomAccess
            | (UsesWriteThrough(options) ? FileOptions.WriteThrough : FileOptions.None));

    private static bool UsesWriteThrough(ObjectLogProbeOptions options) =>
        options.DurabilityMode == ObjectLogDurabilityMode.Durable
        && options.DurabilityStrategy == ObjectLogDurabilityStrategy.WriteThrough;

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A crash-only temporary is deliberately outside the recovery glob.
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException(
                $"The Phase 0 object-log writer for '{_rootDirectory}' is faulted and cannot acknowledge more writes.");
        }
    }
}
