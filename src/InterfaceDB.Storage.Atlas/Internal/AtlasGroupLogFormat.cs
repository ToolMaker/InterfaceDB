using System.Buffers.Binary;
using System.Buffers;
using System.Text;

namespace Idb.Storage.AtlasCore.Internal;

internal enum AtlasGroupRecordKind : byte
{
    Put = 1,
    Delete = 2
}

internal readonly record struct AtlasGroupAddress(
    AtlasGroupRecordKind Kind,
    long SegmentId,
    long Offset,
    int FrameLength,
    long Lsn,
    long PlacementGeneration);

internal readonly record struct AtlasGroupFrameHeader(
    AtlasGroupRecordKind Kind,
    int FrameLength,
    int KeyLength,
    int PayloadLength,
    long Lsn,
    long PlacementGeneration,
    uint DataChecksum,
    Guid TransactionId);

internal static class AtlasGroupLogFormat
{
    public const int SegmentHeaderBytes = 64;
    public const int FrameHeaderBytes = 64;
    public const int FrameFooterBytes = 24;
    public const int MaximumFrameBytes = 256 * 1024 * 1024;
    private const int Alignment = 8;
    private const int Version = 2;
    private const uint FrameMagic = 0x31464741; // AGF1
    private const uint FooterMagic = 0x31454741; // AGE1
    private const uint CommitMarker = 0xA71AC0DE;
    private static ReadOnlySpan<byte> SegmentMagic => "IDBAG002"u8;

    public static byte[] CreateSegmentHeader(long segmentId, long previousSegmentId)
    {
        var header = new byte[SegmentHeaderBytes];
        SegmentMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), SegmentHeaderBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), segmentId);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), previousSegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), HeaderChecksum(header, 40));
        return header;
    }

    public static void ValidateSegmentHeader(
        ReadOnlySpan<byte> header,
        long expectedSegmentId,
        long expectedPreviousSegmentId,
        string path)
    {
        if (header.Length != SegmentHeaderBytes
            || !header[..8].SequenceEqual(SegmentMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != SegmentHeaderBytes
            || BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != expectedSegmentId
            || BinaryPrimitives.ReadInt64LittleEndian(header[24..]) != expectedPreviousSegmentId
            || BinaryPrimitives.ReadUInt32LittleEndian(header[40..]) != HeaderChecksum(header, 40))
        {
            throw new InvalidDataException($"AtlasCore segment '{path}' has an invalid chain header.");
        }
    }

    public static AtlasGroupPooledFrame CreateFrame(
        AtlasGroupRecordKind kind,
        long lsn,
        long placementGeneration,
        string key,
        ReadOnlySpan<byte> payload,
        Guid transactionId = default)
    {
        var frameLength = CalculateFrameLength(kind, lsn, placementGeneration, key, payload.Length);
        var owner = new AtlasGroupPooledFrame(ArrayPool<byte>.Shared.Rent(frameLength), frameLength);
        WriteFrame(owner.Span, kind, lsn, placementGeneration, key, payload, transactionId);
        return owner;
    }

    internal static int CalculateFrameLength(
        AtlasGroupRecordKind kind,
        long lsn,
        long placementGeneration,
        string key,
        int payloadLength)
    {
        if (!Enum.IsDefined(kind) || lsn <= 0 || placementGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (payloadLength < 0 || (kind == AtlasGroupRecordKind.Delete && payloadLength != 0))
        {
            throw new ArgumentException("An AtlasCore tombstone cannot contain a payload.", nameof(payloadLength));
        }

        var keyLength = Encoding.UTF8.GetByteCount(key);
        var unaligned = checked(FrameHeaderBytes + keyLength + payloadLength + FrameFooterBytes);
        var frameLength = Align(unaligned);
        if (frameLength > MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength), "The AtlasCore record exceeds the maximum frame size.");
        }

        return frameLength;
    }

    internal static void WriteFrame(
        Span<byte> frame,
        AtlasGroupRecordKind kind,
        long lsn,
        long placementGeneration,
        string key,
        ReadOnlySpan<byte> payload,
        Guid transactionId)
    {
        var frameLength = frame.Length;
        var keyLength = Encoding.UTF8.GetByteCount(key);
        frame[..FrameHeaderBytes].Clear();
        frame.Slice(
                FrameHeaderBytes + keyLength + payload.Length,
                frameLength - FrameHeaderBytes - keyLength - payload.Length - FrameFooterBytes)
            .Clear();
        var data = frame.Slice(FrameHeaderBytes, keyLength + payload.Length);
        _ = Encoding.UTF8.GetBytes(key, data[..keyLength]);
        payload.CopyTo(data[keyLength..]);
        var dataChecksum = AtlasCrc32C.Compute(data);

        BinaryPrimitives.WriteUInt32LittleEndian(frame, FrameMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], Version);
        frame[6] = (byte)kind;
        frame[7] = transactionId == Guid.Empty ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32LittleEndian(frame[8..], frameLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame[12..], keyLength);
        BinaryPrimitives.WriteInt32LittleEndian(frame[16..], payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(frame[24..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(frame[32..], placementGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[40..], dataChecksum);
        _ = transactionId.TryWriteBytes(frame.Slice(48, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], HeaderChecksum(frame[..FrameHeaderBytes], 20));

        var footer = frame[(frameLength - FrameFooterBytes)..];
        BinaryPrimitives.WriteUInt32LittleEndian(footer, FooterMagic);
        BinaryPrimitives.WriteInt32LittleEndian(footer[4..], frameLength);
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..], lsn);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], dataChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[20..], CommitMarker);
    }

    public static AtlasGroupFrameHeader ParseHeader(ReadOnlySpan<byte> header, string path, long offset)
    {
        if (header.Length != FrameHeaderBytes
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != FrameMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != Version
            || header[7] > 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != HeaderChecksum(header, 20))
        {
            throw new InvalidDataException($"AtlasCore segment '{path}' has an invalid frame header at {offset:N0}.");
        }

        var kind = (AtlasGroupRecordKind)header[6];
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var keyLength = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var minimum = (long)FrameHeaderBytes + keyLength + payloadLength + FrameFooterBytes;
        var lsn = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        var placementGeneration = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        var transactionId = new Guid(header.Slice(48, 16));
        if (!Enum.IsDefined(kind)
            || frameLength < minimum
            || frameLength > MaximumFrameBytes
            || (frameLength & (Alignment - 1)) != 0
            || keyLength <= 0
            || payloadLength < 0
            || lsn <= 0
            || placementGeneration <= 0
            || (header[7] == 0) != (transactionId == Guid.Empty)
            || (kind == AtlasGroupRecordKind.Delete && payloadLength != 0))
        {
            throw new InvalidDataException($"AtlasCore segment '{path}' has invalid frame fields at {offset:N0}.");
        }

        return new AtlasGroupFrameHeader(
            kind,
            frameLength,
            keyLength,
            payloadLength,
            lsn,
            placementGeneration,
            BinaryPrimitives.ReadUInt32LittleEndian(header[40..]),
            transactionId);
    }

    public static void ValidateFrame(
        ReadOnlySpan<byte> frame,
        AtlasGroupFrameHeader header,
        string path,
        long offset)
    {
        if (frame.Length != header.FrameLength)
        {
            throw new InvalidDataException($"AtlasCore segment '{path}' ends inside a frame at {offset:N0}.");
        }

        var data = frame.Slice(FrameHeaderBytes, header.KeyLength + header.PayloadLength);
        var footer = frame[^FrameFooterBytes..];
        if (AtlasCrc32C.Compute(data) != header.DataChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer) != FooterMagic
            || BinaryPrimitives.ReadInt32LittleEndian(footer[4..]) != header.FrameLength
            || BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) != header.Lsn
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != header.DataChecksum
            || BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]) != CommitMarker)
        {
            throw new InvalidDataException($"AtlasCore segment '{path}' has an invalid frame checksum at {offset:N0}.");
        }
    }

    public static string ReadKey(ReadOnlySpan<byte> frame, AtlasGroupFrameHeader header) =>
        Encoding.UTF8.GetString(frame.Slice(FrameHeaderBytes, header.KeyLength));

    public static bool KeyEquals(ReadOnlySpan<byte> frame, AtlasGroupFrameHeader header, string expected)
    {
        var stored = frame.Slice(FrameHeaderBytes, header.KeyLength);
        var characters = expected.AsSpan();
        var ascii = true;
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            if (character > 0x7F)
            {
                ascii = false;
                break;
            }

            if (index >= stored.Length || stored[index] != (byte)character)
            {
                return false;
            }
        }

        if (ascii)
        {
            return stored.Length == characters.Length;
        }

        var byteCount = Encoding.UTF8.GetByteCount(characters);
        if (byteCount != stored.Length)
        {
            return false;
        }

        byte[]? rented = null;
        Span<byte> encoded = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            _ = Encoding.UTF8.GetBytes(characters, encoded);
            return stored.SequenceEqual(encoded);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }
    }

    public static byte[] ReadPayload(ReadOnlySpan<byte> frame, AtlasGroupFrameHeader header) =>
        frame.Slice(FrameHeaderBytes + header.KeyLength, header.PayloadLength).ToArray();

    public static string SegmentFileName(long segmentId) => $"segment-{segmentId:D20}.idbag";

    public static bool TryParseSegmentId(string path, out long segmentId)
    {
        segmentId = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("segment-", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan("segment-".Length), out segmentId)
            && segmentId > 0;
    }

    private static uint HeaderChecksum(ReadOnlySpan<byte> bytes, int checksumOffset)
    {
        Span<byte> copy = stackalloc byte[SegmentHeaderBytes];
        var target = copy[..bytes.Length];
        bytes.CopyTo(target);
        target.Slice(checksumOffset, sizeof(uint)).Clear();
        return AtlasCrc32C.Compute(target);
    }

    private static int Align(int value) => checked((value + Alignment - 1) & ~(Alignment - 1));
}

internal sealed class AtlasGroupPooledFrame : IDisposable
{
    private byte[]? _buffer;

    public AtlasGroupPooledFrame(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public Span<byte> Span => (_buffer ?? throw new ObjectDisposedException(nameof(AtlasGroupPooledFrame)))
        .AsSpan(0, Length);

    public ReadOnlySpan<byte> ReadOnlySpan => Span;

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }
}
