using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Idb.Storage.SegmentCore.ObjectLog;

internal static class ObjectLogFormat
{
    public const int SegmentHeaderSize = 64;
    public const int FrameHeaderSize = 48;
    public const int FrameFooterSize = 24;
    public const int FrameAlignment = 8;
    public const int MaximumFrameBytes = 256 * 1024 * 1024;

    public const uint FrameMagic = 0x46424449; // IDBF
    public const uint FooterMagic = 0x54424449; // IDBT
    public const uint CommitMarker = 0xC04D17ED;
    public const ushort FormatVersion = 1;

    private static ReadOnlySpan<byte> SegmentMagic => "IDBSEG01"u8;

    public static byte[] CreateSegmentHeader(
        long segmentId,
        long previousSegmentId,
        uint previousHeaderChecksum,
        out uint headerChecksum)
    {
        var header = new byte[SegmentHeaderSize];
        SegmentMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), SegmentHeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), segmentId);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), previousSegmentId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), previousHeaderChecksum);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), DateTimeOffset.UtcNow.UtcTicks);
        headerChecksum = ComputeHeaderChecksum(header, 48);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), headerChecksum);
        return header;
    }

    public static SegmentHeader ParseSegmentHeader(ReadOnlySpan<byte> header, string path)
    {
        if (header.Length != SegmentHeaderSize || !header[..SegmentMagic.Length].SequenceEqual(SegmentMagic))
        {
            throw new ObjectLogCorruptionException($"Segment '{path}' has an invalid magic/header size.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (version != FormatVersion || headerSize != SegmentHeaderSize)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' uses unsupported format version {version} or header size {headerSize}.");
        }

        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        var actualChecksum = ComputeHeaderChecksum(header, 48);
        if (storedChecksum != actualChecksum)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' header checksum is invalid: expected {storedChecksum:X8}, calculated {actualChecksum:X8}.");
        }

        return new SegmentHeader(
            BinaryPrimitives.ReadInt64LittleEndian(header[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[32..]),
            storedChecksum);
    }

    public static PooledFrame CreateFrame(
        ObjectLogRecordKind kind,
        long lsn,
        string? key,
        ReadOnlySpan<byte> payload,
        long auxiliary = 0)
    {
        var keyLength = string.IsNullOrEmpty(key) ? 0 : Encoding.UTF8.GetByteCount(key);
        var minimumLength = checked(FrameHeaderSize + keyLength + payload.Length + FrameFooterSize);
        var frameLength = Align(minimumLength, FrameAlignment);
        if (frameLength > MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"Phase 0 frame length {frameLength:N0} exceeds {MaximumFrameBytes:N0} bytes.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        var frame = buffer.AsSpan(0, frameLength);
        frame.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], FormatVersion);
        frame[6] = (byte)kind;
        frame[7] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(frame[8..], frameLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame[12..], keyLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame[16..], payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(frame[24..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(frame[32..], auxiliary);

        var data = frame.Slice(FrameHeaderSize, keyLength + payload.Length);
        if (keyLength > 0)
        {
            _ = Encoding.UTF8.GetBytes(key!, data[..keyLength]);
        }

        payload.CopyTo(data[keyLength..]);
        var dataChecksum = Crc32C.Compute(data);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[40..], dataChecksum);
        var headerChecksum = ComputeHeaderChecksum(frame[..FrameHeaderSize], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], headerChecksum);

        var footer = frame[^FrameFooterSize..];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, FooterMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footer[4..], frameLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..], lsn);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], dataChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], CommitMarker);
        return new PooledFrame(buffer, frameLength);
    }

    public static FrameHeader ParseFrameHeader(ReadOnlySpan<byte> header, string path, long offset)
    {
        if (header.Length != FrameHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(header) != FrameMagic)
        {
            throw new ObjectLogCorruptionException($"Segment '{path}' has an invalid frame magic at offset {offset:N0}.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var kind = (ObjectLogRecordKind)header[6];
        if (version != FormatVersion || !Enum.IsDefined(kind) || header[7] != 1)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' has an unsupported frame at offset {offset:N0}.");
        }

        var storedHeaderChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var actualHeaderChecksum = ComputeHeaderChecksum(header, 20);
        if (storedHeaderChecksum != actualHeaderChecksum)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' has an invalid frame-header checksum at offset {offset:N0}.");
        }

        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var minimum = (long)FrameHeaderSize + keyLength + payloadLength + FrameFooterSize;
        if (frameLength < minimum
            || frameLength > MaximumFrameBytes
            || frameLength % FrameAlignment != 0
            || keyLength < 0
            || payloadLength < 0)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' has invalid frame lengths at offset {offset:N0}.");
        }

        return new FrameHeader(
            kind,
            frameLength,
            keyLength,
            payloadLength,
            BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[40..]));
    }

    public static void ValidateFrame(
        ReadOnlySpan<byte> frame,
        FrameHeader header,
        string path,
        long offset)
    {
        if (frame.Length != header.FrameLength)
        {
            throw new ObjectLogCorruptionException($"Segment '{path}' frame at {offset:N0} is incomplete.");
        }

        var data = frame.Slice(FrameHeaderSize, header.KeyLength + header.PayloadLength);
        var dataChecksum = Crc32C.Compute(data);
        var footer = frame[^FrameFooterSize..];
        if (dataChecksum != header.DataChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer) != FooterMagic
            || BinaryPrimitives.ReadInt32LittleEndian(footer[4..]) != header.FrameLength
            || BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) != header.Lsn
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != dataChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) != CommitMarker)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' frame/footer checksum is invalid at offset {offset:N0}.");
        }
    }

    public static string ReadKey(ReadOnlySpan<byte> frame, FrameHeader header) =>
        header.KeyLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(frame.Slice(FrameHeaderSize, header.KeyLength));

    public static bool KeyEquals(
        ReadOnlySpan<byte> frame,
        FrameHeader header,
        string expectedKey)
    {
        ArgumentNullException.ThrowIfNull(expectedKey);
        var storedKey = frame.Slice(FrameHeaderSize, header.KeyLength);
        var expectedCharacters = expectedKey.AsSpan();
        var isAscii = true;
        for (var index = 0; index < expectedCharacters.Length; index++)
        {
            var character = expectedCharacters[index];
            if (character > 0x7F)
            {
                isAscii = false;
                break;
            }

            if (index >= storedKey.Length || storedKey[index] != (byte)character)
            {
                return false;
            }
        }

        if (isAscii)
        {
            return storedKey.Length == expectedCharacters.Length;
        }

        var byteCount = Encoding.UTF8.GetByteCount(expectedCharacters);
        if (byteCount != storedKey.Length)
        {
            return false;
        }

        byte[]? rented = null;
        Span<byte> encoded = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            _ = Encoding.UTF8.GetBytes(expectedCharacters, encoded);
            return storedKey.SequenceEqual(encoded);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    public static byte[] ReadPayload(ReadOnlySpan<byte> frame, FrameHeader header) =>
        frame.Slice(FrameHeaderSize + header.KeyLength, header.PayloadLength).ToArray();

    public static uint ComputeDataChecksum(string key, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var keyLength = Encoding.UTF8.GetByteCount(key);
        var buffer = ArrayPool<byte>.Shared.Rent(checked(keyLength + payload.Length));
        try
        {
            var data = buffer.AsSpan(0, keyLength + payload.Length);
            _ = Encoding.UTF8.GetBytes(key, data[..keyLength]);
            payload.CopyTo(data[keyLength..]);
            return Crc32C.Compute(data);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    public static string SegmentFileName(long segmentId) => $"segment-{segmentId:D20}.idbseg";

    public static bool TryParseSegmentId(string path, out long segmentId)
    {
        segmentId = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("segment-", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan("segment-".Length), out segmentId)
            && segmentId > 0;
    }

    private static uint ComputeHeaderChecksum(ReadOnlySpan<byte> header, int checksumOffset)
    {
        Span<byte> copy = stackalloc byte[SegmentHeaderSize];
        var target = copy[..header.Length];
        header.CopyTo(target);
        target.Slice(checksumOffset, sizeof(uint)).Clear();
        return Crc32C.Compute(target);
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    internal readonly record struct SegmentHeader(
        long SegmentId,
        long PreviousSegmentId,
        uint PreviousHeaderChecksum,
        uint HeaderChecksum);

    internal readonly record struct FrameHeader(
        ObjectLogRecordKind Kind,
        int FrameLength,
        int KeyLength,
        int PayloadLength,
        long Lsn,
        long Auxiliary,
        uint DataChecksum);

    internal sealed class PooledFrame : IDisposable
    {
        private byte[]? _buffer;

        public PooledFrame(byte[] buffer, int length)
        {
            _buffer = buffer;
            Length = length;
        }

        public int Length { get; }

        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(PooledFrame)))
            .AsMemory(0, Length);

        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }
        }
    }
}
