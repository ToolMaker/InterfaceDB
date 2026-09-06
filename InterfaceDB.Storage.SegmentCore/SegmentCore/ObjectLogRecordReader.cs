using System.Buffers;
using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

internal sealed class ObjectLogRecordReader : IDisposable
{
    private readonly string _rootDirectory;
    private readonly SegmentCoreReadOptions _options;
    private readonly ConcurrentDictionary<SegmentKey, Lazy<SegmentReadHandle>> _asynchronousHandles = new();
    private readonly ConcurrentDictionary<SegmentKey, Lazy<SegmentReadHandle>> _synchronousHandles = new();
    private int _disposed;

    public ObjectLogRecordReader(string rootDirectory, SegmentCoreReadOptions options)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public ValueTask<byte[]> ReadPayloadAsync(
        string expectedKey,
        StripedObjectLogRecordAddress locator,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKey);
        var address = locator.Address;
        if (locator.Stripe < 0
            || address.SegmentId is 0 or long.MinValue
            || address.Offset < ObjectLogFormat.SegmentHeaderSize
            || address.FrameLength < ObjectLogFormat.FrameHeaderSize + ObjectLogFormat.FrameFooterSize
            || address.FrameLength > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ObjectLogCorruptionException($"Primary index contains an invalid locator for '{expectedKey}'.");
        }

        if (_options.SynchronousPointReadMaximumBytes > 0
            && address.FrameLength <= _options.SynchronousPointReadMaximumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = GetSynchronousHandle(locator.Stripe, address.SegmentId);
            var rented = ArrayPool<byte>.Shared.Rent(address.FrameLength);
            try
            {
                var frame = rented.AsSpan(0, address.FrameLength);
                ReadExactly(segment.Handle, frame, address.Offset, segment.Path, cancellationToken);
                return ValueTask.FromResult(ReadValidatedPayload(expectedKey, address, frame, segment.Path));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }

        var asynchronousSegment = GetAsynchronousHandle(locator.Stripe, address.SegmentId);
        return ReadPayloadAsynchronouslyAsync(
            expectedKey,
            address,
            asynchronousSegment.Handle,
            asynchronousSegment.Path,
            cancellationToken);
    }

    internal async ValueTask<byte[]> ReadPayloadBudgetedAsync(string key, StripedObjectLogRecordAddress locator,
        IMaintenanceBudget budget, CancellationToken token)
    {
        ValidateLocator(key, locator);
        var segment = GetAsynchronousHandle(locator.Stripe, locator.Address.SegmentId);
        var rented = ArrayPool<byte>.Shared.Rent(locator.Address.FrameLength);
        try
        {
            var chunkSize = (int)Math.Min(64 * 1024, budget.MaximumWorkUnitBytes);
            for (var offset = 0; offset < locator.Address.FrameLength; offset += chunkSize)
            {
                var count = Math.Min(chunkSize, locator.Address.FrameLength - offset);
                using var admission = await budget.AcquireAsync(count, token).ConfigureAwait(false);
                await ReadExactlyAsync(segment.Handle, rented.AsMemory(offset, count), locator.Address.Offset + offset,
                    segment.Path, token).ConfigureAwait(false);
            }
            return ReadValidatedPayload(key, locator.Address, rented.AsSpan(0, locator.Address.FrameLength), segment.Path);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static async ValueTask<byte[]> ReadPayloadAsynchronouslyAsync(
        string expectedKey,
        ObjectLogRecordAddress address,
        SafeFileHandle handle,
        string path,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(address.FrameLength);
        try
        {
            var frame = rented.AsMemory(0, address.FrameLength);
            await ReadExactlyAsync(handle, frame, address.Offset, path, cancellationToken).ConfigureAwait(false);
            return ReadValidatedPayload(expectedKey, address, frame.Span, path);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }

    public async ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadManyAsync(
        IReadOnlyList<KeyValuePair<string, StripedObjectLogRecordAddress>> locators,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(locators);
        if (locators.Count == 0)
        {
            return [];
        }

        var ordered = new PhysicalRead[locators.Count];
        for (var i = 0; i < ordered.Length; i++) ordered[i] = new(locators[i].Key, locators[i].Value);
        Array.Sort(ordered, static (left, right) =>
        {
            var stripe = left.Locator.Stripe.CompareTo(right.Locator.Stripe);
            if (stripe != 0) return stripe;
            var segment = left.Locator.Address.SegmentId.CompareTo(right.Locator.Address.SegmentId);
            return segment != 0 ? segment : left.Locator.Address.Offset.CompareTo(right.Locator.Address.Offset);
        });
        var results = new SegmentCoreRecord[ordered.Length];
        var groups = new List<(int Start, int End)>();
        for (var start = 0; start < ordered.Length;)
        {
            var end = start + 1;
            while (end < ordered.Length && ordered[end].Locator.Stripe == ordered[start].Locator.Stripe
                && ordered[end].Locator.Address.SegmentId == ordered[start].Locator.Address.SegmentId) end++;
            groups.Add((start, end));
            start = end;
        }
        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(
                    _options.MaximumParallelReads,
                    Math.Max(1, Environment.ProcessorCount))
            },
            async (group, token) =>
            {
                var locator = ordered[group.Start].Locator;
                var segment = GetAsynchronousHandle(locator.Stripe, locator.Address.SegmentId);
                var path = segment.Path;
                var handle = segment.Handle;
                var cursor = group.Start;
                while (cursor < group.End)
                {
                    token.ThrowIfCancellationRequested();
                    var first = ordered[cursor];
                    ValidateLocator(first.Key, first.Locator);
                    var windowStart = first.Locator.Address.Offset;
                    var windowEnd = checked(windowStart + first.Locator.Address.FrameLength);
                    var end = cursor + 1;
                    while (end < group.End)
                    {
                        var candidate = ordered[end];
                        ValidateLocator(candidate.Key, candidate.Locator);
                        var candidateStart = candidate.Locator.Address.Offset;
                        var candidateEnd = checked(candidateStart + candidate.Locator.Address.FrameLength);
                        if (candidateStart - windowEnd > _options.MaximumReadGapBytes
                            || candidateEnd - windowStart > _options.MaximumReadWindowBytes)
                        {
                            break;
                        }

                        windowEnd = Math.Max(windowEnd, candidateEnd);
                        end++;
                    }

                    var windowLength = checked((int)(windowEnd - windowStart));
                    var rented = ArrayPool<byte>.Shared.Rent(windowLength);
                    try
                    {
                        var window = rented.AsMemory(0, windowLength);
                        await ReadExactlyAsync(handle, window, windowStart, path, token).ConfigureAwait(false);
                        for (var index = cursor; index < end; index++)
                        {
                            var item = ordered[index];
                            var address = item.Locator.Address;
                            var relativeOffset = checked((int)(address.Offset - windowStart));
                            var frame = window.Span.Slice(relativeOffset, address.FrameLength);
                            results[index] = new SegmentCoreRecord(
                                item.Key,
                                ReadValidatedPayload(item.Key, address, frame, path));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                    }

                    cursor = end;
                }
            }).ConfigureAwait(false);
        return results;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeHandles(_asynchronousHandles);
        DisposeHandles(_synchronousHandles);
    }

    private static void DisposeHandles(
        ConcurrentDictionary<SegmentKey, Lazy<SegmentReadHandle>> handles)
    {
        foreach (var handle in handles.Values)
        {
            if (handle.IsValueCreated)
            {
                handle.Value.Handle.Dispose();
            }
        }

        handles.Clear();
    }

    private SegmentReadHandle GetAsynchronousHandle(int stripe, long segmentId)
    {
        var lazy = _asynchronousHandles.GetOrAdd(
            new SegmentKey(stripe, segmentId),
            static (key, root) => new Lazy<SegmentReadHandle>(
                () => OpenSegment(root, key, FileOptions.Asynchronous | FileOptions.RandomAccess),
                LazyThreadSafetyMode.ExecutionAndPublication),
            _rootDirectory);
        return lazy.Value;
    }

    private SegmentReadHandle GetSynchronousHandle(int stripe, long segmentId)
    {
        var lazy = _synchronousHandles.GetOrAdd(
            new SegmentKey(stripe, segmentId),
            static (key, root) => new Lazy<SegmentReadHandle>(
                () => OpenSegment(root, key, FileOptions.RandomAccess),
                LazyThreadSafetyMode.ExecutionAndPublication),
            _rootDirectory);
        return lazy.Value;
    }

    private static SegmentReadHandle OpenSegment(string rootDirectory, SegmentKey key, FileOptions options)
    {
        var path = SegmentCoreIndependentFiles.Resolve(rootDirectory, key.Stripe, key.SegmentId);
        return new SegmentReadHandle(
            path,
            File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                options));
    }

    private static void ValidateLocator(string expectedKey, StripedObjectLogRecordAddress locator)
    {
        var address = locator.Address;
        if (locator.Stripe < 0
            || address.SegmentId is 0 or long.MinValue
            || address.Offset < ObjectLogFormat.SegmentHeaderSize
            || address.FrameLength < ObjectLogFormat.FrameHeaderSize + ObjectLogFormat.FrameFooterSize
            || address.FrameLength > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ObjectLogCorruptionException($"Primary index contains an invalid locator for '{expectedKey}'.");
        }
    }

    private static byte[] ReadValidatedPayload(
        string expectedKey,
        ObjectLogRecordAddress address,
        ReadOnlySpan<byte> frame,
        string path)
    {
        var header = ObjectLogFormat.ParseFrameHeader(
            frame[..ObjectLogFormat.FrameHeaderSize],
            path,
            address.Offset);
        ObjectLogFormat.ValidateFrame(frame, header, path, address.Offset);
        if (header.Kind != ObjectLogRecordKind.Put
            || header.Lsn != address.Lsn
            || header.FrameLength != address.FrameLength
            || !ObjectLogFormat.KeyEquals(frame, header, expectedKey))
        {
            throw new ObjectLogCorruptionException(
                $"Primary-index locator for '{expectedKey}' points to a different object-log record.");
        }

        return ObjectLogFormat.ReadPayload(frame, header);
    }

    private static async ValueTask ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long offset,
        string path,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                destination[totalRead..],
                offset + totalRead,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Unexpected end of segment '{path}' at offset {offset + totalRead:N0}.");
            }

            totalRead += read;
        }
    }

    private static void ReadExactly(
        SafeFileHandle handle,
        Span<byte> destination,
        long offset,
        string path,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(handle, destination[totalRead..], offset + totalRead);
            if (read == 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Unexpected end of segment '{path}' at offset {offset + totalRead:N0}.");
            }

            totalRead += read;
        }
    }

    private readonly record struct SegmentKey(int Stripe, long SegmentId);

    private sealed record SegmentReadHandle(string Path, SafeFileHandle Handle);

    private readonly record struct PhysicalRead(string Key, StripedObjectLogRecordAddress Locator);

}
