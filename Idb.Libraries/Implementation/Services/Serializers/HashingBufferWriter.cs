using System;
using System.Buffers;
using System.IO.Hashing;

namespace Idb.Libraries.Implementation.Services.Serializers;

/// <summary>
/// Buffer writer that feeds appended bytes into an <see cref="XxHash64"/> accumulator.
/// </summary>
internal sealed class HashingBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultBufferSize = 4096;

    private byte[] _buffer;
    private int _position;
    private bool _disposed;
    private readonly XxHash64 _hash;

    public HashingBufferWriter(int initialSize = DefaultBufferSize)
    {
        if (initialSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialSize), initialSize, "Initial size must be positive.");
        }

        _buffer = ArrayPool<byte>.Shared.Rent(initialSize);
        _hash = new XxHash64();
    }

    /// <summary>
    /// Gets the hash of all bytes that have been advanced so far.
    /// </summary>
    public ulong Hash => _hash.GetCurrentHashAsUInt64();

    public void Advance(int count)
    {
        EnsureNotDisposed();

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (_position + count > _buffer.Length)
        {
            throw new InvalidOperationException("Cannot advance beyond the size of the rented buffer.");
        }

        _hash.Append(_buffer.AsSpan(_position, count));
        _position += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureNotDisposed();
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_position);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureNotDisposed();
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_position);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var buffer = _buffer;
        _buffer = Array.Empty<byte>();
        _position = 0;

        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        }

        if (sizeHint == 0)
        {
            sizeHint = DefaultBufferSize;
        }

        var required = _position + sizeHint;
        if (required <= _buffer.Length)
        {
            return;
        }

        var newSize = Math.Max(required, _buffer.Length * 2);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        Array.Copy(_buffer, 0, newBuffer, 0, _position);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = newBuffer;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HashingBufferWriter));
        }
    }
}
