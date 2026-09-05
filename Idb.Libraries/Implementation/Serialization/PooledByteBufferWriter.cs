using System.Buffers;

namespace Idb.Libraries.Implementation.Serialization;

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1024);
    private int _written;

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        sizeHint = Math.Max(1, sizeHint);
        if (sizeHint <= _buffer.Length - _written)
        {
            return;
        }
        var replacement = ArrayPool<byte>.Shared.Rent(checked(_written + Math.Max(sizeHint, _buffer.Length)));
        _buffer.AsSpan(0, _written).CopyTo(replacement);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = replacement;
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
