using System.Buffers.Binary;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

internal readonly record struct TransactionMutationIdentity(
    int Stripe,
    ObjectLogRecordKind Kind,
    ObjectLogRecordAddress Address,
    uint DataChecksum);

internal readonly record struct TransactionCommitDescriptor(
    long TransactionId,
    int MutationCount,
    int ParticipantCount,
    uint MutationChecksum,
    long CommitSequence);

internal sealed class TransactionCommitSnapshot
{
    private readonly IReadOnlyDictionary<long, TransactionCommitDescriptor> _commits;

    public TransactionCommitSnapshot(
        IReadOnlyDictionary<long, TransactionCommitDescriptor> commits,
        long highestTransactionId,
        long highestCommitSequence)
    {
        _commits = commits;
        HighestTransactionId = highestTransactionId;
        HighestCommitSequence = highestCommitSequence;
        CommittedTransactionIds = commits.Keys.ToHashSet();
    }

    public IReadOnlySet<long> CommittedTransactionIds { get; }

    public long HighestTransactionId { get; }

    public long HighestCommitSequence { get; }

    public bool IsCommitted(long transactionId) => _commits.ContainsKey(transactionId);

    public void ValidateRecoveryRecords(
        IReadOnlyList<StripedObjectLogReplayRecord> records,
        long transactionWatermark,
        bool requireEveryCommitAfterWatermark)
    {
        var committedRecords = records
            .Where(record => record.Record.TransactionId > 0 && IsCommitted(record.Record.TransactionId))
            .GroupBy(static record => record.Record.TransactionId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        foreach (var pair in committedRecords)
        {
            if (pair.Key <= transactionWatermark)
            {
                throw new ObjectLogCorruptionException(
                    $"Committed transaction {pair.Key:N0} crosses primary-index checkpoint watermark {transactionWatermark:N0}.");
            }

            ValidateDescriptor(_commits[pair.Key], pair.Value);
        }

        if (!requireEveryCommitAfterWatermark)
        {
            return;
        }

        foreach (var descriptor in _commits.Values.Where(commit => commit.TransactionId > transactionWatermark))
        {
            if (!committedRecords.ContainsKey(descriptor.TransactionId))
            {
                throw new ObjectLogCorruptionException(
                    $"Durable transaction {descriptor.TransactionId:N0} is missing its data frames.");
            }
        }
    }

    private static void ValidateDescriptor(
        TransactionCommitDescriptor descriptor,
        IReadOnlyList<StripedObjectLogReplayRecord> records)
    {
        var identities = records.Select(static record => new TransactionMutationIdentity(
            record.Stripe,
            record.Record.Kind,
            record.Record.Address,
            record.Record.DataChecksum)).ToArray();
        var participants = identities.Select(static identity => identity.Stripe).Distinct().Count();
        var checksum = TransactionCommitJournal.ComputeMutationChecksum(identities);
        if (identities.Length != descriptor.MutationCount
            || participants != descriptor.ParticipantCount
            || checksum != descriptor.MutationChecksum)
        {
            throw new ObjectLogCorruptionException(
                $"Durable transaction {descriptor.TransactionId:N0} does not match its committed mutation set.");
        }
    }
}

internal sealed class TransactionCommitJournal : IAsyncDisposable
{
    private const int RecordBytes = 64;
    private const int FormatVersion = 1;
    private const uint CommitMarker = 0xC07A17ED;
    private const string DirectoryName = "transactions";
    private const string FileName = "commit-journal.idbtxn";
    private static ReadOnlySpan<byte> Magic => "IDBTXN01"u8;

    private readonly IDurableFileSystem _durableFileSystem;
    private readonly bool _writeThrough;
    private readonly string _path;
    private readonly SafeFileHandle _handle;
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private readonly Dictionary<long, TransactionCommitDescriptor> _commits;
    private long _nextOffset;
    private long _highestTransactionId;
    private long _highestCommitSequence;
    private int _faulted;
    private int _disposed;

    private TransactionCommitJournal(
        IDurableFileSystem durableFileSystem,
        bool writeThrough,
        string path,
        SafeFileHandle handle,
        Dictionary<long, TransactionCommitDescriptor> commits,
        long nextOffset,
        long highestTransactionId,
        long highestCommitSequence)
    {
        _durableFileSystem = durableFileSystem;
        _writeThrough = writeThrough;
        _path = path;
        _handle = handle;
        _commits = commits;
        _nextOffset = nextOffset;
        _highestTransactionId = highestTransactionId;
        _highestCommitSequence = highestCommitSequence;
    }

    public long HighestDurableCommittedTransactionId =>
        _commits.Keys.DefaultIfEmpty(0).Max();

    public static ValueTask<TransactionCommitJournal> OpenAsync(
        string rootDirectory,
        ObjectLogDurabilityStrategy durabilityStrategy,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(durableFileSystem);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetFullPath(rootDirectory), DirectoryName);
        var directoryExisted = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (!directoryExisted)
        {
            durableFileSystem.FlushDirectory(Path.GetFullPath(rootDirectory));
        }
        var path = Path.Combine(directory, FileName);
        EnsureJournalFile(path, durableFileSystem);
        var recovery = Recover(path, durableFileSystem);
        var writeThrough = durabilityStrategy == ObjectLogDurabilityStrategy.WriteThrough;
        var handle = File.OpenHandle(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.Asynchronous
            | FileOptions.RandomAccess
            | (writeThrough ? FileOptions.WriteThrough : FileOptions.None));
        return ValueTask.FromResult(new TransactionCommitJournal(
            durableFileSystem,
            writeThrough,
            path,
            handle,
            recovery.Commits,
            recovery.ValidLength,
            recovery.HighestTransactionId,
            recovery.HighestCommitSequence));
    }

    public TransactionCommitSnapshot CreateSnapshot() => new(
        new Dictionary<long, TransactionCommitDescriptor>(_commits),
        Volatile.Read(ref _highestTransactionId),
        Volatile.Read(ref _highestCommitSequence));

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

    public async ValueTask AppendCommitAsync(
        long transactionId,
        IReadOnlyList<TransactionMutationIdentity> mutations,
        CancellationToken cancellationToken = default)
    {
        if (transactionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
        {
            throw new ArgumentException("A durable commit must contain at least one mutation.", nameof(mutations));
        }

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_commits.ContainsKey(transactionId))
            {
                throw new InvalidOperationException($"Transaction {transactionId:N0} is already committed.");
            }

            var sequence = checked(_highestCommitSequence + 1);
            var descriptor = new TransactionCommitDescriptor(
                transactionId,
                mutations.Count,
                mutations.Select(static mutation => mutation.Stripe).Distinct().Count(),
                ComputeMutationChecksum(mutations),
                sequence);
            var bytes = CreateRecord(descriptor);
            try
            {
                await RandomAccess.WriteAsync(
                    _handle,
                    bytes,
                    _nextOffset,
                    CancellationToken.None).ConfigureAwait(false);
                if (!_writeThrough)
                {
                    _durableFileSystem.FlushFile(_handle);
                }
            }
            catch
            {
                Volatile.Write(ref _faulted, 1);
                throw;
            }

            _nextOffset += RecordBytes;
            _highestCommitSequence = sequence;
            _commits.Add(transactionId, descriptor);
        }
        finally
        {
            _appendGate.Release();
        }
    }

    internal async ValueTask WriteCheckpointPrefixAsync(
        string destinationPath,
        long transactionWatermark,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (transactionWatermark < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionWatermark));
        }

        await _appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var ordered = _commits.Values
                .OrderBy(static commit => commit.CommitSequence)
                .ToArray();
            var retainedCount = 0;
            var observedTail = false;
            for (var index = 0; index < ordered.Length; index++)
            {
                if (ordered[index].CommitSequence != index + 1)
                {
                    throw new ObjectLogCorruptionException(
                        $"Transaction journal '{_path}' has a non-contiguous commit sequence.");
                }

                if (ordered[index].TransactionId <= transactionWatermark)
                {
                    if (observedTail)
                    {
                        throw new ObjectLogCorruptionException(
                            $"Transaction journal '{_path}' is not ordered around the backup watermark.");
                    }

                    retainedCount++;
                }
                else
                {
                    observedTail = true;
                }
            }

            var length = checked((long)retainedCount * RecordBytes);
            using var destination = File.OpenHandle(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.WriteThrough | FileOptions.RandomAccess);
            var buffer = new byte[64 * 1024];
            var offset = 0L;
            while (offset < length)
            {
                var requested = (int)Math.Min(buffer.Length, length - offset);
                var read = RandomAccess.Read(_handle, buffer.AsSpan(0, requested), offset);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Transaction journal '{_path}' ended while creating a backup prefix.");
                }

                RandomAccess.Write(destination, buffer.AsSpan(0, read), offset);
                offset += read;
            }

            _durableFileSystem.FlushFile(destination);
        }
        finally
        {
            _appendGate.Release();
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

    internal static uint ComputeMutationChecksum(IReadOnlyList<TransactionMutationIdentity> mutations)
    {
        var ordered = mutations
            .OrderBy(static mutation => mutation.Stripe)
            .ThenBy(static mutation => mutation.Address.Lsn)
            .ToArray();
        var crc = Crc32C.InitialState;
        Span<byte> bytes = stackalloc byte[40];
        foreach (var mutation in ordered)
        {
            bytes.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(bytes, mutation.Stripe);
            bytes[4] = (byte)mutation.Kind;
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], mutation.Address.SegmentId);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], mutation.Address.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], mutation.Address.FrameLength);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[28..], mutation.Address.Lsn);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[36..], mutation.DataChecksum);
            crc = Crc32C.Append(crc, bytes);
        }

        return Crc32C.Complete(crc);
    }

    private static byte[] CreateRecord(TransactionCommitDescriptor descriptor)
    {
        var bytes = new byte[RecordBytes];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), RecordBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), descriptor.TransactionId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), descriptor.MutationCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), descriptor.ParticipantCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), descriptor.MutationChecksum);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), descriptor.CommitSequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), DateTimeOffset.UtcNow.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), CommitMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), ComputeRecordChecksum(bytes));
        return bytes;
    }

    private static JournalRecovery Recover(string path, IDurableFileSystem durableFileSystem)
    {
        var commits = new Dictionary<long, TransactionCommitDescriptor>();
        if (!File.Exists(path))
        {
            return new JournalRecovery(commits, 0, 0, 0);
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.RandomAccess);
        var physicalLength = RandomAccess.GetLength(handle);
        var offset = 0L;
        var highestTransactionId = 0L;
        var highestCommitSequence = 0L;
        Span<byte> bytes = stackalloc byte[RecordBytes];
        while (physicalLength - offset >= RecordBytes)
        {
            ReadExactly(handle, bytes, offset, path);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[60..]) != CommitMarker)
            {
                break;
            }

            if (!TryParseRecord(bytes, out var descriptor))
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction commit journal '{path}' is corrupt at offset {offset:N0}.");
            }

            if (descriptor.CommitSequence != highestCommitSequence + 1
                || !commits.TryAdd(descriptor.TransactionId, descriptor))
            {
                throw new ObjectLogCorruptionException(
                    $"Transaction commit journal '{path}' has a duplicate or non-contiguous commit at offset {offset:N0}.");
            }

            highestTransactionId = Math.Max(highestTransactionId, descriptor.TransactionId);
            highestCommitSequence = descriptor.CommitSequence;
            offset += RecordBytes;
        }

        if (offset != physicalLength)
        {
            RandomAccess.SetLength(handle, offset);
            durableFileSystem.FlushFile(handle);
        }

        return new JournalRecovery(commits, offset, highestTransactionId, highestCommitSequence);
    }

    private static void EnsureJournalFile(string path, IDurableFileSystem durableFileSystem)
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
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A crash-only temporary is outside the journal recovery name.
            }

            throw;
        }
    }

    private static bool TryParseRecord(ReadOnlySpan<byte> bytes, out TransactionCommitDescriptor descriptor)
    {
        descriptor = default;
        if (!bytes[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != FormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != RecordBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]) != ComputeRecordChecksum(bytes))
        {
            return false;
        }

        descriptor = new TransactionCommitDescriptor(
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]));
        return descriptor.TransactionId > 0
            && descriptor.MutationCount > 0
            && descriptor.ParticipantCount > 0
            && descriptor.ParticipantCount <= descriptor.MutationCount
            && descriptor.CommitSequence > 0;
    }

    private static uint ComputeRecordChecksum(ReadOnlySpan<byte> bytes)
    {
        Span<byte> copy = stackalloc byte[RecordBytes];
        bytes.CopyTo(copy);
        copy.Slice(56, sizeof(uint)).Clear();
        return Crc32C.Compute(copy);
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
                    $"Unexpected end of transaction commit journal '{path}' at offset {offset + read:N0}.");
            }

            read += count;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException(
                "The transaction commit journal has an uncertain durability outcome and must be reopened.");
        }
    }

    private sealed record JournalRecovery(
        Dictionary<long, TransactionCommitDescriptor> Commits,
        long ValidLength,
        long HighestTransactionId,
        long HighestCommitSequence);
}
