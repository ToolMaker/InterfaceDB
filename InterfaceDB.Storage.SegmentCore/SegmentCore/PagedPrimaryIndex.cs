using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

internal readonly record struct PagedPrimaryIndexReference(
    ulong Hash,
    long Offset,
    int Length,
    uint Checksum);

internal interface IPagedPrimaryIndexView
{
    bool IsPaged { get; }
}

/// <summary>
/// Keeps only compact checkpoint references resident. Keys and locators are validated and decoded
/// on demand from the immutable checkpoint, avoiding one managed string and hash-table node per key.
/// </summary>
internal sealed class PagedPrimaryIndex :
    IReadOnlyDictionary<string, StripedObjectLogRecordAddress>,
    IPagedPrimaryIndexView,
    IDisposable
{
    private const int EntryFixedBytes = 36;
    private const int MaximumKeyBytes = 1024 * 1024;
    private readonly string _path;
    private readonly SafeFileHandle _handle;
    private readonly ulong[] _hashes;
    private readonly long[] _offsets;
    private readonly int[] _lengths;
    private readonly uint[] _checksums;
    private readonly long _dataOffset;
    private readonly int _stripeCount;
    private int _disposed;

    public PagedPrimaryIndex(
        string path,
        long dataOffset,
        int stripeCount,
        ulong[] hashes,
        long[] offsets,
        int[] lengths,
        uint[] checksums)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(checksums);
        if (offsets.Length != hashes.Length
            || lengths.Length != hashes.Length
            || checksums.Length != hashes.Length)
        {
            throw new ArgumentException("Paged primary-index reference arrays must have equal lengths.");
        }

        _path = path;
        _dataOffset = dataOffset;
        _stripeCount = stripeCount;
        _hashes = hashes;
        _offsets = offsets;
        _lengths = lengths;
        _checksums = checksums;

        _handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
    }

    public int Count => _hashes.Length;

    public bool IsPaged => true;

    public IEnumerable<string> Keys => this.Select(static pair => pair.Key);

    public IEnumerable<StripedObjectLogRecordAddress> Values => this.Select(static pair => pair.Value);

    public StripedObjectLogRecordAddress this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out StripedObjectLogRecordAddress value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var byteCount = Encoding.UTF8.GetByteCount(key);
        byte[]? rentedKey = null;
        Span<byte> keyBytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rentedKey = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            _ = Encoding.UTF8.GetBytes(key, keyBytes);
            var hash = PrimaryIndexKeyHash.Compute(keyBytes);
            var index = LowerBound(_hashes, hash);
            while (index < _hashes.Length && _hashes[index] == hash)
            {
                if (TryReadReference(index, key, keyBytes, out value))
                {
                    return true;
                }

                index++;
            }
        }
        finally
        {
            if (rentedKey is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedKey, clearArray: false);
            }
        }

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<string, StripedObjectLogRecordAddress>> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Publication may unlink this checkpoint while the index still owns its open
        // handle. Each enumerator needs its own position, without reopening the path.
        using var stream = new BufferedStream(new HandleReadStream(_handle, _dataOffset), 64 * 1024);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        for (var index = 0; index < Count; index++)
        {
            var keyLength = reader.ReadInt32();
            if (keyLength is <= 0 or > MaximumKeyBytes)
            {
                throw new ObjectLogCorruptionException(
                    $"Paged primary-index checkpoint '{_path}' contains an invalid key length.");
            }

            var keyBytes = reader.ReadBytes(keyLength);
            if (keyBytes.Length != keyLength)
            {
                throw new ObjectLogCorruptionException(
                    $"Paged primary-index checkpoint '{_path}' is truncated.");
            }

            var key = Encoding.UTF8.GetString(keyBytes);
            var locator = ReadLocator(reader, key);
            yield return new KeyValuePair<string, StripedObjectLogRecordAddress>(key, locator);
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class HandleReadStream(SafeFileHandle handle, long offset) : Stream
    {
        private long _position = offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => RandomAccess.GetLength(handle);
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var read = RandomAccess.Read(handle, buffer, _position);
            _position += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // The index, not an individual enumerator, owns the handle.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    private bool TryReadReference(
        int index,
        string expectedKeyText,
        ReadOnlySpan<byte> expectedKey,
        out StripedObjectLogRecordAddress value)
    {
        var length = _lengths[index];
        if (length < EntryFixedBytes + 1 || length > EntryFixedBytes + MaximumKeyBytes)
        {
            throw InvalidReference(index);
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var entry = rented.AsSpan(0, length);
            ReadExactly(_handle, entry, _offsets[index]);
            if (Crc32C.Compute(entry) != _checksums[index])
            {
                throw new ObjectLogCorruptionException(
                    $"Paged primary-index checkpoint '{_path}' entry {index:N0} failed its checksum.");
            }

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(entry);
            if (keyLength <= 0 || keyLength != length - EntryFixedBytes)
            {
                throw InvalidReference(index);
            }

            if (!entry.Slice(sizeof(int), keyLength).SequenceEqual(expectedKey))
            {
                value = default;
                return false;
            }

            var locatorBytes = entry[(sizeof(int) + keyLength)..];
            value = ParseLocator(locatorBytes, expectedKeyText);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    private StripedObjectLogRecordAddress ReadLocator(BinaryReader reader, string key)
    {
        var stripe = reader.ReadInt32();
        var address = new ObjectLogRecordAddress(
            reader.ReadInt64(),
            reader.ReadInt64(),
            reader.ReadInt32(),
            reader.ReadInt64());
        return ValidateLocator(stripe, address, key);
    }

    private StripedObjectLogRecordAddress ParseLocator(ReadOnlySpan<byte> bytes, string key)
    {
        if (bytes.Length != EntryFixedBytes - sizeof(int))
        {
            throw new ObjectLogCorruptionException(
                $"Paged primary-index checkpoint '{_path}' has an invalid locator for '{key}'.");
        }

        return ValidateLocator(
            BinaryPrimitives.ReadInt32LittleEndian(bytes),
            new ObjectLogRecordAddress(
                BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[24..])),
            key);
    }

    private StripedObjectLogRecordAddress ValidateLocator(
        int stripe,
        ObjectLogRecordAddress address,
        string key)
    {
        if (stripe < 0
            || stripe >= _stripeCount
            || address.SegmentId is 0 or long.MinValue
            || address.Offset < ObjectLogFormat.SegmentHeaderSize
            || address.FrameLength < ObjectLogFormat.FrameHeaderSize + ObjectLogFormat.FrameFooterSize
            || address.FrameLength > ObjectLogFormat.MaximumFrameBytes
            || address.Lsn <= 0)
        {
            throw new ObjectLogCorruptionException(
                $"Paged primary-index checkpoint '{_path}' has an invalid locator for '{key}'.");
        }

        return new StripedObjectLogRecordAddress(stripe, address);
    }

    private ObjectLogCorruptionException InvalidReference(int index) => new(
        $"Paged primary-index checkpoint '{_path}' contains invalid reference {index:N0}.");

    private static int LowerBound(ulong[] values, ulong target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (values[middle] < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = RandomAccess.Read(handle, destination[read..], offset + read);
            if (count == 0)
            {
                throw new EndOfStreamException("The paged primary-index checkpoint ended unexpectedly.");
            }

            read += count;
        }
    }
}

internal static class PrimaryIndexKeyHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(ReadOnlySpan<byte> value)
    {
        var hash = OffsetBasis;
        foreach (var item in value)
        {
            hash ^= item;
            hash *= Prime;
        }

        return hash;
    }
}
