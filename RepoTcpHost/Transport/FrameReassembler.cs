using System.Buffers.Binary;

namespace RepoTcpHost.Transport;

public sealed class FrameReassembler
{
    private byte[] _buffer = Array.Empty<byte>();
    private int _length;

    public IReadOnlyList<ReadOnlyMemory<byte>> Feed(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        var frames = new List<ReadOnlyMemory<byte>>();
        var offset = 0;
        while (offset + sizeof(uint) <= _length)
        {
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(offset, sizeof(uint)));
            if (_length - offset - sizeof(uint) < expected)
            {
                break;
            }

            var frame = new byte[expected];
            Buffer.BlockCopy(_buffer, offset + sizeof(uint), frame, 0, (int)expected);
            frames.Add(frame);
            offset += sizeof(uint) + (int)expected;
        }

        if (offset > 0)
        {
            if (offset < _length)
            {
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, _length - offset);
            }
            _length -= offset;
        }

        return frames;
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
        {
            return;
        }

        var newSize = Math.Max(required, _buffer.Length == 0 ? 4096 : _buffer.Length * 2);
        Array.Resize(ref _buffer, newSize);
    }
}
