using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

internal readonly record struct PrimaryIndexChange(
    bool IsDelete,
    StripedObjectLogRecordAddress Address);

internal sealed record PrimaryIndexDeltaFileState(
    long Generation,
    long BaseGeneration,
    long TransactionWatermark,
    IReadOnlyList<ObjectLogWritePosition> Positions,
    IReadOnlyDictionary<string, PrimaryIndexChange> Changes,
    long FileBytes);

internal static class PrimaryIndexDeltaCheckpoint
{
    private const int FormatVersion = 1;
    private const int HeaderBytes = 64;
    private const int PositionBytes = 32;
    private const int FooterBytes = sizeof(uint);
    private const int MaximumKeyBytes = 1024 * 1024;
    private const int MaximumChanges = 100_000_000;
    private static ReadOnlySpan<byte> Magic => "IDBDEL01"u8;

    public static async ValueTask<PrimaryIndexDeltaFileState> WriteAsync(
        string temporaryPath,
        string finalPath,
        long generation,
        long baseGeneration,
        long transactionWatermark,
        IReadOnlyList<ObjectLogWritePosition> positions,
        IReadOnlyDictionary<string, PrimaryIndexChange> changes,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        if (generation <= 0 || baseGeneration <= 0 || baseGeneration >= generation)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (changes.Count > MaximumChanges)
        {
            throw new ArgumentOutOfRangeException(nameof(changes));
        }

        long fileBytes;
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.ReadWrite,
                                 Share = FileShare.Read,
                                 BufferSize = 64 * 1024,
                                 Options = FileOptions.Asynchronous
                                     | FileOptions.SequentialScan
                             }))
            {
                var writer = new DeltaWriter(stream);
                writer.Write(Magic);
                writer.WriteInt32(FormatVersion);
                writer.WriteInt32(HeaderBytes);
                writer.WriteInt64(generation);
                writer.WriteInt64(baseGeneration);
                writer.WriteInt32(positions.Count);
                writer.WriteInt32(changes.Count);
                writer.WriteInt64(DateTimeOffset.UtcNow.UtcTicks);
                writer.WriteInt64(transactionWatermark);
                writer.WriteInt64(0);
                foreach (var position in positions)
                {
                    writer.WriteInt64(position.SegmentId);
                    writer.WriteInt64(position.NextOffset);
                    writer.WriteInt64(position.LastLsn);
                    writer.WriteUInt32(position.SegmentHeaderChecksum);
                    writer.WriteUInt32(0);
                }

                byte[]? entryBuffer = null;
                try
                {
                    foreach (var pair in changes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var keyLength = Encoding.UTF8.GetByteCount(pair.Key);
                        if (keyLength is <= 0 or > MaximumKeyBytes)
                        {
                            throw new ArgumentOutOfRangeException(nameof(changes));
                        }

                        var entryLength = checked(36 + keyLength);
                        if (entryBuffer is null || entryBuffer.Length < entryLength)
                        {
                            if (entryBuffer is not null)
                            {
                                ArrayPool<byte>.Shared.Return(entryBuffer, clearArray: false);
                            }
                            entryBuffer = ArrayPool<byte>.Shared.Rent(entryLength);
                        }

                        var entry = entryBuffer.AsSpan(0, entryLength);
                        BinaryPrimitives.WriteInt32LittleEndian(entry, keyLength);
                        var keyBytes = entry.Slice(sizeof(int), keyLength);
                        _ = Encoding.UTF8.GetBytes(pair.Key, keyBytes);
                        var locator = entry[(sizeof(int) + keyLength)..];
                        BinaryPrimitives.WriteInt32LittleEndian(locator, pair.Value.IsDelete ? -1 : pair.Value.Address.Stripe);
                        var address = pair.Value.IsDelete ? default : pair.Value.Address.Address;
                        BinaryPrimitives.WriteInt64LittleEndian(locator[4..], address.SegmentId);
                        BinaryPrimitives.WriteInt64LittleEndian(locator[12..], address.Offset);
                        BinaryPrimitives.WriteInt32LittleEndian(locator[20..], address.FrameLength);
                        BinaryPrimitives.WriteInt64LittleEndian(locator[24..], address.Lsn);
                        writer.Write(entry);
                    }
                }
                finally
                {
                    if (entryBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(entryBuffer, clearArray: false);
                    }
                }

                writer.WriteChecksumFooter();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                durableFileSystem.FlushFile(stream.SafeFileHandle);
                fileBytes = stream.Position;
            }

            durableFileSystem.PublishFile(temporaryPath, finalPath);
            return new PrimaryIndexDeltaFileState(
                generation,
                baseGeneration,
                transactionWatermark,
                positions,
                changes,
                fileBytes);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static PrimaryIndexDeltaFileState Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length < HeaderBytes + PositionBytes + FooterBytes)
        {
            throw new ObjectLogCorruptionException($"Primary-index delta '{path}' is incomplete.");
        }

        var reader = new DeltaReader(stream, path);
        if (!reader.ReadBytes(8).SequenceEqual(Magic)
            || reader.ReadInt32() != FormatVersion
            || reader.ReadInt32() != HeaderBytes)
        {
            throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid header.");
        }

        var generation = reader.ReadInt64();
        var baseGeneration = reader.ReadInt64();
        var stripeCount = reader.ReadInt32();
        var changeCount = reader.ReadInt32();
        _ = reader.ReadInt64();
        var watermark = reader.ReadInt64();
        _ = reader.ReadInt64();
        if (generation <= 0
            || baseGeneration <= 0
            || baseGeneration >= generation
            || stripeCount is < 1 or > 64
            || changeCount is < 0 or > MaximumChanges
            || watermark < 0)
        {
            throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has invalid counts.");
        }

        var minimumLength = HeaderBytes + (long)stripeCount * PositionBytes
            + (long)changeCount * (36 + 1) + FooterBytes;
        if (stream.Length < minimumLength)
            throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has impossible counts.");

        var positions = new ObjectLogWritePosition[stripeCount];
        for (var index = 0; index < stripeCount; index++)
        {
            positions[index] = new ObjectLogWritePosition(
                reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt32());
            _ = reader.ReadUInt32();
            if (positions[index].SegmentId <= 0
                || positions[index].NextOffset < ObjectLogFormat.SegmentHeaderSize
                || positions[index].LastLsn < 0)
            {
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid position.");
            }
        }

        var changes = new Dictionary<string, PrimaryIndexChange>(Math.Min(changeCount, 65_536), StringComparer.Ordinal);
        for (var index = 0; index < changeCount; index++)
        {
            var keyLength = reader.ReadInt32();
            if (keyLength is <= 0 or > MaximumKeyBytes)
            {
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid key.");
            }

            var key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
            var stripe = reader.ReadInt32();
            var address = new ObjectLogRecordAddress(
                reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt64());
            PrimaryIndexChange change;
            if (stripe == -1)
            {
                if (address != default)
                {
                    throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid deletion.");
                }

                change = new PrimaryIndexChange(true, default);
            }
            else if (stripe < 0
                     || stripe >= stripeCount
                     || address.SegmentId is 0 or long.MinValue
                     || address.Offset < ObjectLogFormat.SegmentHeaderSize
                     || address.FrameLength < ObjectLogFormat.FrameHeaderSize + ObjectLogFormat.FrameFooterSize
                     || address.FrameLength > ObjectLogFormat.MaximumFrameBytes
                     || address.Lsn <= 0)
            {
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid locator.");
            }
            else
            {
                change = new PrimaryIndexChange(false, new StripedObjectLogRecordAddress(stripe, address));
            }

            if (!changes.TryAdd(key, change))
            {
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has a duplicate key.");
            }
        }

        reader.RequireEnd();
        return new PrimaryIndexDeltaFileState(
            generation,
            baseGeneration,
            watermark,
            positions,
            changes,
            stream.Length);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class DeltaWriter
    {
        private readonly Stream _stream;
        private uint _crc = Crc32C.InitialState;

        public DeltaWriter(Stream stream) => _stream = stream;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            _crc = Crc32C.Append(_crc, bytes);
            _stream.Write(bytes);
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteChecksumFooter()
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Crc32C.Complete(_crc));
            _stream.Write(bytes);
        }
    }

    private sealed class DeltaReader(Stream stream, string path)
    {
        private byte[] _buffer = new byte[256];
        private uint _crc = Crc32C.InitialState;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > stream.Length - FooterBytes - stream.Position)
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' is truncated.");
            if (_buffer.Length < count) _buffer = new byte[count];
            var value = _buffer.AsSpan(0, count);
            try { stream.ReadExactly(value); }
            catch (EndOfStreamException) { throw new ObjectLogCorruptionException($"Primary-index delta '{path}' is truncated."); }
            _crc = Crc32C.Append(_crc, value);
            return value;
        }

        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));

        public void RequireEnd()
        {
            if (stream.Position != stream.Length - FooterBytes)
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has trailing data.");
            Span<byte> footer = stackalloc byte[FooterBytes];
            try { stream.ReadExactly(footer); }
            catch (EndOfStreamException) { throw new ObjectLogCorruptionException($"Primary-index delta '{path}' is truncated."); }
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32C.Complete(_crc))
                throw new ObjectLogCorruptionException($"Primary-index delta '{path}' has an invalid checksum.");
        }
    }
}
