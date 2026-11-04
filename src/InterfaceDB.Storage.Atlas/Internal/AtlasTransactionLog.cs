using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Internal;

internal readonly record struct AtlasWalMutation(
    int GroupId,
    AtlasGroupRecordKind Kind,
    string Key,
    byte[] Payload,
    AtlasGroupAddress ExpectedAddress);

internal readonly record struct AtlasWalRecoveryMutation(
    long TransactionSequence,
    Guid TransactionId,
    AtlasWalMutation Mutation);

internal sealed class AtlasTransactionLog : IAsyncDisposable
{
    private const int HeaderBytes = 64;
    private const int LegacyRecordBytes = 64;
    private const int LegacyVersion = 1;
    private const uint LegacyRecordMagic = 0x31435441; // ATC1

    private const int WalVersion = 2;
    private const int WalFrameHeaderBytes = 64;
    private const int WalEntryHeaderBytes = 64;
    private const int WalFrameFooterBytes = 24;
    private const int Alignment = 8;
    private const uint WalRecordMagic = 0x31575441; // ATW1
    private const uint WalFooterMagic = 0x31455741; // AWE1
    private const uint CommitMarker = 0xA71AC0DE;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static ReadOnlySpan<byte> LegacyHeaderMagic => "IDBATX01"u8;
    private static ReadOnlySpan<byte> WalHeaderMagic => "IDBATW02"u8;

    private readonly string _path;
    private readonly AtlasCoreTransactionProtocol _protocol;
    private readonly AtlasCoreDurabilityStrategy _durability;
    private readonly int _segmentGroupCount;
    private readonly int _maximumWalRecordBytes;
    private readonly IDurableFileSystem _durableFileSystem;
    private readonly ConcurrentDictionary<Guid, CommitInfo> _commits;
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private SafeFileHandle _handle;
    private long _appendOffset;
    private long _lastSequence;
    private long _recycledThrough;
    private int _disposed;

    private AtlasTransactionLog(
        string path,
        AtlasCoreTransactionProtocol protocol,
        AtlasCoreDurabilityStrategy durability,
        int segmentGroupCount,
        int maximumWalRecordBytes,
        IDurableFileSystem durableFileSystem,
        ConcurrentDictionary<Guid, CommitInfo> commits,
        SafeFileHandle handle,
        long appendOffset,
        long lastSequence,
        long recycledThrough)
    {
        _path = path;
        _protocol = protocol;
        _durability = durability;
        _segmentGroupCount = segmentGroupCount;
        _maximumWalRecordBytes = maximumWalRecordBytes;
        _durableFileSystem = durableFileSystem;
        _commits = commits;
        _handle = handle;
        _appendOffset = appendOffset;
        _lastSequence = lastSequence;
        _recycledThrough = recycledThrough;
    }

    public AtlasCoreTransactionProtocol Protocol => _protocol;

    public long LastCommittedSequence => Volatile.Read(ref _lastSequence);

    public int RetainedCommitCount => _commits.Count;

    public long LogBytes => Volatile.Read(ref _appendOffset);

    public long RecycledThrough => Volatile.Read(ref _recycledThrough);

    public bool IsCommitted(Guid transactionId) =>
        transactionId == Guid.Empty || _commits.ContainsKey(transactionId);

    public static async ValueTask<AtlasTransactionLog> OpenAsync(
        string rootDirectory,
        AtlasCoreOptions options,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(rootDirectory, "transactions");
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!existed)
        {
            durableFileSystem.FlushDirectory(rootDirectory);
        }

        var path = Path.Combine(directory, "commits.idbatx");
        if (!File.Exists(path))
        {
            await DurableMetadata.PublishNewAsync(
                    path,
                    CreateHeader(options.TransactionProtocol, 0, AtlasCoreOptions.MaximumAddressablePartitions),
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var recovery = Recover(path, options, durableFileSystem);
        var handle = OpenHandle(path, options.DurabilityStrategy);
        return new AtlasTransactionLog(
            path,
            options.TransactionProtocol,
            options.DurabilityStrategy,
            AtlasCoreOptions.MaximumAddressablePartitions,
            options.MaximumTransactionLogRecordBytes,
            durableFileSystem,
            recovery.Commits,
            handle,
            recovery.AppendOffset,
            recovery.LastSequence,
            recovery.RecycledThrough);
    }

    public async ValueTask<long> CommitAsync(
        Guid transactionId,
        int mutationCount,
        CancellationToken cancellationToken)
    {
        if (_protocol != AtlasCoreTransactionProtocol.ParticipantCommitJournal)
        {
            throw new InvalidOperationException(
                "The participant commit API is unavailable when the mutation WAL protocol is active.");
        }

        ValidateCommitArguments(transactionId, mutationCount);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            EnsureNotCommitted(transactionId);
            var sequence = checked(_lastSequence + 1);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var record = CreateLegacyCommitRecord(sequence, transactionId, mutationCount, timestamp);
            var offset = _appendOffset;
            await WriteDurablyAsync(record, offset).ConfigureAwait(false);
            PublishCommit(
                transactionId,
                new CommitInfo(sequence, mutationCount, timestamp, offset, LegacyRecordBytes));
            return sequence;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask<long> CommitMutationsAsync(
        Guid transactionId,
        IReadOnlyList<AtlasWalMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (_protocol != AtlasCoreTransactionProtocol.MutationWriteAheadLog)
        {
            throw new InvalidOperationException(
                "The mutation WAL commit API is unavailable when the participant journal is active.");
        }

        ArgumentNullException.ThrowIfNull(mutations);
        ValidateCommitArguments(transactionId, mutations.Count);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            EnsureNotCommitted(transactionId);
            var sequence = checked(_lastSequence + 1);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var record = CreateWalRecord(
                sequence,
                transactionId,
                timestamp,
                mutations,
                _segmentGroupCount,
                _maximumWalRecordBytes);
            var offset = _appendOffset;
            await WriteDurablyAsync(record, offset).ConfigureAwait(false);
            PublishCommit(
                transactionId,
                new CommitInfo(sequence, mutations.Count, timestamp, offset, record.Length)
                {
                    Groups = mutations.Select(static mutation => mutation.GroupId).Distinct().Order().ToArray()
                });
            return sequence;
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask<AtlasWalRecoveryMutation[]> GetRecoveryMutationsAsync(
        int groupId,
        long afterTransactionSequence,
        CancellationToken cancellationToken)
    {
        if (_protocol != AtlasCoreTransactionProtocol.MutationWriteAheadLog)
        {
            return [];
        }

        if ((uint)groupId >= (uint)_segmentGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(groupId));
        }

        if (afterTransactionSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterTransactionSequence));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var retained = _commits
                .Select(static pair => (TransactionId: pair.Key, Info: pair.Value))
                .Where(item => item.Info.Sequence > afterTransactionSequence
                    && (item.Info.Groups is null || Array.BinarySearch(item.Info.Groups, groupId) >= 0))
                .OrderBy(item => item.Info.Sequence)
                .ToArray();
            var recovered = new List<AtlasWalRecoveryMutation>();
            foreach (var item in retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ArrayPool<byte>.Shared.Rent(item.Info.FrameLength);
                WalRecord parsed;
                try
                {
                    await ReadExactlyAsync(
                            _handle,
                            record.AsMemory(0, item.Info.FrameLength),
                            item.Info.Offset,
                            _path,
                            cancellationToken)
                        .ConfigureAwait(false);
                    parsed = ParseWalRecord(
                        record.AsSpan(0, item.Info.FrameLength),
                        _path,
                        item.Info.Offset,
                        _segmentGroupCount,
                        _maximumWalRecordBytes,
                        materializeGroup: groupId);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(record, clearArray: false);
                }

                if (parsed.TransactionId != item.TransactionId
                    || parsed.Info.Sequence != item.Info.Sequence)
                {
                    throw new InvalidDataException(
                        $"AtlasCore mutation WAL '{_path}' changed while recovery was reading it.");
                }

                foreach (var mutation in parsed.Mutations)
                {
                    if (mutation.GroupId == groupId)
                    {
                        recovered.Add(new AtlasWalRecoveryMutation(
                            parsed.Info.Sequence,
                            parsed.TransactionId,
                            mutation));
                    }
                }
            }

            return recovered.ToArray();
        }
        finally
        {
            _appendGate.Release();
        }
    }

    public async ValueTask RecycleAsync(
        long safeWatermark,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (safeWatermark <= _recycledThrough)
            {
                return;
            }

            if (safeWatermark > _lastSequence)
            {
                throw new ArgumentOutOfRangeException(nameof(safeWatermark));
            }

            var retained = _commits
                .Select(static pair => (TransactionId: pair.Key, Info: pair.Value))
                .Where(item => item.Info.Sequence > safeWatermark)
                .OrderBy(item => item.Info.Sequence)
                .ToArray();
            var sourceOffset = retained.Length == 0 ? _appendOffset : retained[0].Info.Offset;
            var expectedOffset = sourceOffset;
            var expectedSequence = safeWatermark;
            foreach (var item in retained)
            {
                if (item.Info.Sequence != ++expectedSequence
                    || item.Info.Offset != expectedOffset
                    || item.Info.FrameLength < (_protocol == AtlasCoreTransactionProtocol.ParticipantCommitJournal
                        ? LegacyRecordBytes : WalFrameHeaderBytes + WalFrameFooterBytes))
                {
                    throw new InvalidDataException("AtlasCore cannot recycle a non-contiguous committed suffix.");
                }

                expectedOffset = checked(expectedOffset + item.Info.FrameLength);
            }

            if (expectedOffset != _appendOffset || expectedSequence != _lastSequence)
            {
                throw new InvalidDataException("AtlasCore retained commits do not cover the transaction-log suffix.");
            }

            var temporaryPath = _path + ".recycle-" + Guid.NewGuid().ToString("N");
            var updated = new Dictionary<Guid, CommitInfo>(retained.Length);
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 bufferSize: 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(
                            CreateHeader(_protocol, safeWatermark, _segmentGroupCount),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var targetOffset = (long)HeaderBytes;
                    foreach (var item in retained)
                    {
                        updated.Add(
                            item.TransactionId,
                            item.Info with { Offset = targetOffset });
                        targetOffset = checked(targetOffset + item.Info.FrameLength);
                    }

                    // Frames are immutable and contiguous under the append gate. Copy the
                    // exact committed suffix in bounded chunks, including partial frames,
                    // instead of allocating/renting and issuing I/O for every transaction.
                    await CopyCommittedSuffixAsync(stream, sourceOffset, _appendOffset - sourceOffset)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                _handle.Dispose();
                try
                {
                    _durableFileSystem.ReplaceFile(temporaryPath, _path);
                }
                catch
                {
                    _handle = OpenHandle(_path, _durability);
                    throw;
                }

                _handle = OpenHandle(_path, _durability);
                _appendOffset = updated.Count == 0
                    ? HeaderBytes
                    : updated.Values.Max(static info => info.Offset + info.FrameLength);
                _recycledThrough = safeWatermark;
                foreach (var pair in _commits)
                {
                    if (pair.Value.Sequence <= safeWatermark)
                    {
                        _commits.TryRemove(pair.Key, out _);
                    }
                }

                foreach (var pair in updated)
                {
                    _commits[pair.Key] = pair.Value;
                }
            }
            finally
            {
                DurableMetadata.TryDelete(temporaryPath);
            }
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private async ValueTask CopyCommittedSuffixAsync(Stream output, long offset, long remaining)
    {
        if (remaining == 0)
        {
            return;
        }

        const int maximumBufferBytes = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(remaining, maximumBufferBytes));
        try
        {
            while (remaining > 0)
            {
                var length = (int)Math.Min(remaining, maximumBufferBytes);
                await ReadExactlyAsync(_handle, buffer.AsMemory(0, length), offset, _path, CancellationToken.None)
                    .ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, length), CancellationToken.None).ConfigureAwait(false);
                offset += length;
                remaining -= length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _appendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _handle.Dispose();
        }
        finally
        {
            _appendGate.Release();
            _appendGate.Dispose();
        }
    }

    private async ValueTask WriteDurablyAsync(byte[] record, long offset)
    {
        await RandomAccess.WriteAsync(
                _handle,
                record,
                offset,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (_durability == AtlasCoreDurabilityStrategy.ExplicitFlush)
        {
            _durableFileSystem.FlushFile(_handle);
        }
    }

    private void PublishCommit(Guid transactionId, CommitInfo info)
    {
        _appendOffset = checked(info.Offset + info.FrameLength);
        _lastSequence = info.Sequence;
        if (!_commits.TryAdd(transactionId, info))
        {
            throw new InvalidOperationException(
                $"AtlasCore transaction {transactionId} was published twice.");
        }
    }

    private void EnsureNotCommitted(Guid transactionId)
    {
        if (_commits.ContainsKey(transactionId))
        {
            throw new InvalidOperationException(
                $"AtlasCore transaction {transactionId} is already committed.");
        }
    }

    private static void ValidateCommitArguments(Guid transactionId, int mutationCount)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException(
                "An AtlasCore transaction id cannot be empty.",
                nameof(transactionId));
        }

        if (mutationCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mutationCount));
        }
    }

    private static TransactionRecovery Recover(
        string path,
        AtlasCoreOptions options,
        IDurableFileSystem durableFileSystem) =>
        options.TransactionProtocol == AtlasCoreTransactionProtocol.ParticipantCommitJournal
            ? RecoverParticipant(path, durableFileSystem)
            : RecoverWal(path, options, durableFileSystem);

    private static TransactionRecovery RecoverParticipant(
        string path,
        IDurableFileSystem durableFileSystem)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        var length = RandomAccess.GetLength(handle);
        if (length < HeaderBytes)
        {
            throw new InvalidDataException("AtlasCore transaction log header is truncated.");
        }

        var header = new byte[HeaderBytes];
        ReadExactlyParticipant(handle, header, 0, path);
        var recycledThrough = ValidateLegacyHeader(header, path);
        var completeLength = HeaderBytes + ((length - HeaderBytes) / LegacyRecordBytes) * LegacyRecordBytes;
        var commits = new ConcurrentDictionary<Guid, CommitInfo>();
        var lastSequence = recycledThrough;
        var record = new byte[LegacyRecordBytes];
        for (var offset = HeaderBytes; offset < completeLength; offset += LegacyRecordBytes)
        {
            ReadExactlyParticipant(handle, record, offset, path);
            var commit = ParseLegacyCommitRecord(record, path, offset);
            if (commit.Info.Sequence != lastSequence + 1
                || !commits.TryAdd(commit.TransactionId, commit.Info with { Offset = offset }))
            {
                throw new InvalidDataException(
                    $"AtlasCore transaction log '{path}' has an invalid commit sequence at {offset:N0}.");
            }

            lastSequence = commit.Info.Sequence;
        }

        if (completeLength != length)
        {
            Truncate(path, completeLength, durableFileSystem);
        }

        return new TransactionRecovery(commits, completeLength, lastSequence, recycledThrough);
    }

    private static void ReadExactlyParticipant(
        SafeFileHandle handle,
        Span<byte> destination,
        long offset,
        string path)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var amount = RandomAccess.Read(handle, destination[read..], offset + read);
            if (amount == 0)
            {
                throw new InvalidDataException(
                    $"AtlasCore transaction log '{path}' ended unexpectedly.");
            }
            read += amount;
        }
    }

    private static TransactionRecovery RecoverWal(
        string path,
        AtlasCoreOptions options,
        IDurableFileSystem durableFileSystem)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.RandomAccess);
        var length = RandomAccess.GetLength(handle);
        if (length < HeaderBytes)
        {
            throw new InvalidDataException("AtlasCore mutation WAL header is truncated.");
        }

        var header = new byte[HeaderBytes];
        ReadExactly(handle, header, 0, path);
        var recycledThrough = ValidateWalHeader(header, path, AtlasCoreOptions.MaximumAddressablePartitions);
        var commits = new ConcurrentDictionary<Guid, CommitInfo>();
        var lastSequence = recycledThrough;
        var offset = (long)HeaderBytes;
        var frameHeader = new byte[WalFrameHeaderBytes];
        while (offset < length)
        {
            if (length - offset < WalFrameHeaderBytes)
            {
                Truncate(path, offset, durableFileSystem);
                length = offset;
                break;
            }

            ReadExactly(handle, frameHeader, offset, path);
            var parsedHeader = ParseWalRecordHeader(
                frameHeader,
                path,
                offset,
                options.MaximumTransactionLogRecordBytes);
            if (length - offset < parsedHeader.FrameLength)
            {
                Truncate(path, offset, durableFileSystem);
                length = offset;
                break;
            }

            var record = ArrayPool<byte>.Shared.Rent(parsedHeader.FrameLength);
            WalRecord parsed;
            try
            {
                ReadExactly(handle, record.AsSpan(0, parsedHeader.FrameLength), offset, path);
                parsed = ParseWalRecord(
                    record.AsSpan(0, parsedHeader.FrameLength),
                    path,
                    offset,
                    AtlasCoreOptions.MaximumAddressablePartitions,
                    options.MaximumTransactionLogRecordBytes,
                    materializeGroup: -1);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(record, clearArray: false);
            }
            var info = parsed.Info with { Offset = offset };
            if (info.Sequence != lastSequence + 1
                || !commits.TryAdd(parsed.TransactionId, info))
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' has an invalid commit sequence at {offset:N0}.");
            }

            lastSequence = info.Sequence;
            offset = checked(offset + info.FrameLength);
        }

        return new TransactionRecovery(commits, length, lastSequence, recycledThrough);
    }

    private static byte[] CreateHeader(
        AtlasCoreTransactionProtocol protocol,
        long recycledThrough,
        int segmentGroupCount) =>
        protocol == AtlasCoreTransactionProtocol.ParticipantCommitJournal
            ? CreateLegacyHeader(recycledThrough)
            : CreateWalHeader(recycledThrough, segmentGroupCount);

    private static byte[] CreateLegacyHeader(long recycledThrough)
    {
        var header = new byte[HeaderBytes];
        LegacyHeaderMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), LegacyVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), HeaderBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), recycledThrough);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(24),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), LegacyChecksum(header, 32));
        return header;
    }

    private static byte[] CreateWalHeader(long recycledThrough, int segmentGroupCount)
    {
        var header = new byte[HeaderBytes];
        WalHeaderMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), WalVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), HeaderBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), recycledThrough);
        BinaryPrimitives.WriteInt64LittleEndian(
            header.AsSpan(24),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), segmentGroupCount);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(36),
            (int)AtlasCoreTransactionProtocol.MutationWriteAheadLog);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), AtlasCrc32C.Compute(header.AsSpan(0, 40)));
        return header;
    }

    private static long ValidateLegacyHeader(ReadOnlySpan<byte> header, string path)
    {
        var recycledThrough = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        if (!header[..8].SequenceEqual(LegacyHeaderMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != LegacyVersion
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != HeaderBytes
            || recycledThrough < 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header[32..]) != LegacyChecksum(header, 32))
        {
            throw new InvalidDataException($"AtlasCore transaction log '{path}' has an invalid header.");
        }

        return recycledThrough;
    }

    private static long ValidateWalHeader(
        ReadOnlySpan<byte> header,
        string path,
        int expectedSegmentGroupCount)
    {
        var recycledThrough = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        if (!header[..8].SequenceEqual(WalHeaderMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != WalVersion
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != HeaderBytes
            || recycledThrough < 0
            || BinaryPrimitives.ReadInt32LittleEndian(header[32..]) != expectedSegmentGroupCount
            || BinaryPrimitives.ReadInt32LittleEndian(header[36..])
            != (int)AtlasCoreTransactionProtocol.MutationWriteAheadLog
            || BinaryPrimitives.ReadUInt32LittleEndian(header[40..])
            != AtlasCrc32C.Compute(header[..40])
            || !IsZero(header[44..]))
        {
            throw new InvalidDataException($"AtlasCore mutation WAL '{path}' has an invalid header.");
        }

        return recycledThrough;
    }

    private static byte[] CreateLegacyCommitRecord(
        long sequence,
        Guid transactionId,
        int mutationCount,
        long timestampMilliseconds)
    {
        var record = new byte[LegacyRecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(record, LegacyRecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), LegacyVersion);
        record[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), LegacyRecordBytes);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), mutationCount);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(16), sequence);
        _ = transactionId.TryWriteBytes(record.AsSpan(24, 16));
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(40), timestampMilliseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), CommitMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(48), LegacyChecksum(record, 48));
        return record;
    }

    private static LegacyCommitRecord ParseLegacyCommitRecord(
        ReadOnlySpan<byte> record,
        string path,
        long offset)
    {
        var transactionId = new Guid(record.Slice(24, 16));
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
        var mutationCount = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != LegacyRecordMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != LegacyVersion
            || record[6] != 1
            || record[7] != 0
            || BinaryPrimitives.ReadInt32LittleEndian(record[8..]) != LegacyRecordBytes
            || mutationCount < 1
            || sequence <= 0
            || transactionId == Guid.Empty
            || BinaryPrimitives.ReadUInt32LittleEndian(record[48..]) != LegacyChecksum(record, 48)
            || BinaryPrimitives.ReadUInt32LittleEndian(record[52..]) != CommitMarker
            || !IsZero(record[56..]))
        {
            throw new InvalidDataException(
                $"AtlasCore transaction log '{path}' has an invalid commit at {offset:N0}.");
        }

        return new LegacyCommitRecord(
            transactionId,
            new CommitInfo(
                sequence,
                mutationCount,
                BinaryPrimitives.ReadInt64LittleEndian(record[40..]),
                offset,
                LegacyRecordBytes));
    }

    private static byte[] CreateWalRecord(
        long sequence,
        Guid transactionId,
        long timestampMilliseconds,
        IReadOnlyList<AtlasWalMutation> mutations,
        int segmentGroupCount,
        int maximumRecordBytes)
    {
        var entryLengths = new int[mutations.Count];
        var keyLengths = new int[mutations.Count];
        var entriesLength = 0;
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            ValidateWalMutation(mutation, segmentGroupCount);
            var keyLength = StrictUtf8.GetByteCount(mutation.Key);
            var entryLength = Align(checked(WalEntryHeaderBytes + keyLength + mutation.Payload.Length));
            keyLengths[index] = keyLength;
            entryLengths[index] = entryLength;
            entriesLength = checked(entriesLength + entryLength);
        }

        var frameLength = Align(checked(WalFrameHeaderBytes + entriesLength + WalFrameFooterBytes));
        if (frameLength > maximumRecordBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutations),
                $"The encoded AtlasCore transaction is {frameLength:N0} bytes; the configured maximum is {maximumRecordBytes:N0} bytes.");
        }

        var frame = new byte[frameLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, WalRecordMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), WalVersion);
        frame[6] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), frameLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), mutations.Count);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(16), sequence);
        _ = transactionId.TryWriteBytes(frame.AsSpan(24, 16));
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(40), timestampMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(48), entriesLength);

        var cursor = WalFrameHeaderBytes;
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            var entryLength = entryLengths[index];
            var keyLength = keyLengths[index];
            var entry = frame.AsSpan(cursor, entryLength);
            BinaryPrimitives.WriteInt32LittleEndian(entry, entryLength);
            BinaryPrimitives.WriteInt32LittleEndian(entry[4..], mutation.GroupId);
            entry[8] = (byte)mutation.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], keyLength);
            BinaryPrimitives.WriteInt32LittleEndian(entry[16..], mutation.Payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[20..], mutation.ExpectedAddress.FrameLength);
            BinaryPrimitives.WriteInt64LittleEndian(entry[24..], mutation.ExpectedAddress.SegmentId);
            BinaryPrimitives.WriteInt64LittleEndian(entry[32..], mutation.ExpectedAddress.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(entry[40..], mutation.ExpectedAddress.Lsn);
            BinaryPrimitives.WriteInt64LittleEndian(entry[48..], mutation.ExpectedAddress.PlacementGeneration);
            var data = entry[WalEntryHeaderBytes..(WalEntryHeaderBytes + keyLength + mutation.Payload.Length)];
            _ = StrictUtf8.GetBytes(mutation.Key.AsSpan(), data[..keyLength]);
            mutation.Payload.CopyTo(data[keyLength..]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[56..], AtlasCrc32C.Compute(data));
            cursor += entryLength;
        }

        var entries = frame.AsSpan(WalFrameHeaderBytes, entriesLength);
        var entriesChecksum = AtlasCrc32C.Compute(entries);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), AtlasCrc32C.Compute(frame.AsSpan(0, 52)));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(56), entriesChecksum);
        var footer = frame.AsSpan(frameLength - WalFrameFooterBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, WalFooterMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footer[4..], frameLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], entriesChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], CommitMarker);
        return frame;
    }

    internal static void ValidateTransactionAdmission(
        IReadOnlyList<AtlasCorePendingMutation> mutations,
        int maximumRecordBytes)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        try
        {
            var entriesLength = 0;
            foreach (var mutation in mutations)
            {
                entriesLength = checked(entriesLength
                    + CalculateTransactionEntryLength(
                        mutation.Kind,
                        mutation.Key,
                        mutation.Payload.Length));
            }

            ValidateTransactionAdmission(entriesLength, maximumRecordBytes);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutations),
                $"The encoded AtlasCore transaction exceeds the supported size: {exception.Message}");
        }
    }

    internal static int CalculateTransactionEntryLength(
        AtlasGroupRecordKind kind,
        string key,
        int payloadLength)
    {
        _ = AtlasGroupLogFormat.CalculateFrameLength(
            kind,
            lsn: 1,
            placementGeneration: 1,
            key,
            payloadLength);
        return CalculateWalEntryLength(key, payloadLength);
    }

    internal static int CalculateWalEntryLength(string key, int payloadLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (payloadLength < 0) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        var keyLength = StrictUtf8.GetByteCount(key);
        return Align(checked(WalEntryHeaderBytes + keyLength + payloadLength));
    }

    internal static void ValidateTransactionAdmission(int entriesLength, int maximumRecordBytes)
    {
        try
        {
            var frameLength = Align(checked(WalFrameHeaderBytes + entriesLength + WalFrameFooterBytes));
            if (frameLength > maximumRecordBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(entriesLength),
                    $"The encoded AtlasCore transaction is {frameLength:N0} bytes; the configured maximum is {maximumRecordBytes:N0} bytes.");
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entriesLength),
                $"The encoded AtlasCore transaction exceeds the supported size: {exception.Message}");
        }
    }

    private static WalRecord ParseWalRecord(
        ReadOnlySpan<byte> record,
        string path,
        long offset,
        int segmentGroupCount,
        int maximumRecordBytes,
        int? materializeGroup = null)
    {
        var header = ParseWalRecordHeader(
            record[..WalFrameHeaderBytes],
            path,
            offset,
            maximumRecordBytes);
        if (record.Length != header.FrameLength)
        {
            throw new InvalidDataException(
                $"AtlasCore mutation WAL '{path}' ends inside a transaction at {offset:N0}.");
        }

        var entries = record.Slice(WalFrameHeaderBytes, header.EntriesLength);
        var entriesChecksum = BinaryPrimitives.ReadUInt32LittleEndian(record[56..]);
        var footer = record[^WalFrameFooterBytes..];
        if (AtlasCrc32C.Compute(entries) != entriesChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer) != WalFooterMagic
            || BinaryPrimitives.ReadInt32LittleEndian(footer[4..]) != header.FrameLength
            || BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) != header.Sequence
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != entriesChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) != CommitMarker
            || !IsZero(record.Slice(
                WalFrameHeaderBytes + header.EntriesLength,
                header.FrameLength - WalFrameHeaderBytes - header.EntriesLength - WalFrameFooterBytes)))
        {
            throw new InvalidDataException(
                $"AtlasCore mutation WAL '{path}' has an invalid committed transaction at {offset:N0}.");
        }

        var mutations = new List<AtlasWalMutation>(materializeGroup == -1 ? 0 : Math.Min(header.MutationCount, 64));
        var groups = new HashSet<int>();
        var cursor = 0;
        for (var index = 0; index < header.MutationCount; index++)
        {
            if (entries.Length - cursor < WalEntryHeaderBytes)
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' has a truncated mutation at {offset + WalFrameHeaderBytes + cursor:N0}.");
            }

            var entry = entries[cursor..];
            var entryLength = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var groupId = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            var kind = (AtlasGroupRecordKind)entry[8];
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(entry[16..]);
            var groupFrameLength = BinaryPrimitives.ReadInt32LittleEndian(entry[20..]);
            long unaligned;
            try
            {
                unaligned = checked((long)WalEntryHeaderBytes + keyLength + payloadLength);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("AtlasCore mutation WAL entry lengths overflow.", exception);
            }

            if (entryLength < WalEntryHeaderBytes
                || (entryLength & (Alignment - 1)) != 0
                || entryLength > entries.Length - cursor
                || keyLength <= 0
                || payloadLength < 0
                || unaligned > entryLength
                || (uint)groupId >= (uint)segmentGroupCount
                || !Enum.IsDefined(kind)
                || (kind == AtlasGroupRecordKind.Delete && payloadLength != 0)
                || entry[9] != 0
                || entry[10] != 0
                || entry[11] != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]) != 0)
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' has invalid mutation fields at {offset + WalFrameHeaderBytes + cursor:N0}.");
            }

            var dataLength = checked(keyLength + payloadLength);
            var data = entry.Slice(WalEntryHeaderBytes, dataLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]) != AtlasCrc32C.Compute(data)
                || !IsZero(entry.Slice(WalEntryHeaderBytes + dataLength, entryLength - WalEntryHeaderBytes - dataLength)))
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' has an invalid mutation checksum at {offset + WalFrameHeaderBytes + cursor:N0}.");
            }

            string key;
            try
            {
                key = StrictUtf8.GetString(data[..keyLength]);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' contains an invalid UTF-8 key.",
                    exception);
            }

            var expectedAddress = new AtlasGroupAddress(
                kind,
                BinaryPrimitives.ReadInt64LittleEndian(entry[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
                groupFrameLength,
                BinaryPrimitives.ReadInt64LittleEndian(entry[40..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[48..]));
            ValidateWalMutationFields(groupId, kind, key, payloadLength, expectedAddress, segmentGroupCount);
            if (groupFrameLength != CalculateGroupFrameLength(keyLength, payloadLength))
            {
                throw new InvalidDataException(
                    $"AtlasCore mutation WAL '{path}' contains an invalid participant frame length.");
            }

            groups.Add(groupId);
            if (materializeGroup is null || materializeGroup == groupId)
            {
                mutations.Add(new AtlasWalMutation(
                    groupId, kind, key, data[keyLength..].ToArray(), expectedAddress));
            }
            cursor += entryLength;
        }

        if (cursor != entries.Length)
        {
            throw new InvalidDataException(
                $"AtlasCore mutation WAL '{path}' contains trailing mutation bytes at {offset:N0}.");
        }

        return new WalRecord(
            header.TransactionId,
            new CommitInfo(
                header.Sequence,
                header.MutationCount,
                header.TimestampMilliseconds,
                offset,
                header.FrameLength)
            {
                Groups = groups.Order().ToArray()
            },
            mutations.ToArray());
    }

    private static WalRecordHeader ParseWalRecordHeader(
        ReadOnlySpan<byte> header,
        string path,
        long offset,
        int maximumRecordBytes)
    {
        if (header.Length != WalFrameHeaderBytes)
        {
            throw new InvalidDataException(
                $"AtlasCore mutation WAL '{path}' has a truncated frame header at {offset:N0}.");
        }

        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var mutationCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        var transactionId = new Guid(header.Slice(24, 16));
        var entriesLength = BinaryPrimitives.ReadInt32LittleEndian(header[48..]);
        var minimumLength = (long)WalFrameHeaderBytes + entriesLength + WalFrameFooterBytes;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != WalRecordMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != WalVersion
            || header[6] != 1
            || header[7] != 0
            || frameLength < WalFrameHeaderBytes + WalEntryHeaderBytes + WalFrameFooterBytes
            || frameLength > maximumRecordBytes
            || (frameLength & (Alignment - 1)) != 0
            || mutationCount < 1
            || mutationCount > entriesLength / WalEntryHeaderBytes
            || sequence <= 0
            || transactionId == Guid.Empty
            || entriesLength < WalEntryHeaderBytes
            || minimumLength > frameLength
            || BinaryPrimitives.ReadUInt32LittleEndian(header[52..])
            != AtlasCrc32C.Compute(header[..52])
            || BinaryPrimitives.ReadUInt32LittleEndian(header[60..]) != 0)
        {
            throw new InvalidDataException(
                $"AtlasCore mutation WAL '{path}' has an invalid frame header at {offset:N0}.");
        }

        return new WalRecordHeader(
            frameLength,
            mutationCount,
            sequence,
            transactionId,
            BinaryPrimitives.ReadInt64LittleEndian(header[40..]),
            entriesLength);
    }

    private static void ValidateWalMutation(AtlasWalMutation mutation, int segmentGroupCount)
    {
        ArgumentNullException.ThrowIfNull(mutation.Payload);
        ValidateWalMutationFields(
            mutation.GroupId, mutation.Kind, mutation.Key, mutation.Payload.Length, mutation.ExpectedAddress, segmentGroupCount);
    }

    private static void ValidateWalMutationFields(
        int groupId,
        AtlasGroupRecordKind kind,
        string key,
        int payloadLength,
        AtlasGroupAddress address,
        int segmentGroupCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if ((uint)groupId >= (uint)segmentGroupCount
            || !Enum.IsDefined(kind)
            || kind != address.Kind
            || (kind == AtlasGroupRecordKind.Delete && payloadLength != 0)
            || address.SegmentId <= 0
            || address.Offset < AtlasGroupLogFormat.SegmentHeaderBytes
            || address.FrameLength < AtlasGroupLogFormat.FrameHeaderBytes + AtlasGroupLogFormat.FrameFooterBytes
            || address.FrameLength > AtlasGroupLogFormat.MaximumFrameBytes
            || address.Lsn <= 0
            || address.PlacementGeneration <= 0)
        {
            throw new InvalidDataException("AtlasCore mutation WAL received an invalid participant locator.");
        }
    }

    private static int CalculateGroupFrameLength(int keyLength, int payloadLength)
    {
        var unaligned = checked(
            AtlasGroupLogFormat.FrameHeaderBytes
            + keyLength
            + payloadLength
            + AtlasGroupLogFormat.FrameFooterBytes);
        return Align(unaligned);
    }

    private static int Align(int value) => checked((value + Alignment - 1) & ~(Alignment - 1));

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static uint LegacyChecksum(ReadOnlySpan<byte> bytes, int checksumOffset)
    {
        Span<byte> copy = stackalloc byte[LegacyRecordBytes];
        var target = copy[..bytes.Length];
        bytes.CopyTo(target);
        target.Slice(checksumOffset, sizeof(uint)).Clear();
        return AtlasCrc32C.Compute(target);
    }

    private static SafeFileHandle OpenHandle(
        string path,
        AtlasCoreDurabilityStrategy durability) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous
            | FileOptions.RandomAccess
            | (durability == AtlasCoreDurabilityStrategy.WriteThrough
                ? FileOptions.WriteThrough
                : FileOptions.None));

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

    private static void ReadExactly(
        SafeFileHandle handle,
        Span<byte> destination,
        long offset,
        string path)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of AtlasCore transaction log '{path}'.");
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
                throw new EndOfStreamException($"Unexpected end of AtlasCore transaction log '{path}'.");
            }

            total += read;
        }
    }

    private readonly record struct CommitInfo(
        long Sequence,
        int MutationCount,
        long TimestampMilliseconds,
        long Offset,
        int FrameLength)
    {
        // Only group membership is retained; entity payloads stay in the durable WAL.
        public int[]? Groups { get; init; }
    }

    private readonly record struct LegacyCommitRecord(Guid TransactionId, CommitInfo Info);

    private readonly record struct WalRecordHeader(
        int FrameLength,
        int MutationCount,
        long Sequence,
        Guid TransactionId,
        long TimestampMilliseconds,
        int EntriesLength);

    private sealed record WalRecord(
        Guid TransactionId,
        CommitInfo Info,
        AtlasWalMutation[] Mutations);

    private sealed record TransactionRecovery(
        ConcurrentDictionary<Guid, CommitInfo> Commits,
        long AppendOffset,
        long LastSequence,
        long RecycledThrough);
}
