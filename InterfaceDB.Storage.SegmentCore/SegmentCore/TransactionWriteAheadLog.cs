using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

internal sealed record TransactionWalMutation(
    SegmentCorePendingMutation Mutation,
    StripedObjectLogRecordAddress PreparedAddress);

internal sealed record TransactionWalCommit(
    long TransactionId,
    long CommitSequence,
    IReadOnlyList<TransactionWalMutation> Mutations);

internal sealed class TransactionWalSnapshot
{
    private readonly IReadOnlyDictionary<long, TransactionWalCommit> _commits;

    public TransactionWalSnapshot(long baseTransactionId, IReadOnlyList<TransactionWalCommit> commits)
    {
        BaseTransactionId = baseTransactionId;
        Commits = commits.OrderBy(static commit => commit.CommitSequence).ToArray();
        _commits = Commits.ToDictionary(static commit => commit.TransactionId);
        HighestTransactionId = Commits
            .Select(static commit => commit.TransactionId)
            .Append(baseTransactionId)
            .Max();
    }

    public long BaseTransactionId { get; }

    public IReadOnlyList<TransactionWalCommit> Commits { get; }

    public long HighestTransactionId { get; }

    public IReadOnlySet<long> CommittedTransactionIds => _commits.Keys.ToHashSet();

    public bool IsCommitted(long transactionId) => _commits.ContainsKey(transactionId);

    public bool TryGetCommit(long transactionId, out TransactionWalCommit commit) =>
        _commits.TryGetValue(transactionId, out commit!);

    public IEnumerable<TransactionWalCommit> After(long transactionWatermark) =>
        Commits.Where(commit => commit.TransactionId > transactionWatermark);
}

internal sealed class TransactionWriteAheadLog : IAsyncDisposable
{
    internal const int FileHeaderSize = 64;
    private const int FileHeaderBytes = FileHeaderSize;
    private const int HeaderBytes = 64;
    private const int MutationHeaderBytes = 56;
    private const int FooterBytes = 24;
    private const int Alignment = 8;
    private const int FormatVersion = 1;
    private const int FileFormatVersion = 1;
    private const int MaximumMutations = 10_000_000;
    private const uint FooterMagic = 0x57425449;
    private const uint CommitMarker = 0xC07A17ED;
    private const string DirectoryName = "transactions";
    private const string FileName = "mutation-wal.idbwal";
    private const int RecoveryBoundaryBytes = 144;
    private const int RecoveryBoundaryFormatVersion = 1;
    private const int PrefixHashBufferBytes = 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "IDBTWA01"u8;
    private static ReadOnlySpan<byte> FileMagic => "IDBTWLF1"u8;
    private static ReadOnlySpan<byte> RecoveryBoundaryMagic => "IDBWBD01"u8;

    private readonly IDurableFileSystem _durableFileSystem;
    private readonly bool _writeThrough;
    private readonly string _path;
    private SafeFileHandle _handle;
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly Channel<PendingWalCommit> _commitQueue;
    private readonly Task _commitWorker;
    private readonly TimeSpan _groupCommitDelay;
    private readonly int _maximumGroupCommitRecords;
    private readonly int _maximumGroupCommitBytes;
    private readonly List<TransactionWalCommit> _recoveryCommits;
    private readonly HashSet<long> _committedTransactionIds;
    private readonly List<WalRecordLocation> _recordLocations;
    private long _nextOffset;
    private long _generation;
    private long _baseTransactionId;
    private long _baseCommitSequence;
    private long _recoveryBaseTransactionId;
    private long _recoveryBoundaryOffset;
    private long _recoveryPriorTransactionId;
    private long _recoveryPriorCommitSequence;
    private long _recoveryPriorRecordOffset;
    private int _recoveryPriorRecordLength;
    private long _highestTransactionId;
    private long _highestDurableTransactionId;
    private long _highestCommitSequence;
    private int _faulted;
    private int _disposed;

    private TransactionWriteAheadLog(
        IDurableFileSystem durableFileSystem,
        bool writeThrough,
        string path,
        SafeFileHandle handle,
        List<TransactionWalCommit> commits,
        List<WalRecordLocation> recordLocations,
        long nextOffset,
        long generation,
        long baseTransactionId,
        long baseCommitSequence,
        long recoveryBaseTransactionId,
        long highestObservedCommitSequence,
        long recoveryBoundaryOffset,
        long recoveryPriorTransactionId,
        long recoveryPriorCommitSequence,
        long recoveryPriorRecordOffset,
        int recoveryPriorRecordLength,
        TimeSpan groupCommitDelay,
        int maximumGroupCommitRecords,
        int maximumGroupCommitBytes)
    {
        _durableFileSystem = durableFileSystem;
        _writeThrough = writeThrough;
        _path = path;
        _handle = handle;
        _recoveryCommits = commits;
        _committedTransactionIds = commits.Select(static commit => commit.TransactionId).ToHashSet();
        _recordLocations = recordLocations;
        _nextOffset = nextOffset;
        _generation = generation;
        _baseTransactionId = baseTransactionId;
        _baseCommitSequence = baseCommitSequence;
        _recoveryBaseTransactionId = recoveryBaseTransactionId;
        _recoveryBoundaryOffset = recoveryBoundaryOffset;
        _recoveryPriorTransactionId = recoveryPriorTransactionId;
        _recoveryPriorCommitSequence = recoveryPriorCommitSequence;
        _recoveryPriorRecordOffset = recoveryPriorRecordOffset;
        _recoveryPriorRecordLength = recoveryPriorRecordLength;
        _groupCommitDelay = groupCommitDelay;
        _maximumGroupCommitRecords = maximumGroupCommitRecords;
        _maximumGroupCommitBytes = maximumGroupCommitBytes;
        _commitQueue = Channel.CreateUnbounded<PendingWalCommit>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _highestTransactionId = commits
            .Select(static commit => commit.TransactionId)
            .Append(recoveryBaseTransactionId)
            .Max();
        _highestDurableTransactionId = _highestTransactionId;
        _highestCommitSequence = commits
            .Select(static commit => commit.CommitSequence)
            .Append(highestObservedCommitSequence)
            .Max();
        _commitWorker = RunCommitWorkerAsync();
    }

    public long HighestDurableCommittedTransactionId => Volatile.Read(ref _highestDurableTransactionId);

    internal long RecoveryPrefixBytesSkipped => Math.Max(
        0,
        Volatile.Read(ref _recoveryBoundaryOffset) - FileHeaderBytes);

    public static ValueTask<TransactionWriteAheadLog> OpenAsync(
        string rootDirectory,
        ObjectLogDurabilityStrategy durabilityStrategy,
        TimeSpan groupCommitDelay,
        int maximumGroupCommitRecords,
        int maximumGroupCommitBytes,
        IDurableFileSystem durableFileSystem,
        long checkpointGeneration = 0,
        long checkpointTransactionWatermark = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(durableFileSystem);
        cancellationToken.ThrowIfCancellationRequested();
        if (groupCommitDelay < TimeSpan.Zero || groupCommitDelay > TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(groupCommitDelay));
        }

        if (maximumGroupCommitRecords is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGroupCommitRecords));
        }

        if (maximumGroupCommitBytes is < 4 * 1024 or > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGroupCommitBytes));
        }
        var directory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!directoryExisted)
        {
            durableFileSystem.FlushDirectory(Path.GetFullPath(rootDirectory));
        }

        var path = Path.Combine(directory, FileName);
        EnsureWalFile(path, durableFileSystem);
        if (checkpointGeneration < 0 || checkpointTransactionWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointGeneration));
        }

        var recovery = Recover(
            path,
            durableFileSystem,
            checkpointGeneration,
            checkpointTransactionWatermark);
        var writeThrough = durabilityStrategy == ObjectLogDurabilityStrategy.WriteThrough;
        var handle = OpenWalHandle(path, writeThrough);
        return ValueTask.FromResult(new TransactionWriteAheadLog(
            durableFileSystem,
            writeThrough,
            path,
            handle,
            recovery.Commits,
            recovery.RecordLocations,
            recovery.ValidLength,
            recovery.Generation,
            recovery.BaseTransactionId,
            recovery.BaseCommitSequence,
            recovery.RecoveryBaseTransactionId,
            recovery.HighestObservedCommitSequence,
            recovery.RecoveryBoundaryOffset,
            recovery.RecoveryPriorTransactionId,
            recovery.RecoveryPriorCommitSequence,
            recovery.RecoveryPriorRecordOffset,
            recovery.RecoveryPriorRecordLength,
            groupCommitDelay,
            maximumGroupCommitRecords,
            maximumGroupCommitBytes));
    }

    public TransactionWalSnapshot CreateRecoverySnapshot() => new(
        Volatile.Read(ref _recoveryBaseTransactionId),
        _recoveryCommits.ToArray());

    public void ReleaseRecoveryPayloads() => _recoveryCommits.Clear();

    public long ReserveTransactionId()
    {
        ThrowIfUnavailable();
        return Interlocked.Increment(ref _highestTransactionId);
    }

    public void AdvanceTransactionIdPast(long observedTransactionId)
    {
        var current = Volatile.Read(ref _highestTransactionId);
        while (observedTransactionId > current)
        {
            var prior = Interlocked.CompareExchange(
                ref _highestTransactionId,
                observedTransactionId,
                current);
            if (prior == current)
            {
                return;
            }

            current = prior;
        }
    }

    public ValueTask AppendCommitAsync(
        long transactionId,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        IReadOnlyList<StripedObjectLogRecordAddress> preparedAddresses,
        CancellationToken cancellationToken = default)
    {
        if (transactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(preparedAddresses);
        if (mutations.Count is <= 0 or > MaximumMutations)
        {
            throw new ArgumentOutOfRangeException(nameof(mutations));
        }

        if (preparedAddresses.Count != mutations.Count)
        {
            throw new ArgumentException(
                "The prepared-address count must match the mutation count.",
                nameof(preparedAddresses));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask(EnqueueCommit(transactionId, mutations, preparedAddresses));
    }

    internal Task EnqueueCommit(
        long transactionId,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        IReadOnlyList<StripedObjectLogRecordAddress> preparedAddresses)
    {
        ThrowIfUnavailable();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingWalCommit(
            transactionId,
            mutations,
            preparedAddresses,
            completion);
        if (!_commitQueue.Writer.TryWrite(pending))
        {
            return Task.FromException(
                new InvalidOperationException("The mutation WAL commit queue is no longer accepting work."));
        }

        return completion.Task;
    }

    private async Task RunCommitWorkerAsync()
    {
        var batch = new List<PendingWalCommit>(_maximumGroupCommitRecords);
        try
        {
            while (await _commitQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                if (!_commitQueue.Reader.TryRead(out var first))
                {
                    continue;
                }

                batch.Add(first);
                if (_groupCommitDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_groupCommitDelay).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }

                while (batch.Count < _maximumGroupCommitRecords
                       && _commitQueue.Reader.TryRead(out var pending))
                {
                    batch.Add(pending);
                }

                await PersistCommitBatchAsync(batch).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _faulted, 1);
            foreach (var pending in batch)
            {
                pending.Completion.TrySetException(exception);
            }

            while (_commitQueue.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetException(exception);
            }

            _commitQueue.Writer.TryComplete(exception);
        }
    }

    private async ValueTask PersistCommitBatchAsync(IReadOnlyList<PendingWalCommit> batch)
    {
        await _appendGate.WaitAsync().ConfigureAwait(false);
        var records = new byte[batch.Count][];
        var locations = new WalRecordLocation[batch.Count];
        byte[]? combinedBuffer = null;
        try
        {
            ThrowIfUnavailable();
            var sequence = _highestCommitSequence;
            var offset = _nextOffset;
            for (var index = 0; index < batch.Count; index++)
            {
                var pending = batch[index];
                if (_committedTransactionIds.Contains(pending.TransactionId))
                {
                    throw new InvalidOperationException(
                        $"Transaction {pending.TransactionId:N0} is already committed.");
                }

                sequence = checked(sequence + 1);
                var record = CreateRecord(
                    pending.TransactionId,
                    sequence,
                    pending.Mutations,
                    pending.PreparedAddresses);
                records[index] = record;
                locations[index] = new WalRecordLocation(
                    pending.TransactionId,
                    sequence,
                    offset,
                    record.Length);
                offset += record.Length;
            }

            var totalBytes = checked((int)(offset - _nextOffset));
            if (records.Length > 1 && totalBytes <= _maximumGroupCommitBytes)
            {
                combinedBuffer = ArrayPool<byte>.Shared.Rent(totalBytes);
                var destination = combinedBuffer.AsMemory(0, totalBytes);
                var copied = 0;
                foreach (var record in records)
                {
                    record.CopyTo(destination[copied..]);
                    copied += record.Length;
                }

                await RandomAccess.WriteAsync(
                    _handle,
                    destination,
                    _nextOffset,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                for (var index = 0; index < records.Length; index++)
                {
                    await RandomAccess.WriteAsync(
                        _handle,
                        records[index],
                        locations[index].Offset,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (!_writeThrough)
            {
                _durableFileSystem.FlushFile(_handle);
            }

            for (var index = 0; index < batch.Count; index++)
            {
                var pending = batch[index];
                var location = locations[index];
                _committedTransactionIds.Add(pending.TransactionId);
                _recordLocations.Add(location);
                Volatile.Write(ref _highestDurableTransactionId, pending.TransactionId);
                pending.Completion.TrySetResult();
            }

            _highestCommitSequence = locations[^1].CommitSequence;
            _nextOffset = offset;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _faulted, 1);
            foreach (var pending in batch)
            {
                pending.Completion.TrySetException(exception);
            }

            throw;
        }
        finally
        {
            if (combinedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(combinedBuffer, clearArray: false);
            }

            _appendGate.Release();
        }
    }

    public async ValueTask PublishRecoveryBoundaryAsync(
        long checkpointGeneration,
        long transactionWatermark,
        int generationsToKeep,
        CancellationToken cancellationToken = default)
    {
        if (checkpointGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointGeneration));
        }

        if (transactionWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionWatermark));
        }

        if (generationsToKeep is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(generationsToKeep));
        }

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var finalPath = RecoveryBoundaryPath(_path, checkpointGeneration);
        var temporaryPath = $"{finalPath}.creating-{Guid.NewGuid():N}";
        try
        {
            ThrowIfUnavailable();
            if (transactionWatermark < _recoveryBaseTransactionId
                || transactionWatermark > _highestDurableTransactionId)
            {
                throw new InvalidOperationException(
                    $"Checkpoint watermark {transactionWatermark:N0} is outside the WAL recovery range "
                    + $"{_recoveryBaseTransactionId:N0}..{_highestDurableTransactionId:N0}.");
            }

            var boundaryOffset = _recoveryBoundaryOffset;
            var priorTransactionId = _recoveryPriorTransactionId;
            var priorCommitSequence = _recoveryPriorCommitSequence;
            var priorRecordOffset = _recoveryPriorRecordOffset;
            var priorRecordLength = _recoveryPriorRecordLength;
            var observedTail = false;
            foreach (var location in _recordLocations)
            {
                if (location.TransactionId <= transactionWatermark)
                {
                    if (observedTail)
                    {
                        throw new ObjectLogCorruptionException(
                            "Mutation WAL transaction IDs are not ordered around the checkpoint watermark.");
                    }

                    boundaryOffset = checked(location.Offset + location.RecordLength);
                    priorTransactionId = location.TransactionId;
                    priorCommitSequence = location.CommitSequence;
                    priorRecordOffset = location.Offset;
                    priorRecordLength = location.RecordLength;
                }
                else
                {
                    observedTail = true;
                }
            }

            if (boundaryOffset < FileHeaderBytes || boundaryOffset > _nextOffset)
            {
                throw new ObjectLogCorruptionException("Mutation WAL recovery boundary is outside the durable file.");
            }

            var bytes = new byte[RecoveryBoundaryBytes];
            RecoveryBoundaryMagic.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), RecoveryBoundaryFormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), RecoveryBoundaryBytes);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), checkpointGeneration);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), transactionWatermark);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), _generation);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), _baseTransactionId);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), _baseCommitSequence);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), boundaryOffset);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), priorTransactionId);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72), priorCommitSequence);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), priorRecordOffset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(88), priorRecordLength);
            ComputePrefixHash(_handle, boundaryOffset).CopyTo(bytes.AsSpan(96));
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(128), DateTimeOffset.UtcNow.UtcTicks);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(140), Crc32C.Compute(bytes.AsSpan(0, 140)));

            // The seek boundary is a validated derived accelerator, not recovery
            // authority. It deliberately avoids a second durable metadata barrier:
            // a missing/torn sidecar after power loss is rejected and recovery scans
            // the complete WAL instead.
            await using (var boundaryStream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await boundaryStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await boundaryStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, finalPath, overwrite: true);

        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Optional accelerator publication must not fault a store whose
            // checkpoint and WAL are already durable. Reopen safely scans from the
            // WAL header when no validated sidecar is available.
        }
        finally
        {
            TryDelete(temporaryPath);
            _appendGate.Release();
        }

        PruneRecoveryBoundaries(_path, generationsToKeep);
    }

    public async ValueTask RecycleAsync(
        long transactionWatermark,
        long minimumReclaimBytes,
        long maximumBytesBeforeForcedRecycle,
        CancellationToken cancellationToken = default)
    {
        if (transactionWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionWatermark));
        }


        if (minimumReclaimBytes < 0
            || maximumBytesBeforeForcedRecycle < FileHeaderBytes
            || maximumBytesBeforeForcedRecycle < minimumReclaimBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumReclaimBytes));
        }

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{_path}.recycling-{Guid.NewGuid():N}";
        try
        {
            ThrowIfUnavailable();
            if (transactionWatermark <= _baseTransactionId)
            {
                return;
            }

            if (transactionWatermark < _recoveryBaseTransactionId)
            {
                // The current process intentionally did not decode the checkpoint-covered WAL prefix.
                // Retain it until a newer recovery root advances past the persisted boundary.
                return;
            }

            if (transactionWatermark > _highestDurableTransactionId)
            {
                throw new InvalidOperationException(
                    $"Cannot recycle the mutation WAL through transaction {transactionWatermark:N0}; "
                    + $"its highest durable transaction is {_highestDurableTransactionId:N0}.");
            }

            var retained = _recordLocations
                .Where(location => location.TransactionId > transactionWatermark)
                .OrderBy(static location => location.CommitSequence)
                .ToArray();
            var reclaimableBytes = retained.Length == 0
                ? _nextOffset - FileHeaderBytes
                : retained[0].Offset - FileHeaderBytes;
            if (reclaimableBytes < minimumReclaimBytes
                && _nextOffset < maximumBytesBeforeForcedRecycle)
            {
                return;
            }

            var baseCommitSequence = retained.Length == 0
                ? _highestCommitSequence
                : checked(retained[0].CommitSequence - 1);
            for (var index = 0; index < retained.Length; index++)
            {
                if (retained[index].CommitSequence != checked(baseCommitSequence + index + 1))
                {
                    throw new ObjectLogCorruptionException(
                        "The mutation WAL cannot recycle a non-contiguous committed suffix.");
                }
            }

            var generation = checked(_generation + 1);
            var newLocations = new List<WalRecordLocation>(retained.Length);
            using (var replacement = File.OpenHandle(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       FileOptions.WriteThrough | FileOptions.RandomAccess))
            {
                RandomAccess.Write(
                    replacement,
                    CreateFileHeader(generation, transactionWatermark, baseCommitSequence),
                    0);
                var nextOffset = (long)FileHeaderBytes;
                foreach (var location in retained)
                {
                    var record = new byte[location.RecordLength];
                    ReadExactly(_handle, record, location.Offset, _path);
                    RandomAccess.Write(replacement, record, nextOffset);
                    newLocations.Add(location with { Offset = nextOffset });
                    nextOffset += record.Length;
                }

                _durableFileSystem.FlushFile(replacement);
            }

            try
            {
                var previousHandle = _handle;
                previousHandle.Dispose();
                _durableFileSystem.ReplaceFile(temporaryPath, _path);
                var replacementHandle = OpenWalHandle(_path, _writeThrough);
                _handle = replacementHandle;
            }
            catch
            {
                try
                {
                    _handle = OpenWalHandle(_path, _writeThrough);
                }
                catch
                {
                    // The store is faulted below; recovery will reopen the durable path.
                }

                Volatile.Write(ref _faulted, 1);
                throw;
            }

            _generation = generation;
            _baseTransactionId = transactionWatermark;
            _baseCommitSequence = baseCommitSequence;
            _recoveryBaseTransactionId = transactionWatermark;
            _recoveryBoundaryOffset = FileHeaderBytes;
            _recoveryPriorTransactionId = transactionWatermark;
            _recoveryPriorCommitSequence = baseCommitSequence;
            _recoveryPriorRecordOffset = 0;
            _recoveryPriorRecordLength = 0;
            _nextOffset = FileHeaderBytes + retained.Sum(static location => (long)location.RecordLength);
            _recordLocations.Clear();
            _recordLocations.AddRange(newLocations);
            _committedTransactionIds.RemoveWhere(transactionId => transactionId <= transactionWatermark);
            _recoveryCommits.RemoveAll(commit => commit.TransactionId <= transactionWatermark);
        }
        catch
        {
            Volatile.Write(ref _faulted, 1);
            throw;
        }
        finally
        {
            TryDelete(temporaryPath);
            _appendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commitQueue.Writer.TryComplete();
        await _commitWorker.ConfigureAwait(false);
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

    internal async ValueTask WriteCheckpointBaseAsync(
        string destinationPath,
        long transactionWatermark,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (transactionWatermark < _recoveryBaseTransactionId
                || transactionWatermark > _highestDurableTransactionId)
            {
                throw new InvalidOperationException(
                    $"Cannot create a checkpoint-based WAL at transaction {transactionWatermark:N0}.");
            }

            var baseCommitSequence = _recoveryPriorCommitSequence;
            var observedTail = false;
            foreach (var location in _recordLocations)
            {
                if (location.TransactionId <= transactionWatermark)
                {
                    if (observedTail)
                    {
                        throw new ObjectLogCorruptionException(
                            "Mutation WAL transaction IDs are not ordered around the backup watermark.");
                    }

                    baseCommitSequence = location.CommitSequence;
                }
                else
                {
                    observedTail = true;
                }
            }

            using var handle = File.OpenHandle(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough | FileOptions.RandomAccess);
            RandomAccess.Write(
                handle,
                CreateFileHeader(
                    generation: 1,
                    baseTransactionId: transactionWatermark,
                    baseCommitSequence),
                0);
            _durableFileSystem.FlushFile(handle);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    private sealed record PendingWalCommit(
        long TransactionId,
        IReadOnlyList<SegmentCorePendingMutation> Mutations,
        IReadOnlyList<StripedObjectLogRecordAddress> PreparedAddresses,
        TaskCompletionSource Completion);

    private static byte[] CreateRecord(
        long transactionId,
        long sequence,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        IReadOnlyList<StripedObjectLogRecordAddress> preparedAddresses)
    {
        var encoded = new (
            SegmentCorePendingMutation Mutation,
            StripedObjectLogRecordAddress PreparedAddress,
            byte[] KeyBytes)[mutations.Count];
        var payloadBytes = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            var preparedAddress = preparedAddresses[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(mutation.Key);
            if (mutation.Kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete)
                || (mutation.Kind == ObjectLogRecordKind.Delete && mutation.Payload.Length != 0)
                || preparedAddress.Stripe < 0
                || preparedAddress.Address.SegmentId <= 0
                || preparedAddress.Address.Offset < 0
                || preparedAddress.Address.FrameLength <= 0
                || preparedAddress.Address.Lsn <= 0
                || !keys.Add(mutation.Key))
            {
                throw new ArgumentException("The WAL mutation set is invalid or contains duplicate keys.", nameof(mutations));
            }

            var keyBytes = Encoding.UTF8.GetBytes(mutation.Key);
            encoded[index] = (mutation, preparedAddress, keyBytes);
            payloadBytes = checked(payloadBytes + MutationHeaderBytes + keyBytes.Length + mutation.Payload.Length);
        }

        var minimumLength = checked(HeaderBytes + payloadBytes + FooterBytes);
        var recordLength = Align(minimumLength);
        if (recordLength > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutations),
                $"Transaction WAL record length {recordLength:N0} exceeds {ObjectLogFormat.MaximumFrameBytes:N0} bytes.");
        }

        var record = new byte[recordLength];
        var payload = record.AsSpan(HeaderBytes, payloadBytes);
        var cursor = 0;
        foreach (var item in encoded)
        {
            var header = payload.Slice(cursor, MutationHeaderBytes);
            header[0] = (byte)item.Mutation.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], item.PreparedAddress.Stripe);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], item.KeyBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], item.Mutation.Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                header[16..],
                ObjectLogFormat.ComputeDataChecksum(item.Mutation.Key, item.Mutation.Payload));
            BinaryPrimitives.WriteInt64LittleEndian(header[24..], item.PreparedAddress.Address.SegmentId);
            BinaryPrimitives.WriteInt64LittleEndian(header[32..], item.PreparedAddress.Address.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], item.PreparedAddress.Address.FrameLength);
            BinaryPrimitives.WriteInt64LittleEndian(header[48..], item.PreparedAddress.Address.Lsn);
            cursor += MutationHeaderBytes;
            item.KeyBytes.CopyTo(payload[cursor..]);
            cursor += item.KeyBytes.Length;
            item.Mutation.Payload.CopyTo(payload[cursor..]);
            cursor += item.Mutation.Payload.Length;
        }

        var payloadChecksum = Crc32C.Compute(payload);
        Magic.CopyTo(record);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), recordLength);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), mutations.Count);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), transactionId);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(32), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(40), payloadBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(48), payloadChecksum);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(56), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), ComputeHeaderChecksum(record.AsSpan(0, HeaderBytes)));

        var footer = record.AsSpan(recordLength - FooterBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(footer, FooterMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footer[4..], recordLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..], transactionId);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], payloadChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], CommitMarker);
        return record;
    }

    private static WalRecovery Recover(
        string path,
        IDurableFileSystem durableFileSystem,
        long checkpointGeneration,
        long checkpointTransactionWatermark)
    {
        var commits = new List<TransactionWalCommit>();
        var committedTransactionIds = new HashSet<long>();
        var recordLocations = new List<WalRecordLocation>();
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.RandomAccess);
        var physicalLength = RandomAccess.GetLength(handle);
        var offset = 0L;
        var generation = 0L;
        var baseTransactionId = 0L;
        var baseCommitSequence = 0L;
        if (physicalLength >= FileMagic.Length)
        {
            Span<byte> prefix = stackalloc byte[8];
            ReadExactly(handle, prefix, 0, path);
            if (prefix.SequenceEqual(FileMagic))
            {
                if (physicalLength < FileHeaderBytes)
                {
                    throw new ObjectLogCorruptionException(
                        $"Transaction WAL '{path}' has an incomplete file header.");
                }

                Span<byte> fileHeader = stackalloc byte[FileHeaderBytes];
                ReadExactly(handle, fileHeader, 0, path);
                ParseFileHeader(fileHeader, path, out generation, out baseTransactionId, out baseCommitSequence);
                offset = FileHeaderBytes;
            }
            else if (!prefix.SequenceEqual(Magic))
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction WAL '{path}' has an unknown file signature.");
            }
        }

        var recoveryBaseTransactionId = baseTransactionId;
        var recoveryBoundaryOffset = (long)FileHeaderBytes;
        var recoveryPriorTransactionId = baseTransactionId;
        var recoveryPriorCommitSequence = baseCommitSequence;
        var recoveryPriorRecordOffset = 0L;
        var recoveryPriorRecordLength = 0;
        var expectedSequence = checked(baseCommitSequence + 1);
        WalRecoveryBoundary? boundary = null;
        var usedBoundary = checkpointGeneration > 0
            && checkpointTransactionWatermark >= baseTransactionId
            && TryLoadRecoveryBoundary(
                path,
                handle,
                physicalLength,
                checkpointGeneration,
                checkpointTransactionWatermark,
                generation,
                baseTransactionId,
                baseCommitSequence,
                out boundary);
        if (usedBoundary)
        {
            offset = boundary!.Offset;
            expectedSequence = boundary.NextCommitSequence;
            recoveryBaseTransactionId = checkpointTransactionWatermark;
            recoveryBoundaryOffset = boundary.Offset;
            recoveryPriorTransactionId = boundary.PriorTransactionId;
            recoveryPriorCommitSequence = boundary.PriorCommitSequence;
            recoveryPriorRecordOffset = boundary.PriorRecordOffset;
            recoveryPriorRecordLength = boundary.PriorRecordLength;
        }

        var incompleteTail = false;
        Span<byte> header = stackalloc byte[HeaderBytes];
        while (physicalLength - offset >= HeaderBytes)
        {
            ReadExactly(handle, header, offset, path);
            if (!TryParseHeader(header, out var parsed))
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction WAL '{path}' has an invalid header at offset {offset:N0}.");
            }

            if (physicalLength - offset < parsed.RecordLength)
            {
                incompleteTail = true;
                break;
            }

            var record = new byte[parsed.RecordLength];
            ReadExactly(handle, record, offset, path);
            var footer = record.AsSpan(parsed.RecordLength - FooterBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) != CommitMarker)
            {
                incompleteTail = true;
                break;
            }

            if (parsed.CommitSequence != expectedSequence
                || parsed.TransactionId <= recoveryBaseTransactionId
                || BinaryPrimitives.ReadUInt32LittleEndian(footer) != FooterMagic
                || BinaryPrimitives.ReadInt32LittleEndian(footer[4..]) != parsed.RecordLength
                || BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) != parsed.TransactionId
                || BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != parsed.PayloadChecksum)
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction WAL '{path}' has an invalid committed record at offset {offset:N0}.");
            }

            var payload = record.AsSpan(HeaderBytes, parsed.PayloadLength);
            if (Crc32C.Compute(payload) != parsed.PayloadChecksum)
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction WAL '{path}' has a corrupt committed payload at offset {offset:N0}.");
            }

            var mutations = ParseMutations(payload, parsed.MutationCount, path, offset);
            if (!committedTransactionIds.Add(parsed.TransactionId))
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction WAL '{path}' repeats transaction {parsed.TransactionId:N0}.");
            }

            commits.Add(new TransactionWalCommit(
                parsed.TransactionId,
                parsed.CommitSequence,
                mutations));
            recordLocations.Add(new WalRecordLocation(
                parsed.TransactionId,
                parsed.CommitSequence,
                offset,
                parsed.RecordLength));
            expectedSequence++;
            offset += parsed.RecordLength;
        }

        if (physicalLength - offset is > 0 and < HeaderBytes)
        {
            incompleteTail = true;
        }

        if (incompleteTail && usedBoundary)
        {
            // A persisted boundary may skip only checkpoint-covered records. Tail repair is deliberately
            // delegated to a complete scan so a stale accelerator can never authorize truncation.
            handle.Dispose();
            return Recover(path, durableFileSystem, checkpointGeneration: 0, checkpointTransactionWatermark: 0);
        }

        if (offset != physicalLength)
        {
            RandomAccess.SetLength(handle, offset);
            durableFileSystem.FlushFile(handle);
        }

        return new WalRecovery(
            commits,
            recordLocations,
            offset,
            generation,
            baseTransactionId,
            baseCommitSequence,
            recoveryBaseTransactionId,
            checked(expectedSequence - 1),
            recoveryBoundaryOffset,
            recoveryPriorTransactionId,
            recoveryPriorCommitSequence,
            recoveryPriorRecordOffset,
            recoveryPriorRecordLength);
    }

    private static bool TryLoadRecoveryBoundary(
        string walPath,
        SafeFileHandle walHandle,
        long physicalWalLength,
        long checkpointGeneration,
        long checkpointTransactionWatermark,
        long walGeneration,
        long walBaseTransactionId,
        long walBaseCommitSequence,
        out WalRecoveryBoundary? boundary)
    {
        boundary = null;
        var path = RecoveryBoundaryPath(walPath, checkpointGeneration);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != RecoveryBoundaryBytes
                || !bytes.AsSpan(0, RecoveryBoundaryMagic.Length).SequenceEqual(RecoveryBoundaryMagic)
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) != RecoveryBoundaryFormatVersion
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)) != RecoveryBoundaryBytes
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) != checkpointGeneration
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)) != checkpointTransactionWatermark
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)) != walGeneration
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)) != walBaseTransactionId
                || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)) != walBaseCommitSequence
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(140)) != Crc32C.Compute(bytes.AsSpan(0, 140)))
            {
                return false;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56));
            var priorTransactionId = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64));
            var priorCommitSequence = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72));
            var priorRecordOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(80));
            var priorRecordLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(88));
            if (offset < FileHeaderBytes
                || offset > physicalWalLength
                || priorTransactionId < walBaseTransactionId
                || priorTransactionId > checkpointTransactionWatermark
                || priorCommitSequence < walBaseCommitSequence)
            {
                return false;
            }

            if (offset == FileHeaderBytes)
            {
                if (priorTransactionId != walBaseTransactionId
                    || priorCommitSequence != walBaseCommitSequence
                    || priorRecordOffset != 0
                    || priorRecordLength != 0)
                {
                    return false;
                }
            }
            else if (priorRecordOffset < FileHeaderBytes
                     || priorRecordLength < HeaderBytes + FooterBytes
                     || priorRecordLength > ObjectLogFormat.MaximumFrameBytes
                     || priorRecordOffset + priorRecordLength != offset
                     || !ValidateBoundaryRecord(
                         walHandle,
                         walPath,
                         priorRecordOffset,
                         priorRecordLength,
                         priorTransactionId,
                         priorCommitSequence))
            {
                return false;
            }

            var actualHash = ComputePrefixHash(walHandle, offset);
            if (!actualHash.AsSpan().SequenceEqual(bytes.AsSpan(96, SHA256.HashSizeInBytes)))
            {
                return false;
            }

            if (physicalWalLength - offset >= HeaderBytes)
            {
                Span<byte> nextHeader = stackalloc byte[HeaderBytes];
                ReadExactly(walHandle, nextHeader, offset, walPath);
                if (!TryParseHeader(nextHeader, out var next)
                    || next.TransactionId <= checkpointTransactionWatermark
                    || next.CommitSequence != checked(priorCommitSequence + 1))
                {
                    return false;
                }
            }

            boundary = new WalRecoveryBoundary(
                offset,
                checked(priorCommitSequence + 1),
                priorTransactionId,
                priorCommitSequence,
                priorRecordOffset,
                priorRecordLength);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateBoundaryRecord(
        SafeFileHandle handle,
        string path,
        long offset,
        int recordLength,
        long expectedTransactionId,
        long expectedCommitSequence)
    {
        var record = new byte[recordLength];
        ReadExactly(handle, record, offset, path);
        if (!TryParseHeader(record.AsSpan(0, HeaderBytes), out var parsed)
            || parsed.RecordLength != recordLength
            || parsed.TransactionId != expectedTransactionId
            || parsed.CommitSequence != expectedCommitSequence)
        {
            return false;
        }

        var footer = record.AsSpan(recordLength - FooterBytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(footer) == FooterMagic
            && BinaryPrimitives.ReadInt32LittleEndian(footer[4..]) == recordLength
            && BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) == expectedTransactionId
            && BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) == parsed.PayloadChecksum
            && BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) == CommitMarker
            && Crc32C.Compute(record.AsSpan(HeaderBytes, parsed.PayloadLength)) == parsed.PayloadChecksum;
    }

    private static byte[] ComputePrefixHash(SafeFileHandle handle, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(PrefixHashBufferBytes);
        try
        {
            var offset = 0L;
            while (offset < length)
            {
                var requested = (int)Math.Min(buffer.Length, length - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("The transaction WAL ended while hashing a recovery boundary.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                offset += read;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> header, out WalHeader parsed)
    {
        parsed = default;
        if (!header[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != FormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != HeaderBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(header[52..]) != ComputeHeaderChecksum(header))
        {
            return false;
        }

        parsed = new WalHeader(
            BinaryPrimitives.ReadInt32LittleEndian(header[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[48..]));
        var minimum = (long)HeaderBytes + parsed.PayloadLength + FooterBytes;
        return parsed.RecordLength >= minimum
            && parsed.RecordLength <= ObjectLogFormat.MaximumFrameBytes
            && parsed.RecordLength % Alignment == 0
            && parsed.MutationCount is > 0 and <= MaximumMutations
            && parsed.TransactionId > 0
            && parsed.CommitSequence > 0
            && parsed.PayloadLength >= parsed.MutationCount * MutationHeaderBytes;
    }

    private static IReadOnlyList<TransactionWalMutation> ParseMutations(
        ReadOnlySpan<byte> payload,
        int mutationCount,
        string path,
        long recordOffset)
    {
        var mutations = new TransactionWalMutation[mutationCount];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cursor = 0;
        for (var index = 0; index < mutationCount; index++)
        {
            if (payload.Length - cursor < MutationHeaderBytes)
            {
                throw InvalidPayload(path, recordOffset);
            }

            var header = payload.Slice(cursor, MutationHeaderBytes);
            var kind = (ObjectLogRecordKind)header[0];
            var stripe = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var segmentId = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
            var frameOffset = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
            var lsn = BinaryPrimitives.ReadInt64LittleEndian(header[48..]);
            cursor += MutationHeaderBytes;
            var dataLength = (long)keyLength + payloadLength;
            if (kind is not (ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete)
                || stripe < 0
                || keyLength <= 0
                || payloadLength < 0
                || (kind == ObjectLogRecordKind.Delete && payloadLength != 0)
                || segmentId <= 0
                || frameOffset < 0
                || frameLength <= 0
                || lsn <= 0
                || dataLength > payload.Length - cursor)
            {
                throw InvalidPayload(path, recordOffset);
            }

            var key = Encoding.UTF8.GetString(payload.Slice(cursor, keyLength));
            cursor += keyLength;
            var mutationPayload = payload.Slice(cursor, payloadLength).ToArray();
            cursor += payloadLength;
            if (string.IsNullOrWhiteSpace(key)
                || !keys.Add(key)
                || ObjectLogFormat.ComputeDataChecksum(key, mutationPayload) != expectedChecksum)
            {
                throw InvalidPayload(path, recordOffset);
            }

            mutations[index] = new TransactionWalMutation(
                new SegmentCorePendingMutation(key, kind, mutationPayload),
                new StripedObjectLogRecordAddress(
                    stripe,
                    new ObjectLogRecordAddress(segmentId, frameOffset, frameLength, lsn)));
        }

        if (cursor != payload.Length)
        {
            throw InvalidPayload(path, recordOffset);
        }

        return mutations;
    }

    private static ObjectLogCorruptionException InvalidPayload(string path, long offset) => new(
        $"Transaction WAL '{path}' has an invalid mutation payload at offset {offset:N0}.");

    private static uint ComputeHeaderChecksum(ReadOnlySpan<byte> header)
    {
        Span<byte> copy = stackalloc byte[HeaderBytes];
        header.CopyTo(copy);
        copy.Slice(52, sizeof(uint)).Clear();
        return Crc32C.Compute(copy);
    }

    private static byte[] CreateFileHeader(
        long generation,
        long baseTransactionId,
        long baseCommitSequence)
    {
        if (generation <= 0 || baseTransactionId < 0 || baseCommitSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        var header = new byte[FileHeaderBytes];
        FileMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FileFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), FileHeaderBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), generation);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), baseTransactionId);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), baseCommitSequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), Crc32C.Compute(header.AsSpan(0, 60)));
        return header;
    }

    private static void ParseFileHeader(
        ReadOnlySpan<byte> header,
        string path,
        out long generation,
        out long baseTransactionId,
        out long baseCommitSequence)
    {
        generation = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        baseTransactionId = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        baseCommitSequence = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        if (!header[..FileMagic.Length].SequenceEqual(FileMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != FileFormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != FileHeaderBytes
            || generation <= 0
            || baseTransactionId < 0
            || baseCommitSequence < 0
            || BinaryPrimitives.ReadUInt32LittleEndian(header[60..]) != Crc32C.Compute(header[..60]))
        {
            throw new ObjectLogCorruptionException(
                $"Transaction WAL '{path}' has an invalid file header.");
        }
    }

    private static int Align(int value) => checked((value + Alignment - 1) & ~(Alignment - 1));

    private static void EnsureWalFile(string path, IDurableFileSystem durableFileSystem)
    {
        if (File.Exists(path))
        {
            return;
        }

        var temporaryPath = $"{path}.creating-{Guid.NewGuid():N}";
        try
        {
            using (var handle = File.OpenHandle(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       FileOptions.WriteThrough | FileOptions.RandomAccess))
            {
                RandomAccess.Write(handle, CreateFileHeader(
                    generation: 1,
                    baseTransactionId: 0,
                    baseCommitSequence: 0), 0);
                durableFileSystem.FlushFile(handle);
            }

            try
            {
                durableFileSystem.PublishFile(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset, string path)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = RandomAccess.Read(handle, destination[read..], offset + read);
            if (count == 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Unexpected end of transaction WAL '{path}' at offset {offset + read:N0}.");
            }

            read += count;
        }
    }

    private static SafeFileHandle OpenWalHandle(string path, bool writeThrough) => File.OpenHandle(
        path,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.Read | FileShare.Delete,
        FileOptions.Asynchronous
        | FileOptions.RandomAccess
        | (writeThrough ? FileOptions.WriteThrough : FileOptions.None));

    private static string RecoveryBoundaryPath(string walPath, long checkpointGeneration) => Path.Combine(
        Path.GetDirectoryName(walPath)!,
        $"wal-boundary-{checkpointGeneration:D20}.idbwbd");

    private static void PruneRecoveryBoundaries(string walPath, int generationsToKeep)
    {
        var directory = Path.GetDirectoryName(walPath)!;
        foreach (var path in Directory
                     .EnumerateFiles(directory, "wal-boundary-*.idbwbd", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(static item => item, StringComparer.Ordinal)
                     .Skip(generationsToKeep))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A crash-only temporary is outside the WAL recovery name.
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException(
                "The mutation WAL has an uncertain durability outcome and must be reopened.");
        }
    }

    private readonly record struct WalHeader(
        int RecordLength,
        int MutationCount,
        long TransactionId,
        long CommitSequence,
        int PayloadLength,
        uint PayloadChecksum);

    private readonly record struct WalRecordLocation(
        long TransactionId,
        long CommitSequence,
        long Offset,
        int RecordLength);

    private sealed record WalRecoveryBoundary(
        long Offset,
        long NextCommitSequence,
        long PriorTransactionId,
        long PriorCommitSequence,
        long PriorRecordOffset,
        int PriorRecordLength);

    private sealed record WalRecovery(
        List<TransactionWalCommit> Commits,
        List<WalRecordLocation> RecordLocations,
        long ValidLength,
        long Generation,
        long BaseTransactionId,
        long BaseCommitSequence,
        long RecoveryBaseTransactionId,
        long HighestObservedCommitSequence,
        long RecoveryBoundaryOffset,
        long RecoveryPriorTransactionId,
        long RecoveryPriorCommitSequence,
        long RecoveryPriorRecordOffset,
        int RecoveryPriorRecordLength);
}
