using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

public static class ObjectLogRecovery
{
    public static ObjectLogRecoveryResult Recover(
        string rootDirectory,
        bool repairActiveTail = true,
        IDurableFileSystem? durableFileSystem = null,
        IReadOnlySet<long>? committedTransactionIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var segmentsDirectory = Path.Combine(Path.GetFullPath(rootDirectory), "segments");
        if (!Directory.Exists(segmentsDirectory))
        {
            return ObjectLogRecoveryResult.Empty;
        }

        var candidates = Directory
            .EnumerateFiles(segmentsDirectory, "segment-*.idbseg", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: ObjectLogFormat.TryParseSegmentId(path, out var id), Id: id))
            .Where(candidate => candidate.Parsed)
            .OrderBy(candidate => candidate.Id)
            .ToArray();

        if (candidates.Length == 0)
        {
            return ObjectLogRecoveryResult.Empty;
        }

        var pathsById = candidates.ToDictionary(candidate => candidate.Id, candidate => candidate.Path);
        if (!pathsById.TryGetValue(1, out var currentPath))
        {
            return ObjectLogRecoveryResult.Empty with { IgnoredOrphanSegments = candidates.Length };
        }

        var records = new Dictionary<string, ObjectLogRecoveredRecord>(StringComparer.Ordinal);
        var transactionRecords = new List<ObjectLogReplayRecord>();
        var replayRecords = new List<ObjectLogReplayRecord>();
        var followedSegmentIds = new HashSet<long>();
        var expectedSegmentId = 1L;
        var expectedPreviousSegmentId = 0L;
        var expectedPreviousHeaderChecksum = 0u;
        var lastLsn = 0L;
        var repairedTailBytes = 0L;
        long activeSegmentId;
        long activeLength;
        uint activeHeaderChecksum;

        while (true)
        {
            currentPath = pathsById[expectedSegmentId];
            followedSegmentIds.Add(expectedSegmentId);
            var scan = ScanSegment(
                currentPath,
                expectedSegmentId,
                expectedPreviousSegmentId,
                expectedPreviousHeaderChecksum,
                records,
                transactionRecords,
                replayRecords,
                committedTransactionIds,
                ref lastLsn);

            if (scan.NextSegmentId is null)
            {
                activeSegmentId = expectedSegmentId;
                activeLength = scan.ValidLength;
                activeHeaderChecksum = scan.HeaderChecksum;

                if (scan.PhysicalLength != scan.ValidLength)
                {
                    repairedTailBytes = scan.PhysicalLength - scan.ValidLength;
                    if (repairActiveTail)
                    {
                        RepairTail(currentPath, scan.ValidLength, durableFileSystem ?? PlatformDurableFileSystem.Instance);
                    }
                    else
                    {
                        activeLength = scan.PhysicalLength;
                    }
                }

                break;
            }

            var nextSegmentId = scan.NextSegmentId.Value;
            if (!pathsById.ContainsKey(nextSegmentId))
            {
                throw new ObjectLogCorruptionException(
                    $"Sealed segment '{currentPath}' references missing segment {nextSegmentId:N0}.");
            }

            expectedPreviousSegmentId = expectedSegmentId;
            expectedPreviousHeaderChecksum = scan.HeaderChecksum;
            expectedSegmentId = nextSegmentId;
        }

        return new ObjectLogRecoveryResult(
            records,
            lastLsn,
            activeSegmentId,
            pathsById[activeSegmentId],
            activeLength,
            repairedTailBytes,
            candidates.Length - followedSegmentIds.Count,
            activeHeaderChecksum)
        {
            TransactionRecords = transactionRecords,
            ReplayRecords = replayRecords
        };
    }

    internal static long FindHighestSegmentId(string rootDirectory)
    {
        var segmentsDirectory = Path.Combine(Path.GetFullPath(rootDirectory), "segments");
        return !Directory.Exists(segmentsDirectory)
            ? 0
            : Directory
                .EnumerateFiles(segmentsDirectory, "segment-*.idbseg", SearchOption.TopDirectoryOnly)
                .Select(path => ObjectLogFormat.TryParseSegmentId(path, out var id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max();
    }

    private static SegmentScanResult ScanSegment(
        string path,
        long expectedSegmentId,
        long expectedPreviousSegmentId,
        uint expectedPreviousHeaderChecksum,
        Dictionary<string, ObjectLogRecoveredRecord> records,
        List<ObjectLogReplayRecord> transactionRecords,
        List<ObjectLogReplayRecord> replayRecords,
        IReadOnlySet<long>? committedTransactionIds,
        ref long lastLsn)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        var physicalLength = RandomAccess.GetLength(handle);
        if (physicalLength < ObjectLogFormat.SegmentHeaderSize)
        {
            throw new ObjectLogCorruptionException($"Segment '{path}' has an incomplete header.");
        }

        var segmentHeaderBytes = new byte[ObjectLogFormat.SegmentHeaderSize];
        ReadExactly(handle, segmentHeaderBytes, 0, path);
        var segmentHeader = ObjectLogFormat.ParseSegmentHeader(segmentHeaderBytes, path);
        if (segmentHeader.SegmentId != expectedSegmentId
            || segmentHeader.PreviousSegmentId != expectedPreviousSegmentId
            || segmentHeader.PreviousHeaderChecksum != expectedPreviousHeaderChecksum)
        {
            throw new ObjectLogCorruptionException(
                $"Segment '{path}' does not match its filename or predecessor chain.");
        }

        var offset = (long)ObjectLogFormat.SegmentHeaderSize;
        while (offset < physicalLength)
        {
            var remaining = physicalLength - offset;
            if (remaining < ObjectLogFormat.FrameHeaderSize)
            {
                return new SegmentScanResult(
                    segmentHeader.HeaderChecksum,
                    offset,
                    physicalLength,
                    null);
            }

            var frameHeaderBytes = new byte[ObjectLogFormat.FrameHeaderSize];
            ReadExactly(handle, frameHeaderBytes, offset, path);
            ObjectLogFormat.FrameHeader frameHeader;
            try
            {
                frameHeader = ObjectLogFormat.ParseFrameHeader(frameHeaderBytes, path, offset);
            }
            catch (ObjectLogCorruptionException) when (!HasValidFrameAfter(handle, path, offset, physicalLength))
            {
                return new SegmentScanResult(
                    segmentHeader.HeaderChecksum,
                    offset,
                    physicalLength,
                    null);
            }

            if (remaining < frameHeader.FrameLength)
            {
                return new SegmentScanResult(
                    segmentHeader.HeaderChecksum,
                    offset,
                    physicalLength,
                    null);
            }

            var frame = new byte[frameHeader.FrameLength];
            ReadExactly(handle, frame, offset, path);
            ObjectLogFormat.ValidateFrame(frame, frameHeader, path, offset);

            if (frameHeader.Lsn != checked(lastLsn + 1))
            {
                throw new ObjectLogCorruptionException(
                    $"Segment '{path}' has non-contiguous LSN {frameHeader.Lsn:N0} at offset {offset:N0}; expected {lastLsn + 1:N0}.");
            }

            lastLsn = frameHeader.Lsn;
            switch (frameHeader.Kind)
            {
                case ObjectLogRecordKind.Put:
                {
                    var key = RequireKey(frame, frameHeader, path, offset);
                    if (frameHeader.Auxiliary < 0)
                    {
                        throw new ObjectLogCorruptionException(
                            $"Put frame in '{path}' has an invalid transaction ID at offset {offset:N0}.");
                    }

                    var address = new ObjectLogRecordAddress(
                        expectedSegmentId,
                        offset,
                        frameHeader.FrameLength,
                        frameHeader.Lsn);
                    var replayRecord = new ObjectLogReplayRecord(
                        frameHeader.Kind,
                        key,
                        address,
                        frameHeader.Auxiliary,
                        frameHeader.DataChecksum);
                    replayRecords.Add(replayRecord);
                    if (frameHeader.Auxiliary > 0)
                    {
                        transactionRecords.Add(replayRecord);
                    }

                    if (frameHeader.Auxiliary == 0
                        || committedTransactionIds?.Contains(frameHeader.Auxiliary) == true)
                    {
                        records[key] = new ObjectLogRecoveredRecord(
                            key,
                            ObjectLogFormat.ReadPayload(frame, frameHeader),
                            address);
                    }
                    break;
                }
                case ObjectLogRecordKind.Delete:
                {
                    var key = RequireKey(frame, frameHeader, path, offset);
                    if (frameHeader.PayloadLength != 0 || frameHeader.Auxiliary < 0)
                    {
                        throw new ObjectLogCorruptionException(
                            $"Delete frame in '{path}' is invalid at offset {offset:N0}.");
                    }

                    var address = new ObjectLogRecordAddress(
                        expectedSegmentId,
                        offset,
                        frameHeader.FrameLength,
                        frameHeader.Lsn);
                    var replayRecord = new ObjectLogReplayRecord(
                        frameHeader.Kind,
                        key,
                        address,
                        frameHeader.Auxiliary,
                        frameHeader.DataChecksum);
                    replayRecords.Add(replayRecord);
                    if (frameHeader.Auxiliary > 0)
                    {
                        transactionRecords.Add(replayRecord);
                    }

                    if (frameHeader.Auxiliary == 0
                        || committedTransactionIds?.Contains(frameHeader.Auxiliary) == true)
                    {
                        records.Remove(key);
                    }
                    break;
                }
                case ObjectLogRecordKind.Seal:
                {
                    if (frameHeader.KeyLength != 0
                        || frameHeader.PayloadLength != 0
                        || frameHeader.Auxiliary <= 0
                        || frameHeader.Auxiliary == expectedSegmentId
                        || offset + frameHeader.FrameLength != physicalLength)
                    {
                        throw new ObjectLogCorruptionException(
                            $"Segment '{path}' has an invalid or non-terminal seal at offset {offset:N0}.");
                    }

                    return new SegmentScanResult(
                        segmentHeader.HeaderChecksum,
                        physicalLength,
                        physicalLength,
                        frameHeader.Auxiliary);
                }
                default:
                    throw new ObjectLogCorruptionException(
                        $"Segment '{path}' has unknown record kind at offset {offset:N0}.");
            }

            offset += frameHeader.FrameLength;
        }

        return new SegmentScanResult(
            segmentHeader.HeaderChecksum,
            offset,
            physicalLength,
            null);
    }

    private static bool HasValidFrameAfter(
        SafeFileHandle handle,
        string path,
        long corruptOffset,
        long physicalLength)
    {
        var headerBytes = new byte[ObjectLogFormat.FrameHeaderSize];
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

                var frame = new byte[header.FrameLength];
                ReadExactly(handle, frame, candidate, path);
                ObjectLogFormat.ValidateFrame(frame, header, path, candidate);
                return true;
            }
            catch (ObjectLogCorruptionException)
            {
                // Continue looking for a later aligned, independently valid frame.
            }
        }

        return false;
    }

    private static string RequireKey(
        ReadOnlySpan<byte> frame,
        ObjectLogFormat.FrameHeader frameHeader,
        string path,
        long offset)
    {
        if (frameHeader.KeyLength == 0)
        {
            throw new ObjectLogCorruptionException(
                $"Data frame in '{path}' has no key at offset {offset:N0}.");
        }

        return ObjectLogFormat.ReadKey(frame, frameHeader);
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

    private static void ReadExactly(
        SafeFileHandle handle,
        Span<byte> destination,
        long offset,
        string path)
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

    private readonly record struct SegmentScanResult(
        uint HeaderChecksum,
        long ValidLength,
        long PhysicalLength,
        long? NextSegmentId);
}
