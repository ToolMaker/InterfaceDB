using System.Buffers;
using System.Threading.Channels;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Internal;

internal readonly record struct AtlasGroupReadResult(
    bool Found,
    bool Deleted,
    byte[]? Payload,
    AtlasGroupAddress Address)
{
    public static AtlasGroupReadResult Missing { get; } = new(false, false, null, default);

    public static AtlasGroupReadResult Tombstone(AtlasGroupAddress address) =>
        new(true, true, null, address);
}

internal readonly record struct AtlasGroupCheckpointResult(
    int Entries,
    long Bytes,
    long Generation,
    long RetainedTransactionFloor);

internal readonly record struct AtlasGroupIntegrityResult(long Frames, long PayloadBytes);

internal readonly record struct AtlasPhysicalRead(
    int OutputIndex,
    int Group,
    string Key,
    AtlasGroupAddress Address);

internal readonly record struct AtlasGroupBulkReadResult(
    long Frames,
    long Windows,
    long Bytes);

internal sealed class AtlasGroupStore : IAsyncDisposable
{
    private readonly int _groupId;
    private readonly string _rootDirectory;
    private readonly string _segmentsDirectory;
    private readonly AtlasCoreOptions _options;
    private readonly IDurableFileSystem _durableFileSystem;
    private readonly Dictionary<string, AtlasGroupAddress> _index;
    private readonly ReaderWriterLockSlim _indexLock = new(LockRecursionPolicy.NoRecursion);
    private readonly SegmentReadHandleCache _readHandleCache;
    private readonly bool _ownsReadHandleCache;
    private readonly Channel<GroupCommand> _commands;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly Queue<long> _retainedTransactionWatermarks = new();
    private readonly Task _worker;
    private SafeFileHandle _writeHandle;
    private SafeFileHandle _bufferedWriteHandle;
    private long _activeSegmentId;
    private long _activeOffset;
    private long _lastLsn;
    private long _checkpointGeneration;
    private long _checkpointTransactionWatermark;
    private long _repairedTailBytes;
    private long _scannedFrames;
    private long _physicalBytes;
    private long _replayedWalMutations;
    private long _indexVersion;
    private int _liveCount;
    private long _liveBytes;
    private GroupCheckpointCapture? _lastCheckpointCapture;
    private long _lastWrittenCheckpointLsn = -1;
    private long _lastWrittenCheckpointIndexVersion = -1;
    private int _faulted;
    private int _disposed;

    private AtlasGroupStore(
        int groupId,
        string rootDirectory,
        string segmentsDirectory,
        AtlasCoreOptions options,
        IDurableFileSystem durableFileSystem,
        SegmentReadHandleCache readHandleCache,
        bool ownsReadHandleCache,
        Dictionary<string, AtlasGroupAddress> index,
        SafeFileHandle writeHandle,
        SafeFileHandle bufferedWriteHandle,
        long activeSegmentId,
        long activeOffset,
        long lastLsn,
        long checkpointGeneration,
        long checkpointTransactionWatermark,
        int retainedCheckpointCount,
        long repairedTailBytes,
        long scannedFrames,
        long physicalBytes)
    {
        _groupId = groupId;
        _rootDirectory = rootDirectory;
        _segmentsDirectory = segmentsDirectory;
        _options = options;
        _durableFileSystem = durableFileSystem;
        _readHandleCache = readHandleCache;
        _ownsReadHandleCache = ownsReadHandleCache;
        _index = index;
        foreach (var address in index.Values)
            if (address.Kind == AtlasGroupRecordKind.Put) { _liveCount++; _liveBytes += address.FrameLength; }
        _writeHandle = writeHandle;
        _bufferedWriteHandle = bufferedWriteHandle;
        _activeSegmentId = activeSegmentId;
        _activeOffset = activeOffset;
        _lastLsn = lastLsn;
        _checkpointGeneration = checkpointGeneration;
        _checkpointTransactionWatermark = checkpointTransactionWatermark;
        for (var retainedIndex = 1; retainedIndex < retainedCheckpointCount; retainedIndex++)
        {
            _retainedTransactionWatermarks.Enqueue(0);
        }

        if (retainedCheckpointCount > 0)
        {
            _retainedTransactionWatermarks.Enqueue(checkpointTransactionWatermark);
        }
        _repairedTailBytes = repairedTailBytes;
        _scannedFrames = scannedFrames;
        _physicalBytes = physicalBytes;
        _commands = Channel.CreateBounded<GroupCommand>(new BoundedChannelOptions(options.QueueCapacityPerGroup)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(RunWorkerAsync);
    }

    public int Count
    {
        get
        {
            _indexLock.EnterReadLock();
            try
            {
                return _index.Count;
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }
    }

    public long RepairedTailBytes => Volatile.Read(ref _repairedTailBytes);
    internal int LiveCount => Volatile.Read(ref _liveCount);
    internal long LiveBytes => Volatile.Read(ref _liveBytes);

    // Called only under the existing index write lock; no additional writer gate is introduced.
    private void SetAddress(string key, AtlasGroupAddress address)
    {
        if (_index.TryGetValue(key, out var previous) && previous.Kind == AtlasGroupRecordKind.Put)
        { _liveCount--; _liveBytes -= previous.FrameLength; }
        _index[key] = address;
        if (address.Kind == AtlasGroupRecordKind.Put) { _liveCount++; _liveBytes += address.FrameLength; }
    }

    public long ScannedFrames => Volatile.Read(ref _scannedFrames);

    public long PhysicalBytes => Volatile.Read(ref _physicalBytes);

    public long ReplayedWalMutations => Volatile.Read(ref _replayedWalMutations);

    public long SegmentFileCount => Volatile.Read(ref _activeSegmentId);

    public long CheckpointGeneration => Volatile.Read(ref _checkpointGeneration);

    public long CheckpointTransactionWatermark =>
        Volatile.Read(ref _checkpointTransactionWatermark);

    internal long IndexVersion => Volatile.Read(ref _indexVersion);

    public bool TryGetAddress(string key, out AtlasGroupAddress address)
    {
        ThrowIfUnavailable();
        _indexLock.EnterReadLock();
        try
        {
            return _index.TryGetValue(key, out address);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    public KeyValuePair<string, AtlasGroupAddress>[] SnapshotIndex()
        => SnapshotIndex(out _);

    internal KeyValuePair<string, AtlasGroupAddress>[] SnapshotIndex(out long version)
    {
        ThrowIfUnavailable();
        _indexLock.EnterReadLock();
        try
        {
            version = _indexVersion;
            return _index.ToArray();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    public AtlasGroupIntegrityResult VerifyIntegrity()
    {
        ThrowIfUnavailable();
        var candidates = Directory
            .EnumerateFiles(_segmentsDirectory, "segment-*.idbag", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: AtlasGroupLogFormat.TryParseSegmentId(path, out var id), Id: id))
            .Where(static item => item.Parsed)
            .OrderBy(static item => item.Id)
            .ToArray();
        long expectedLsn = 1;
        long frames = 0;
        long payloadBytes = 0;
        var segmentHeader = new byte[AtlasGroupLogFormat.SegmentHeaderBytes];
        var frameHeader = new byte[AtlasGroupLogFormat.FrameHeaderBytes];
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            if (candidate.Id != index + 1)
            {
                throw new InvalidDataException("AtlasCore group segment chain is not contiguous.");
            }

            using var handle = File.OpenHandle(
                candidate.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            ReadExactly(handle, segmentHeader, 0, candidate.Path);
            AtlasGroupLogFormat.ValidateSegmentHeader(
                segmentHeader,
                candidate.Id,
                candidate.Id - 1,
                candidate.Path);
            var length = RandomAccess.GetLength(handle);
            var offset = (long)AtlasGroupLogFormat.SegmentHeaderBytes;
            while (offset < length)
            {
                if (length - offset < AtlasGroupLogFormat.FrameHeaderBytes)
                {
                    throw new InvalidDataException(
                        $"AtlasCore segment '{candidate.Path}' ends inside a frame header.");
                }

                ReadExactly(handle, frameHeader, offset, candidate.Path);
                var parsed = AtlasGroupLogFormat.ParseHeader(frameHeader, candidate.Path, offset);
                if (parsed.Lsn != expectedLsn)
                {
                    throw new InvalidDataException(
                        $"AtlasCore group LSN sequence breaks at {candidate.Path}:{offset:N0}.");
                }

                var rented = ArrayPool<byte>.Shared.Rent(parsed.FrameLength);
                try
                {
                    var frame = rented.AsSpan(0, parsed.FrameLength);
                    ReadExactly(handle, frame, offset, candidate.Path);
                    AtlasGroupLogFormat.ValidateFrame(frame, parsed, candidate.Path, offset);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }

                expectedLsn++;
                frames++;
                payloadBytes += parsed.PayloadLength;
                offset = checked(offset + parsed.FrameLength);
            }
        }

        return new AtlasGroupIntegrityResult(frames, payloadBytes);
    }

    public static async ValueTask<AtlasGroupStore> OpenAsync(
        string rootDirectory,
        AtlasCoreOptions options,
        AtlasTransactionLog transactionLog,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken = default,
        int groupId = -1,
        bool recoverMutationWal = true,
        SegmentReadHandleCache? readHandleCache = null)
    {
        var fullRoot = Path.GetFullPath(rootDirectory);
        var segmentsDirectory = Path.Combine(fullRoot, "segments");
        var rootExisted = Directory.Exists(fullRoot);
        Directory.CreateDirectory(fullRoot);
        Directory.CreateDirectory(segmentsDirectory);
        if (!rootExisted)
        {
            durableFileSystem.FlushDirectory(Path.GetDirectoryName(fullRoot)!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var retainedCheckpointCount = Math.Min(
            options.CheckpointGenerationsToKeep,
            AtlasGroupCheckpoint.CountCandidates(fullRoot));
        var checkpoint = AtlasGroupCheckpoint.TryLoadLatest(
            fullRoot,
            options.MaximumRecoveryMetadataBytes);
        var recovery = Recover(
            fullRoot,
            segmentsDirectory,
            checkpoint,
            transactionLog,
            durableFileSystem);
        if (recovery.ActiveSegmentId == 0)
        {
            await CreateSegmentAsync(
                    segmentsDirectory,
                    segmentId: 1,
                    previousSegmentId: 0,
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            recovery = recovery with
            {
                ActiveSegmentId = 1,
                ActiveOffset = AtlasGroupLogFormat.SegmentHeaderBytes,
                PhysicalBytes = AtlasGroupLogFormat.SegmentHeaderBytes
            };
        }

        var activePath = Path.Combine(
            segmentsDirectory,
            AtlasGroupLogFormat.SegmentFileName(recovery.ActiveSegmentId));
        var writeHandle = OpenWriteHandle(activePath, options.DurabilityStrategy);
        var bufferedWriteHandle = OpenBufferedWriteHandle(activePath);
        var resolvedReadHandleCache = readHandleCache
            ?? new SegmentReadHandleCache(options.MaximumOpenReadHandles);
        var store = new AtlasGroupStore(
            groupId,
            fullRoot,
            segmentsDirectory,
            options,
            durableFileSystem,
            resolvedReadHandleCache,
            readHandleCache is null,
            recovery.Index,
            writeHandle,
            bufferedWriteHandle,
            recovery.ActiveSegmentId,
            recovery.ActiveOffset,
            recovery.LastLsn,
            checkpoint?.Generation ?? 0,
            checkpoint?.TransactionWatermark ?? 0,
            retainedCheckpointCount,
            recovery.RepairedTailBytes,
            recovery.ScannedFrames,
            recovery.PhysicalBytes);
        try
        {
            if (recoverMutationWal
                && groupId >= 0
                && transactionLog.Protocol == AtlasCoreTransactionProtocol.MutationWriteAheadLog)
            {
                await store.RecoverCommittedWalAsync(
                        transactionLog,
                        recovery.ObservedTransactionMutations,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<AtlasGroupAddress> PutAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        long placementGeneration,
        CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(
            new WriteCommand(
                AtlasGroupRecordKind.Put,
                key,
                payload,
                placementGeneration,
                Guid.Empty,
                requireDurability: true,
                publishImmediately: true),
            cancellationToken);

    public ValueTask<AtlasGroupAddress> DeleteAsync(
        string key,
        long placementGeneration,
        CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(
            new WriteCommand(
                AtlasGroupRecordKind.Delete,
                key,
                ReadOnlyMemory<byte>.Empty,
                placementGeneration,
                Guid.Empty,
                requireDurability: true,
                publishImmediately: true),
            cancellationToken);

    public ValueTask<AtlasGroupAddress> PreparePutAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        long placementGeneration,
        Guid transactionId,
        bool requireDurability = true,
        CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(
            new WriteCommand(
                AtlasGroupRecordKind.Put,
                key,
                payload,
                placementGeneration,
                transactionId,
                requireDurability,
                publishImmediately: false),
            cancellationToken);

    public ValueTask<AtlasGroupAddress> PrepareDeleteAsync(
        string key,
        long placementGeneration,
        Guid transactionId,
        bool requireDurability = true,
        CancellationToken cancellationToken = default) =>
        EnqueueWriteAsync(
            new WriteCommand(
                AtlasGroupRecordKind.Delete,
                key,
                ReadOnlyMemory<byte>.Empty,
                placementGeneration,
                transactionId,
                requireDurability,
                publishImmediately: false),
            cancellationToken);

    public void PublishPrepared(string key, AtlasGroupAddress address)
    {
        ThrowIfUnavailable();
        _indexLock.EnterWriteLock();
        try
        {
            SetAddress(key, address);
            Interlocked.Increment(ref _indexVersion);
            _lastCheckpointCapture = null;
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }

    private async ValueTask RecoverCommittedWalAsync(
        AtlasTransactionLog transactionLog,
        Dictionary<AtlasPreparedMutationIdentity, AtlasGroupAddress> observed,
        CancellationToken cancellationToken)
    {
        var recoveryMutations = await transactionLog.GetRecoveryMutationsAsync(
                _groupId,
                CheckpointTransactionWatermark,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var recovery in recoveryMutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new AtlasPreparedMutationIdentity(
                recovery.TransactionId,
                recovery.Mutation.Key);
            if (observed.TryGetValue(identity, out var existingAddress))
            {
                ValidateRecoveredWalMutation(
                    recovery.TransactionId,
                    recovery.Mutation,
                    existingAddress);
                continue;
            }

            var mutation = recovery.Mutation;
            var address = await EnqueueWriteAsync(
                    new WriteCommand(
                        mutation.Kind,
                        mutation.Key,
                        mutation.Payload,
                        mutation.ExpectedAddress.PlacementGeneration,
                        recovery.TransactionId,
                        requireDurability: true,
                        publishImmediately: false),
                    CancellationToken.None)
                .ConfigureAwait(false);
            PublishPrepared(mutation.Key, address);
            observed[identity] = address;
            Interlocked.Increment(ref _replayedWalMutations);
        }
    }

    private void ValidateRecoveredWalMutation(
        Guid transactionId,
        AtlasWalMutation mutation,
        AtlasGroupAddress address)
    {
        using var readHandle = AcquireSynchronousReadHandle(address.SegmentId);
        var rented = ArrayPool<byte>.Shared.Rent(address.FrameLength);
        try
        {
            var frame = rented.AsSpan(0, address.FrameLength);
            ReadExactly(readHandle.Handle, frame, address.Offset, readHandle.Path);
            var header = AtlasGroupLogFormat.ParseHeader(
                frame[..AtlasGroupLogFormat.FrameHeaderBytes],
                readHandle.Path,
                address.Offset);
            AtlasGroupLogFormat.ValidateFrame(frame, header, readHandle.Path, address.Offset);
            var storedPayload = frame.Slice(
                AtlasGroupLogFormat.FrameHeaderBytes + header.KeyLength,
                header.PayloadLength);
            if (header.TransactionId != transactionId
                || header.Kind != mutation.Kind
                || header.Lsn != address.Lsn
                || header.PlacementGeneration != mutation.ExpectedAddress.PlacementGeneration
                || !AtlasGroupLogFormat.KeyEquals(frame, header, mutation.Key)
                || !storedPayload.SequenceEqual(mutation.Payload))
            {
                throw new InvalidDataException(
                    $"AtlasCore group {_groupId:N0} contains a participant frame that disagrees with its durable transaction WAL record.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    public ValueTask<AtlasGroupReadResult> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        AtlasGroupAddress address;
        _indexLock.EnterReadLock();
        try
        {
            if (!_index.TryGetValue(key, out address))
            {
                return ValueTask.FromResult(AtlasGroupReadResult.Missing);
            }
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        if (address.Kind == AtlasGroupRecordKind.Delete)
        {
            return ValueTask.FromResult(AtlasGroupReadResult.Tombstone(address));
        }

        return ReadAtAsync(key, address, cancellationToken);
    }

    public ValueTask<AtlasGroupReadResult> ReadAtAsync(
        string key,
        AtlasGroupAddress address,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (address.Kind == AtlasGroupRecordKind.Delete)
        {
            return ValueTask.FromResult(AtlasGroupReadResult.Tombstone(address));
        }

        if (address.FrameLength <= 64 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var readHandle = AcquireSynchronousReadHandle(address.SegmentId, cancellationToken);
            var rented = ArrayPool<byte>.Shared.Rent(address.FrameLength);
            try
            {
                var frame = rented.AsSpan(0, address.FrameLength);
                ReadExactly(readHandle.Handle, frame, address.Offset, readHandle.Path);
                return ValueTask.FromResult(ReadValidated(key, address, frame, readHandle.Path));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }

        var asynchronousHandle = AcquireAsynchronousReadHandle(
            address.SegmentId,
            cancellationToken);
        return ReadLargeAsync(key, address, asynchronousHandle, cancellationToken);
    }

    internal async ValueTask<AtlasGroupBulkReadResult> ReadManyFromSegmentAsync(
        ReadOnlyMemory<AtlasPhysicalRead> reads,
        AtlasCoreRecord[] destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(destination);
        if (reads.IsEmpty)
        {
            return default;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var segmentId = reads.Span[0].Address.SegmentId;
        using var readHandle = AcquireAsynchronousReadHandle(segmentId, cancellationToken);
        long framesRead = 0;
        long windowsRead = 0;
        long bytesRead = 0;
        var cursor = 0;
        while (cursor < reads.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = reads.Span[cursor];
            ValidatePhysicalRead(first, _groupId, segmentId, destination.Length);
            var windowStart = first.Address.Offset;
            var windowEnd = checked(windowStart + first.Address.FrameLength);
            var end = cursor + 1;
            while (end < reads.Length)
            {
                var candidate = reads.Span[end];
                ValidatePhysicalRead(candidate, _groupId, segmentId, destination.Length);
                var candidateStart = candidate.Address.Offset;
                if (candidateStart < windowStart)
                {
                    throw new InvalidDataException(
                        $"AtlasCore segment {segmentId:N0} bulk reads are not ordered by physical offset.");
                }

                var candidateEnd = checked(candidateStart + candidate.Address.FrameLength);
                if (candidateStart - windowEnd > _options.MaximumReadGapBytes
                    || candidateEnd - windowStart > _options.MaximumReadWindowBytes)
                {
                    break;
                }

                windowEnd = Math.Max(windowEnd, candidateEnd);
                end++;
            }

            var windowLength = checked((int)(windowEnd - windowStart));
            var rented = ArrayPool<byte>.Shared.Rent(windowLength);
            try
            {
                var window = rented.AsMemory(0, windowLength);
                await ReadExactlyAsync(
                        readHandle.Handle,
                        window,
                        windowStart,
                        readHandle.Path,
                        cancellationToken)
                    .ConfigureAwait(false);

                for (var index = cursor; index < end; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = reads.Span[index];
                    var relativeOffset = checked((int)(item.Address.Offset - windowStart));
                    var frame = window.Span.Slice(relativeOffset, item.Address.FrameLength);
                    var stored = ReadValidated(item.Key, item.Address, frame, readHandle.Path);
                    if (!stored.Found || stored.Deleted || stored.Payload is null)
                    {
                        throw new InvalidDataException(
                            $"AtlasCore live locator for '{item.Key}' did not resolve to a payload.");
                    }

                    destination[item.OutputIndex] = new AtlasCoreRecord(item.Key, stored.Payload);
                    framesRead++;
                }

                windowsRead++;
                bytesRead += windowLength;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }

            cursor = end;
        }

        return new AtlasGroupBulkReadResult(framesRead, windowsRead, bytesRead);
    }

    private static async ValueTask<AtlasGroupReadResult> ReadLargeAsync(
        string key,
        AtlasGroupAddress address,
        SegmentReadHandleLease readHandle,
        CancellationToken cancellationToken)
    {
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(address.FrameLength);
            var frame = rented.AsMemory(0, address.FrameLength);
            await ReadExactlyAsync(
                    readHandle.Handle,
                    frame,
                    address.Offset,
                    readHandle.Path,
                    cancellationToken)
                .ConfigureAwait(false);
            return ReadValidated(key, address, frame.Span, readHandle.Path);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
            readHandle.Dispose();
        }
    }

    private static AtlasGroupReadResult ReadValidated(
        string key,
        AtlasGroupAddress address,
        ReadOnlySpan<byte> frame,
        string path)
    {
        var header = AtlasGroupLogFormat.ParseHeader(
            frame[..AtlasGroupLogFormat.FrameHeaderBytes],
            path,
            address.Offset);
        AtlasGroupLogFormat.ValidateFrame(frame, header, path, address.Offset);
        if (header.Kind != address.Kind
            || header.Lsn != address.Lsn
            || header.PlacementGeneration != address.PlacementGeneration
            || !AtlasGroupLogFormat.KeyEquals(frame, header, key))
        {
            throw new InvalidDataException($"AtlasCore locator for '{key}' points to a different physical record.");
        }

        return new AtlasGroupReadResult(
            true,
            false,
            AtlasGroupLogFormat.ReadPayload(frame, header),
            address);
    }

    private static void ValidatePhysicalRead(
        AtlasPhysicalRead read,
        int expectedGroupId,
        long expectedSegmentId,
        int destinationLength)
    {
        var address = read.Address;
        if (read.OutputIndex < 0
            || read.OutputIndex >= destinationLength
            || read.Group != expectedGroupId
            || string.IsNullOrWhiteSpace(read.Key)
            || address.Kind != AtlasGroupRecordKind.Put
            || address.SegmentId != expectedSegmentId
            || address.SegmentId <= 0
            || address.Offset < AtlasGroupLogFormat.SegmentHeaderBytes
            || address.FrameLength < AtlasGroupLogFormat.FrameHeaderBytes
                + AtlasGroupLogFormat.FrameFooterBytes
            || address.FrameLength > AtlasGroupLogFormat.MaximumFrameBytes
            || address.Lsn <= 0
            || address.PlacementGeneration <= 0)
        {
            throw new InvalidDataException(
                $"AtlasCore contains an invalid bulk-read locator for '{read.Key}'.");
        }
    }

    public async ValueTask<AtlasGroupCheckpointResult> CheckpointAsync(
        long transactionWatermark,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new CheckpointCommand();
            await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            var capture = await command.Completion.Task.ConfigureAwait(false);
            // A stable group still publishes advancing transaction watermarks until
            // every retained fallback root permits the same WAL recycling floor.
            if (capture.LastLsn == _lastWrittenCheckpointLsn
                && capture.IndexVersion == _lastWrittenCheckpointIndexVersion
                && transactionWatermark == _checkpointTransactionWatermark
                && _checkpointGeneration > 0
                && _retainedTransactionWatermarks.All(value => value == transactionWatermark))
            {
                return new AtlasGroupCheckpointResult(
                    capture.Entries.Count, 0, _checkpointGeneration, transactionWatermark);
            }

            var generation = checked(Volatile.Read(ref _checkpointGeneration) + 1);
            var state = new AtlasGroupCheckpointState(
                generation,
                capture.SegmentId,
                capture.Offset,
                capture.LastLsn,
                transactionWatermark,
                capture.Entries,
                0);
            var written = await AtlasGroupCheckpoint.WriteAsync(
                    _rootDirectory,
                    state,
                    _options.CheckpointGenerationsToKeep,
                    _durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _checkpointGeneration, generation);
            Volatile.Write(ref _checkpointTransactionWatermark, transactionWatermark);
            _lastWrittenCheckpointLsn = capture.LastLsn;
            _lastWrittenCheckpointIndexVersion = capture.IndexVersion;
            _retainedTransactionWatermarks.Enqueue(transactionWatermark);
            while (_retainedTransactionWatermarks.Count > _options.CheckpointGenerationsToKeep)
            {
                _ = _retainedTransactionWatermarks.Dequeue();
            }

            var retainedFloor = _retainedTransactionWatermarks.Count == 0
                ? 0
                : _retainedTransactionWatermarks.Min();
            return new AtlasGroupCheckpointResult(
                written.Entries.Count,
                written.Bytes,
                generation,
                retainedFloor);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        Exception? workerFailure = null;
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            workerFailure = exception;
        }

        Exception? flushFailure = null;
        try
        {
            _durableFileSystem.FlushFile(_bufferedWriteHandle);
        }
        catch (Exception exception)
        {
            flushFailure = exception;
        }

        _writeHandle.Dispose();
        _bufferedWriteHandle.Dispose();
        if (_ownsReadHandleCache)
        {
            await _readHandleCache.DisposeAsync().ConfigureAwait(false);
        }
        _checkpointGate.Dispose();
        _indexLock.Dispose();
        if (workerFailure is not null && flushFailure is not null)
        {
            throw new AggregateException(workerFailure, flushFailure);
        }

        if (workerFailure is not null)
        {
            throw workerFailure;
        }

        if (flushFailure is not null)
        {
            throw flushFailure;
        }
    }

    private ValueTask<AtlasGroupAddress> EnqueueWriteAsync(
        WriteCommand command,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        _ = AtlasGroupLogFormat.CalculateFrameLength(
            command.Kind,
            lsn: 1,
            command.PlacementGeneration,
            command.Key,
            command.Payload.Length);
        if (_commands.Writer.TryWrite(command))
        {
            return new ValueTask<AtlasGroupAddress>(command.Completion.Task);
        }

        return EnqueueWriteSlowAsync(command, cancellationToken);
    }

    private async ValueTask<AtlasGroupAddress> EnqueueWriteSlowAsync(
        WriteCommand command,
        CancellationToken cancellationToken)
    {
        await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        return await command.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        GroupCommand? pending = null;
        try
        {
            while (pending is not null || await _commands.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var command = pending ?? await _commands.Reader.ReadAsync().ConfigureAwait(false);
                pending = null;
                if (command is CheckpointCommand checkpoint)
                {
                    try
                    {
                        if (_options.WriteScheduler is { } checkpointScheduler) await checkpointScheduler.EnterAsync().ConfigureAwait(false);
                        try
                        {
                        _durableFileSystem.FlushFile(_bufferedWriteHandle);
                        checkpoint.Completion.TrySetResult(CaptureCheckpoint());
                        }
                        finally { _options.WriteScheduler?.Exit(); }
                    }
                    catch (Exception exception)
                    {
                        checkpoint.Completion.TrySetException(exception);
                        throw;
                    }

                    continue;
                }

                var writes = new List<WriteCommand>(_options.MaximumBatchRecords)
                {
                    (WriteCommand)command
                };
                if (_options.GroupCommitDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.GroupCommitDelay).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }

                while (writes.Count < _options.MaximumBatchRecords && _commands.Reader.TryRead(out var next))
                {
                    if (next is WriteCommand write
                        && write.RequireDurability == writes[0].RequireDurability)
                    {
                        writes.Add(write);
                    }
                    else
                    {
                        pending = next;
                        break;
                    }
                }

                if (_options.WriteScheduler is { } batchScheduler) await batchScheduler.EnterAsync().ConfigureAwait(false);
                try { await WriteBatchAsync(writes).ConfigureAwait(false); }
                finally { _options.WriteScheduler?.Exit(); }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _faulted, 1);
            if (pending is not null)
            {
                pending.Fail(exception);
            }

            while (_commands.Reader.TryRead(out var command))
            {
                command.Fail(exception);
            }

            throw;
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<WriteCommand> writes)
    {
        var pendingFrames = new List<PendingFrame>(writes.Count);
        var pendingBytes = 0;
        foreach (var write in writes)
        {
            int frameLength;
            try
            {
                frameLength = AtlasGroupLogFormat.CalculateFrameLength(
                    write.Kind,
                    checked(_lastLsn + pendingFrames.Count + 1),
                    write.PlacementGeneration,
                    write.Key,
                    write.Payload.Length);
            }
            catch (Exception exception)
            {
                write.Completion.TrySetException(exception);
                continue;
            }

            if ((_activeOffset > AtlasGroupLogFormat.SegmentHeaderBytes || pendingFrames.Count > 0)
                && _activeOffset + pendingBytes + frameLength > _options.MaximumSegmentBytes)
            {
                await FlushFramesAsync(pendingFrames, pendingBytes).ConfigureAwait(false);
                pendingFrames.Clear();
                pendingBytes = 0;
                await RotateAsync().ConfigureAwait(false);
            }
            else if (pendingFrames.Count > 0
                     && pendingBytes + frameLength > _options.MaximumCombinedWriteBytes)
            {
                await FlushFramesAsync(pendingFrames, pendingBytes).ConfigureAwait(false);
                pendingFrames.Clear();
                pendingBytes = 0;
            }

            pendingFrames.Add(new PendingFrame(
                write,
                new AtlasGroupAddress(
                    write.Kind,
                    _activeSegmentId,
                    checked(_activeOffset + pendingBytes),
                    frameLength,
                    checked(_lastLsn + pendingFrames.Count + 1),
                    write.PlacementGeneration)));
            pendingBytes = checked(pendingBytes + frameLength);
        }

        await FlushFramesAsync(pendingFrames, pendingBytes).ConfigureAwait(false);
    }

    private async Task FlushFramesAsync(List<PendingFrame> frames, int totalBytes)
    {
        if (frames.Count == 0)
        {
            return;
        }

        var requireDurability = frames[0].Command.RequireDurability;
        if (frames.Any(frame => frame.Command.RequireDurability != requireDurability))
        {
            throw new InvalidOperationException(
                "AtlasCore cannot combine durable and WAL-derived participant frames in one physical write.");
        }

        var combined = ArrayPool<byte>.Shared.Rent(totalBytes);
        var handle = requireDurability ? _writeHandle : _bufferedWriteHandle;
        try
        {
            var cursor = 0;
            foreach (var frame in frames)
            {
                AtlasGroupLogFormat.WriteFrame(
                    combined.AsSpan(cursor, frame.Address.FrameLength),
                    frame.Command.Kind,
                    frame.Address.Lsn,
                    frame.Command.PlacementGeneration,
                    frame.Command.Key,
                    frame.Command.Payload.Span,
                    frame.Command.TransactionId);
                cursor += frame.Address.FrameLength;
            }

            await RandomAccess.WriteAsync(
                    handle,
                    combined.AsMemory(0, totalBytes),
                    _activeOffset,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (requireDurability
                && _options.DurabilityStrategy == AtlasCoreDurabilityStrategy.ExplicitFlush)
            {
                _durableFileSystem.FlushFile(handle);
            }
        }
        catch (Exception exception)
        {
            foreach (var frame in frames)
            {
                frame.Command.Completion.TrySetException(exception);
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(combined, clearArray: false);
        }

        _activeOffset = checked(_activeOffset + totalBytes);
        Interlocked.Add(ref _physicalBytes, totalBytes);
        _lastLsn = frames[^1].Address.Lsn;
        _indexLock.EnterWriteLock();
        try
        {
            foreach (var frame in frames)
            {
                if (frame.Command.PublishImmediately)
                {
                    SetAddress(frame.Command.Key, frame.Address);
                    Interlocked.Increment(ref _indexVersion);
                    _lastCheckpointCapture = null;
                }
            }
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }

        foreach (var frame in frames)
        {
            frame.Command.Completion.TrySetResult(frame.Address);
        }
    }

    private GroupCheckpointCapture CaptureCheckpoint()
    {
        _indexLock.EnterReadLock();
        try
        {
            if (_lastCheckpointCapture is { } previous
                && previous.LastLsn == _lastLsn
                && previous.IndexVersion == _indexVersion)
            {
                return previous;
            }

            var entries = new Dictionary<string, AtlasGroupAddress>(_index, StringComparer.Ordinal);
            return _lastCheckpointCapture = new GroupCheckpointCapture(
                _activeSegmentId, _activeOffset, _lastLsn, _indexVersion, entries);
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }

    private async Task RotateAsync()
    {
        _writeHandle.Dispose();
        _bufferedWriteHandle.Dispose();
        var next = checked(_activeSegmentId + 1);
        await CreateSegmentAsync(
                _segmentsDirectory,
                next,
                _activeSegmentId,
                _durableFileSystem,
                CancellationToken.None)
            .ConfigureAwait(false);
        _activeSegmentId = next;
        _activeOffset = AtlasGroupLogFormat.SegmentHeaderBytes;
        Interlocked.Add(ref _physicalBytes, AtlasGroupLogFormat.SegmentHeaderBytes);
        var path = Path.Combine(_segmentsDirectory, AtlasGroupLogFormat.SegmentFileName(next));
        _writeHandle = OpenWriteHandle(path, _options.DurabilityStrategy);
        _bufferedWriteHandle = OpenBufferedWriteHandle(path);
    }

    internal SegmentReadHandleLease AcquireSynchronousReadHandle(
        long segmentId,
        CancellationToken cancellationToken = default) =>
        AcquireReadHandle(segmentId, FileOptions.RandomAccess, cancellationToken);

    internal SegmentReadHandleLease AcquireAsynchronousReadHandle(
        long segmentId,
        CancellationToken cancellationToken = default) =>
        AcquireReadHandle(
            segmentId,
            FileOptions.Asynchronous | FileOptions.RandomAccess,
            cancellationToken);

    private sealed record ReadHandleHint(long SegmentId, SegmentReadHandleCache.CacheEntry Entry);
    private ReadHandleHint? _synchronousReadHint;
    private ReadHandleHint? _asynchronousReadHint;

    private SegmentReadHandleLease AcquireReadHandle(
        long segmentId,
        FileOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ref var hintSlot = ref ((options & FileOptions.Asynchronous) != 0
            ? ref _asynchronousReadHint : ref _synchronousReadHint);
        var hint = Volatile.Read(ref hintSlot);
        if (hint is not null && hint.SegmentId == segmentId
            && _readHandleCache.TryAcquire(hint.Entry, out var cached)) return cached;
        var path = Path.Combine(_segmentsDirectory, AtlasGroupLogFormat.SegmentFileName(segmentId));
        var lease = _readHandleCache.Acquire(path, options, cancellationToken);
        Volatile.Write(ref hintSlot, new ReadHandleHint(segmentId, lease.Entry));
        return lease;
    }

    private static SafeFileHandle OpenWriteHandle(string path, AtlasCoreDurabilityStrategy durability) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous
            | FileOptions.RandomAccess
            | (durability == AtlasCoreDurabilityStrategy.WriteThrough
                ? FileOptions.WriteThrough
                : FileOptions.None));

    private static SafeFileHandle OpenBufferedWriteHandle(string path) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static async ValueTask CreateSegmentAsync(
        string segmentsDirectory,
        long segmentId,
        long previousSegmentId,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(segmentsDirectory, AtlasGroupLogFormat.SegmentFileName(segmentId));
        await DurableMetadata.PublishNewAsync(
                path,
                AtlasGroupLogFormat.CreateSegmentHeader(segmentId, previousSegmentId),
                durableFileSystem,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static GroupRecovery Recover(
        string rootDirectory,
        string segmentsDirectory,
        AtlasGroupCheckpointState? checkpoint,
        AtlasTransactionLog transactionLog,
        IDurableFileSystem durableFileSystem)
    {
        var candidates = Directory
            .EnumerateFiles(segmentsDirectory, "segment-*.idbag", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: AtlasGroupLogFormat.TryParseSegmentId(path, out var id), Id: id))
            .Where(static item => item.Parsed)
            .OrderBy(static item => item.Id)
            .ToArray();
        if (candidates.Length == 0)
        {
            if (checkpoint is not null)
            {
                throw new InvalidDataException("AtlasCore group checkpoint exists without its segment files.");
            }

            return new GroupRecovery(
                new Dictionary<string, AtlasGroupAddress>(StringComparer.Ordinal),
                new Dictionary<AtlasPreparedMutationIdentity, AtlasGroupAddress>(),
                0,
                0,
                0,
                0,
                0,
                0);
        }

        for (var index = 0; index < candidates.Length; index++)
        {
            if (candidates[index].Id != index + 1)
            {
                throw new InvalidDataException("AtlasCore group segment chain is not contiguous.");
            }
        }

        if (checkpoint is not null
            && (checkpoint.SegmentId > candidates[^1].Id
                || checkpoint.Offset > new FileInfo(candidates[checkpoint.SegmentId - 1].Path).Length))
        {
            throw new InvalidDataException("AtlasCore group checkpoint points beyond durable segment data.");
        }

        var entries = checkpoint is null
            ? new Dictionary<string, AtlasGroupAddress>(StringComparer.Ordinal)
            : new Dictionary<string, AtlasGroupAddress>(checkpoint.Entries, StringComparer.Ordinal);
        var observedTransactionMutations =
            new Dictionary<AtlasPreparedMutationIdentity, AtlasGroupAddress>();
        var lastLsn = checkpoint?.LastLsn ?? 0;
        var scanned = 0L;
        var repaired = 0L;
        var activeLength = (long)AtlasGroupLogFormat.SegmentHeaderBytes;
        foreach (var candidate in candidates)
        {
            using var handle = File.OpenHandle(
                candidate.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.RandomAccess);
            var segmentHeader = new byte[AtlasGroupLogFormat.SegmentHeaderBytes];
            ReadExactly(handle, segmentHeader, 0, candidate.Path);
            AtlasGroupLogFormat.ValidateSegmentHeader(
                segmentHeader,
                candidate.Id,
                candidate.Id - 1,
                candidate.Path);

            var length = RandomAccess.GetLength(handle);
            var offset = checkpoint is not null && candidate.Id < checkpoint.SegmentId
                ? length
                : checkpoint is not null && candidate.Id == checkpoint.SegmentId
                    ? checkpoint.Offset
                    : AtlasGroupLogFormat.SegmentHeaderBytes;
            var isActive = candidate.Id == candidates[^1].Id;
            var headerBytes = new byte[AtlasGroupLogFormat.FrameHeaderBytes];
            while (offset < length)
            {
                var validOffset = offset;
                var frameEnd = 0L;
                try
                {
                    if (length - offset < AtlasGroupLogFormat.FrameHeaderBytes)
                    {
                        throw new EndOfStreamException();
                    }

                    ReadExactly(handle, headerBytes, offset, candidate.Path);
                    var header = AtlasGroupLogFormat.ParseHeader(headerBytes, candidate.Path, offset);
                    frameEnd = checked(offset + header.FrameLength);
                    if (frameEnd > length)
                    {
                        throw new EndOfStreamException();
                    }

                    var frame = GC.AllocateUninitializedArray<byte>(header.FrameLength);
                    ReadExactly(handle, frame, offset, candidate.Path);
                    AtlasGroupLogFormat.ValidateFrame(frame, header, candidate.Path, offset);
                    if (header.Lsn != lastLsn + 1)
                    {
                        throw new InvalidDataException(
                            $"AtlasCore group LSN sequence breaks at {candidate.Path}:{offset:N0}.");
                    }

                    if (transactionLog.IsCommitted(header.TransactionId))
                    {
                        var key = AtlasGroupLogFormat.ReadKey(frame, header);
                        var address = new AtlasGroupAddress(
                            header.Kind,
                            candidate.Id,
                            offset,
                            header.FrameLength,
                            header.Lsn,
                            header.PlacementGeneration);
                        entries[key] = address;
                        if (header.TransactionId != Guid.Empty)
                        {
                            observedTransactionMutations[
                                new AtlasPreparedMutationIdentity(header.TransactionId, key)] = address;
                        }
                    }

                    lastLsn = header.Lsn;
                    scanned++;
                    offset += header.FrameLength;
                }
                catch (Exception exception) when (
                    CanRepairActiveTail(
                        exception,
                        isActive,
                        handle,
                        validOffset,
                        frameEnd,
                        length,
                        candidate.Path))
                {
                    repaired += length - validOffset;
                    Truncate(candidate.Path, validOffset, durableFileSystem);
                    length = validOffset;
                    offset = length;
                }
            }

            if (isActive)
            {
                activeLength = length;
            }
        }

        return new GroupRecovery(
            entries,
            observedTransactionMutations,
            candidates[^1].Id,
            activeLength,
            lastLsn,
            repaired,
            scanned,
            candidates.Sum(static candidate => new FileInfo(candidate.Path).Length));
    }

    private static bool CanRepairActiveTail(
        Exception exception,
        bool isActive,
        SafeFileHandle handle,
        long invalidOffset,
        long frameEnd,
        long fileLength,
        string path)
    {
        if (!isActive || exception is not (EndOfStreamException or InvalidDataException))
        {
            return false;
        }

        if (frameEnd > 0 && frameEnd < fileLength)
        {
            return false;
        }

        return !HasValidFrameAfter(handle, invalidOffset + 8, fileLength, path);
    }

    private static bool HasValidFrameAfter(
        SafeFileHandle handle,
        long startOffset,
        long fileLength,
        string path)
    {
        var headerBytes = new byte[AtlasGroupLogFormat.FrameHeaderBytes];
        var first = (startOffset + 7) & ~7L;
        for (var offset = first;
             offset + AtlasGroupLogFormat.FrameHeaderBytes <= fileLength;
             offset += 8)
        {
            try
            {
                ReadExactly(handle, headerBytes, offset, path);
                var header = AtlasGroupLogFormat.ParseHeader(headerBytes, path, offset);
                if (offset + header.FrameLength > fileLength)
                {
                    continue;
                }

                var frame = GC.AllocateUninitializedArray<byte>(header.FrameLength);
                ReadExactly(handle, frame, offset, path);
                AtlasGroupLogFormat.ValidateFrame(frame, header, path, offset);
                return true;
            }
            catch (Exception candidateFailure) when (
                candidateFailure is EndOfStreamException or InvalidDataException)
            {
            }
        }

        return false;
    }

    private static void Truncate(string path, long length, IDurableFileSystem durableFileSystem)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.WriteThrough | FileOptions.RandomAccess);
        RandomAccess.SetLength(handle, length);
        durableFileSystem.FlushFile(handle);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset, string path)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of AtlasCore segment '{path}'.");
            }

            total += read;
        }
    }

    private static async ValueTask ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long offset,
        string path,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                    handle,
                    destination[total..],
                    offset + total,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of AtlasCore segment '{path}'.");
            }

            total += read;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new IOException("The AtlasCore segment group is faulted.");
        }
    }

    private abstract class GroupCommand
    {
        public abstract void Fail(Exception exception);
    }

    private sealed class WriteCommand(
        AtlasGroupRecordKind kind,
        string key,
        ReadOnlyMemory<byte> payload,
        long placementGeneration,
        Guid transactionId,
        bool requireDurability,
        bool publishImmediately) : GroupCommand
    {
        public AtlasGroupRecordKind Kind { get; } = kind;
        public string Key { get; } = key;
        public ReadOnlyMemory<byte> Payload { get; } = payload;
        public long PlacementGeneration { get; } = placementGeneration;
        public Guid TransactionId { get; } = transactionId;
        public bool RequireDurability { get; } = requireDurability;
        public bool PublishImmediately { get; } = publishImmediately;
        public TaskCompletionSource<AtlasGroupAddress> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class CheckpointCommand : GroupCommand
    {
        public TaskCompletionSource<GroupCheckpointCapture> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed record PendingFrame(
        WriteCommand Command,
        AtlasGroupAddress Address);

    private sealed record GroupCheckpointCapture(
        long SegmentId,
        long Offset,
        long LastLsn,
        long IndexVersion,
        Dictionary<string, AtlasGroupAddress> Entries);

    private sealed record GroupRecovery(
        Dictionary<string, AtlasGroupAddress> Index,
        Dictionary<AtlasPreparedMutationIdentity, AtlasGroupAddress> ObservedTransactionMutations,
        long ActiveSegmentId,
        long ActiveOffset,
        long LastLsn,
        long RepairedTailBytes,
        long ScannedFrames,
        long PhysicalBytes);

    private readonly record struct AtlasPreparedMutationIdentity(Guid TransactionId, string Key);
}
