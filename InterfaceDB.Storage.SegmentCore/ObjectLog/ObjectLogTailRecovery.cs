using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

internal static class ObjectLogTailRecovery
{
    public static ObjectLogRecoveryResult Recover(
        string rootDirectory,
        ObjectLogWritePosition position,
        Action<ObjectLogReplayRecord> replayRecord,
        bool repairActiveTail,
        IDurableFileSystem? durableFileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(replayRecord);
        if (position.SegmentId <= 0
            || position.NextOffset < ObjectLogFormat.SegmentHeaderSize
            || position.LastLsn < 0)
        {
            throw new ObjectLogCorruptionException("The object-log checkpoint contains an invalid replay position.");
        }

        var fullRoot = Path.GetFullPath(rootDirectory);
        var segmentsDirectory = Path.Combine(fullRoot, "segments");
        var candidates = Directory
            .EnumerateFiles(segmentsDirectory, "segment-*.idbseg", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: ObjectLogFormat.TryParseSegmentId(path, out var id), Id: id))
            .Where(static candidate => candidate.Parsed)
            .ToDictionary(static candidate => candidate.Id, static candidate => candidate.Path);
        if (!candidates.TryGetValue(position.SegmentId, out var currentPath))
        {
            throw new ObjectLogCorruptionException(
                $"Checkpoint references missing segment {position.SegmentId:N0} in '{segmentsDirectory}'.");
        }

        var segmentId = position.SegmentId;
        var offset = position.NextOffset;
        var lastLsn = position.LastLsn;
        var expectedHeaderChecksum = position.SegmentHeaderChecksum;
        var followed = 0;
        long repairedTailBytes = 0;
        long activeLength;
        uint activeHeaderChecksum;
        var segmentHeaderBytes = new byte[ObjectLogFormat.SegmentHeaderSize];
        var frameHeaderBytes = new byte[ObjectLogFormat.FrameHeaderSize];

        while (true)
        {
            followed++;
            using var handle = File.OpenHandle(
                currentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            var physicalLength = RandomAccess.GetLength(handle);
            if (physicalLength < ObjectLogFormat.SegmentHeaderSize || offset > physicalLength)
            {
                throw new ObjectLogCorruptionException(
                    $"Checkpoint replay position {offset:N0} is outside segment '{currentPath}'.");
            }

            ReadExactly(handle, segmentHeaderBytes, 0, currentPath);
            var segmentHeader = ObjectLogFormat.ParseSegmentHeader(segmentHeaderBytes, currentPath);
            if (segmentHeader.SegmentId != segmentId
                || segmentHeader.HeaderChecksum != expectedHeaderChecksum)
            {
                throw new ObjectLogCorruptionException(
                    $"Checkpoint segment '{currentPath}' no longer matches its durable header.");
            }

            long? nextSegmentId = null;
            while (offset < physicalLength)
            {
                var remaining = physicalLength - offset;
                if (remaining < ObjectLogFormat.FrameHeaderSize)
                {
                    break;
                }

                ReadExactly(handle, frameHeaderBytes, offset, currentPath);
                ObjectLogFormat.FrameHeader header;
                try
                {
                    header = ObjectLogFormat.ParseFrameHeader(frameHeaderBytes, currentPath, offset);
                }
                catch (ObjectLogCorruptionException) when (!HasValidFrameAfter(handle, currentPath, offset, physicalLength))
                {
                    break;
                }

                if (remaining < header.FrameLength)
                {
                    break;
                }

                var rented = ArrayPool<byte>.Shared.Rent(header.FrameLength);
                try
                {
                    var frame = rented.AsSpan(0, header.FrameLength);
                    ReadExactly(handle, frame, offset, currentPath);
                    ObjectLogFormat.ValidateFrame(frame, header, currentPath, offset);
                    if (header.Lsn != checked(lastLsn + 1))
                    {
                        throw new ObjectLogCorruptionException(
                            $"Segment '{currentPath}' has LSN {header.Lsn:N0} after checkpoint; expected {lastLsn + 1:N0}.");
                    }

                    lastLsn = header.Lsn;
                    if (header.Kind is ObjectLogRecordKind.Put or ObjectLogRecordKind.Delete)
                    {
                        if (header.KeyLength == 0
                            || header.Auxiliary < 0
                            || (header.Kind == ObjectLogRecordKind.Delete && header.PayloadLength != 0))
                        {
                            throw new ObjectLogCorruptionException(
                                $"Segment '{currentPath}' has an invalid data frame at offset {offset:N0}.");
                        }

                        replayRecord(new ObjectLogReplayRecord(
                            header.Kind,
                            ObjectLogFormat.ReadKey(frame, header),
                            new ObjectLogRecordAddress(segmentId, offset, header.FrameLength, header.Lsn),
                            header.Auxiliary,
                            header.DataChecksum));
                    }
                    else if (header.Kind == ObjectLogRecordKind.Seal)
                    {
                        if (header.KeyLength != 0
                            || header.PayloadLength != 0
                            || header.Auxiliary <= 0
                            || header.Auxiliary == segmentId
                            || offset + header.FrameLength != physicalLength)
                        {
                            throw new ObjectLogCorruptionException(
                                $"Segment '{currentPath}' has an invalid checkpoint-tail seal at offset {offset:N0}.");
                        }

                        nextSegmentId = header.Auxiliary;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }

                offset += header.FrameLength;
                if (nextSegmentId is not null)
                {
                    break;
                }
            }

            if (nextSegmentId is null)
            {
                activeLength = offset;
                activeHeaderChecksum = segmentHeader.HeaderChecksum;
                if (physicalLength != offset)
                {
                    repairedTailBytes = physicalLength - offset;
                    if (repairActiveTail)
                    {
                        RepairTail(currentPath, offset, durableFileSystem ?? PlatformDurableFileSystem.Instance);
                    }
                    else
                    {
                        activeLength = physicalLength;
                    }
                }

                break;
            }

            segmentId = nextSegmentId.Value;
            if (!candidates.TryGetValue(segmentId, out var nextPath))
            {
                throw new ObjectLogCorruptionException(
                    $"Sealed segment '{currentPath}' references missing segment {segmentId:N0}.");
            }

            currentPath = nextPath;
            offset = ObjectLogFormat.SegmentHeaderSize;
            using (var nextHandle = File.OpenHandle(
                       currentPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       FileOptions.RandomAccess))
            {
                ReadExactly(nextHandle, segmentHeaderBytes, 0, currentPath);
                var nextHeader = ObjectLogFormat.ParseSegmentHeader(segmentHeaderBytes, currentPath);
                if (nextHeader.SegmentId != segmentId
                    || nextHeader.PreviousSegmentId != segmentHeader.SegmentId
                    || nextHeader.PreviousHeaderChecksum != segmentHeader.HeaderChecksum)
                {
                    throw new ObjectLogCorruptionException(
                        $"Segment '{currentPath}' does not continue the checkpoint segment chain.");
                }

                expectedHeaderChecksum = nextHeader.HeaderChecksum;
            }
        }

        return new ObjectLogRecoveryResult(
            new Dictionary<string, ObjectLogRecoveredRecord>(StringComparer.Ordinal),
            lastLsn,
            segmentId,
            currentPath,
            activeLength,
            repairedTailBytes,
            Math.Max(0, candidates.Count - followed),
            activeHeaderChecksum);
    }

    private static bool HasValidFrameAfter(
        SafeFileHandle handle,
        string path,
        long corruptOffset,
        long physicalLength)
    {
        Span<byte> headerBytes = stackalloc byte[ObjectLogFormat.FrameHeaderSize];
        for (var candidate = corruptOffset + ObjectLogFormat.FrameAlignment;
             candidate + ObjectLogFormat.FrameHeaderSize <= physicalLength;
             candidate += ObjectLogFormat.FrameAlignment)
        {
            ReadExactly(handle, headerBytes, candidate, path);
            try
            {
                var header = ObjectLogFormat.ParseFrameHeader(headerBytes, path, candidate);
                if (candidate + header.FrameLength > physicalLength)
                {
                    continue;
                }

                var rented = ArrayPool<byte>.Shared.Rent(header.FrameLength);
                try
                {
                    var frame = rented.AsSpan(0, header.FrameLength);
                    ReadExactly(handle, frame, candidate, path);
                    ObjectLogFormat.ValidateFrame(frame, header, path, candidate);
                    return true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }
            }
            catch (ObjectLogCorruptionException)
            {
                // Continue looking for a later independently valid frame.
            }
        }

        return false;
    }

    private static void RepairTail(string path, long validLength, IDurableFileSystem durableFileSystem)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.RandomAccess);
        RandomAccess.SetLength(handle, validLength);
        durableFileSystem.FlushFile(handle);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset, string path)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[totalRead..], offset + totalRead);
            if (read == 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Unexpected end of segment '{path}' at offset {offset + totalRead:N0}.");
            }

            totalRead += read;
        }
    }
}
