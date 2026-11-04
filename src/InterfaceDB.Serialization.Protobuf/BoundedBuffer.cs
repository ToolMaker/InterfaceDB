namespace Idb.Serialization.Protobuf;

internal sealed class BoundedBuffer(int maximumBytes) : MemoryStream
{
    private void Check(long end)
    {
        if (end > maximumBytes) throw new InvalidDataException("The Protobuf envelope exceeds its configured byte limit.");
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(Position + count);
        base.Write(buffer, offset, count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(Position + buffer.Length);
        base.Write(buffer);
    }
    public override void WriteByte(byte value)
    {
        Check(Position + 1);
        base.WriteByte(value);
    }
    public override void SetLength(long value)
    {
        Check(value);
        base.SetLength(value);
    }
}
