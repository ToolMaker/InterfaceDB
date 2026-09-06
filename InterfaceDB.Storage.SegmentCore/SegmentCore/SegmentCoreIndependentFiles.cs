using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore;

/// <summary>Negative locator segment ids address immutable checkpoint-owned output, never normal
/// append/replay files. Frame bytes and source LSN are preserved. The format manifest gates old readers.</summary>
internal static class SegmentCoreIndependentFiles
{
    internal const string FloorFileName = "compaction-checkpoint-floor.manifest";
    internal static string DirectoryFor(string root, int stripe) => Path.Combine(
        StripedObjectLogRecovery.StripeRoot(root, stripe), "compacted");

    internal static string Resolve(string root, int stripe, long id)
    {
        if (stripe is < 0 or > 63 || id is 0 or long.MinValue) throw new ObjectLogCorruptionException("Invalid segment identity.");
        return id > 0
            ? Path.Combine(StripedObjectLogRecovery.StripeRoot(root, stripe), "segments", ObjectLogFormat.SegmentFileName(id))
            : Path.Combine(DirectoryFor(root, stripe), ObjectLogFormat.SegmentFileName(-id));
    }

    internal static void VerifyRecovery(string root, PrimaryIndexCheckpointState? checkpoint)
    {
        var path = Path.Combine(root, FloorFileName);
        if (!File.Exists(path))
        {
            // Even an interrupted first compaction must not let a full-log scan invent a current
            // state after all checkpoint roots have been lost. Originals may still allow manual repair.
            if (checkpoint is null && Directory.Exists(Path.Combine(root, "stripes"))
                && Directory.EnumerateDirectories(Path.Combine(root, "stripes"))
                    .Any(stripe => Directory.Exists(Path.Combine(stripe, "compacted"))
                        && Directory.EnumerateFiles(Path.Combine(stripe, "compacted"), "segment-*.idbseg").Any()))
                throw new ObjectLogCorruptionException("Independent compaction output exists but no valid checkpoint remains. Restore a verified backup.");
            return;
        }
        Span<byte> bytes = stackalloc byte[32];
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (RandomAccess.GetLength(handle) != bytes.Length || RandomAccess.Read(handle, bytes, 0) != bytes.Length
            || !bytes[..8].SequenceEqual("IDBCMP01"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != Crc32C.Compute(bytes[..28]))
            throw new ObjectLogCorruptionException("Independent compaction recovery floor is corrupt.");
        var floor = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        if (floor <= 0 || checkpoint is null || checkpoint.Generation < floor)
            throw new ObjectLogCorruptionException("No checkpoint at or beyond the compaction recovery floor remains. Restore a verified backup.");
    }

    internal static void PublishRecoveryFloor(string root, long generation, IDurableFileSystem fs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        var path = Path.Combine(root, FloorFileName);
        var temporary = path + ".creating-" + Guid.NewGuid().ToString("N");
        var bytes = new byte[32]; "IDBCMP01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc32C.Compute(bytes.AsSpan(0, 28)));
        try
        {
            using (var handle = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.WriteThrough))
            { RandomAccess.Write(handle, bytes, 0); fs.FlushFile(handle); }
            if (File.Exists(path)) fs.ReplaceFile(temporary, path); else fs.PublishFile(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async ValueTask<long> CopyFrameAsync(string sourcePath, SafeFileHandle source,
        SafeFileHandle destination, long destinationOffset, string key, ObjectLogRecordAddress address,
        int bufferBytes, IMaintenanceBudget? budget, CancellationToken token)
    {
        var headerBytes = new byte[ObjectLogFormat.FrameHeaderSize];
        using (budget is null ? null : await budget.AcquireAsync(headerBytes.Length, token).ConfigureAwait(false))
            await ReadExactlyAsync(source, headerBytes, address.Offset, token).ConfigureAwait(false);
        var header = ObjectLogFormat.ParseFrameHeader(headerBytes, sourcePath, address.Offset);
        var expectedKey = Encoding.UTF8.GetBytes(key);
        if (header.Kind != ObjectLogRecordKind.Put || header.Lsn != address.Lsn
            || header.FrameLength != address.FrameLength || header.KeyLength != expectedKey.Length)
            throw new ObjectLogCorruptionException("Compaction source does not match its captured physical identity.");
        var chunkSize = (int)Math.Min(bufferBytes, budget?.MaximumWorkUnitBytes / 2 ?? bufferBytes);
        if (chunkSize < ObjectLogFormat.FrameHeaderSize) throw new ArgumentException("Copy I/O budget is too small.");
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var footer = new byte[ObjectLogFormat.FrameFooterSize];
        var crc = Crc32C.InitialState;
        var dataEnd = checked(ObjectLogFormat.FrameHeaderSize + header.KeyLength + header.PayloadLength);
        var footerStart = header.FrameLength - footer.Length;
        try
        {
            for (var offset = 0; offset < header.FrameLength; offset += chunkSize)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkSize, header.FrameLength - offset);
                using var admission = budget is null ? null : await budget.AcquireAsync(2L * count, token).ConfigureAwait(false);
                var chunk = buffer.AsMemory(0, count);
                await ReadExactlyAsync(source, chunk, address.Offset + offset, token).ConfigureAwait(false);
                if (offset == 0 && !chunk.Span[..ObjectLogFormat.FrameHeaderSize].SequenceEqual(headerBytes))
                    throw new ObjectLogCorruptionException("Compaction source changed while copying a sealed record.");
                var keyStart = Math.Max(offset, ObjectLogFormat.FrameHeaderSize);
                var keyEnd = Math.Min(offset + count, ObjectLogFormat.FrameHeaderSize + header.KeyLength);
                if (keyEnd > keyStart && !chunk.Span.Slice(keyStart - offset, keyEnd - keyStart)
                    .SequenceEqual(expectedKey.AsSpan(keyStart - ObjectLogFormat.FrameHeaderSize, keyEnd - keyStart)))
                    throw new ObjectLogCorruptionException("Compaction source key does not match its checkpoint locator.");
                var dataStart = Math.Max(offset, ObjectLogFormat.FrameHeaderSize);
                var end = Math.Min(offset + count, dataEnd);
                if (end > dataStart) crc = Crc32C.Append(crc, chunk.Span.Slice(dataStart - offset, end - dataStart));
                var startFooter = Math.Max(offset, footerStart);
                if (offset + count > startFooter)
                    chunk.Span.Slice(startFooter - offset, offset + count - startFooter)
                        .CopyTo(footer.AsSpan(startFooter - footerStart));
                await RandomAccess.WriteAsync(destination, chunk, destinationOffset + offset, token).ConfigureAwait(false);
            }
            if (Crc32C.Complete(crc) != header.DataChecksum
                || BinaryPrimitives.ReadUInt32LittleEndian(footer) != ObjectLogFormat.FooterMagic
                || BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(4)) != header.FrameLength
                || BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(8)) != header.Lsn
                || BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16)) != header.DataChecksum
                || BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(20)) != ObjectLogFormat.CommitMarker)
                throw new ObjectLogCorruptionException("Compaction source checksum or committed footer is invalid.");
            return header.FrameLength;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static async ValueTask ReadExactlyAsync(SafeFileHandle handle, Memory<byte> target, long offset, CancellationToken token)
    {
        var read = 0;
        while (read < target.Length)
        {
            var amount = await RandomAccess.ReadAsync(handle, target[read..], offset + read, token).ConfigureAwait(false);
            if (amount == 0) throw new ObjectLogCorruptionException("Unexpected end of compaction source.");
            read += amount;
        }
    }
}
