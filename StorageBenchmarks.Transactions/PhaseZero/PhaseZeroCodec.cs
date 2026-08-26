using System.Buffers;
using System.Buffers.Binary;
using System.Text;

internal static class PhaseZeroCodec
{
    private const int Version = 1;

    public static byte[] Encode(GateRecord record)
    {
        var writer = new ArrayBufferWriter<byte>(record.Content.Length + 128);
        WriteInt32(writer, Version);
        WriteString(writer, record.Id);
        WriteString(writer, record.Name);
        WriteInt32(writer, record.Age);
        WriteString(writer, record.Role);
        WriteString(writer, record.Content);
        WriteInt64(writer, record.Revision);
        return writer.WrittenSpan.ToArray();
    }

    public static GateRecord Decode(ReadOnlySpan<byte> source)
    {
        var offset = 0;
        var version = ReadInt32(source, ref offset);
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported Phase 0 payload version {version}.");
        }

        var id = ReadString(source, ref offset);
        var name = ReadString(source, ref offset);
        var age = ReadInt32(source, ref offset);
        var role = ReadString(source, ref offset);
        var content = ReadString(source, ref offset);
        var revision = ReadInt64(source, ref offset);
        if (offset != source.Length)
        {
            throw new InvalidDataException("Phase 0 payload has trailing bytes.");
        }

        return new GateRecord(id, name, age, role, content, revision);
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(writer, byteCount);
        var target = writer.GetSpan(byteCount)[..byteCount];
        _ = Encoding.UTF8.GetBytes(value, target);
        writer.Advance(byteCount);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        var target = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(target, value);
        writer.Advance(sizeof(int));
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        var target = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(target, value);
        writer.Advance(sizeof(long));
    }

    private static string ReadString(ReadOnlySpan<byte> source, ref int offset)
    {
        var length = ReadInt32(source, ref offset);
        if (length < 0 || length > source.Length - offset)
        {
            throw new InvalidDataException("Phase 0 payload contains an invalid string length.");
        }

        var value = Encoding.UTF8.GetString(source.Slice(offset, length));
        offset += length;
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureAvailable(source, offset, sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += sizeof(int);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureAvailable(source, offset, sizeof(long));
        var value = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);
        return value;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (offset < 0 || length > source.Length - offset)
        {
            throw new InvalidDataException("Phase 0 payload is truncated.");
        }
    }
}
