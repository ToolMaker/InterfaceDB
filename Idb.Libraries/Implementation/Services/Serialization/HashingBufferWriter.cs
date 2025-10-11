using System;
using System.Buffers;
using System.IO.Hashing;

namespace Idb.Libraries.Implementation.Services.Serialization;

internal sealed class HashingBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer;
    private readonly XxHash64 _hash;

    public HashingBufferWriter(int initialCapacity = 256)
    {
        _buffer = new ArrayBufferWriter<byte>(initialCapacity);
        _hash = new XxHash64();
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.WrittenMemory;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;
    public int WrittenCount => _buffer.WrittenCount;
    public ulong Hash => _hash.GetCurrentHashAsUInt64();

    public void Advance(int count)
    {
        var previousCount = _buffer.WrittenCount;
        _buffer.Advance(count);
        var written = _buffer.WrittenSpan;
        var appended = written.Slice(previousCount, count);
        _hash.Append(appended);
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

    public void Dispose()
    {
        // no resources to dispose
    }
}

