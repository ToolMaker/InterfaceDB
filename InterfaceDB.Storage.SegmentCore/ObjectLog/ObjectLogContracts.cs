using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.ObjectLog;

public enum ObjectLogDurabilityMode
{
    Performance = 0,
    Durable = 1
}

public enum ObjectLogDurabilityStrategy
{
    ExplicitFlush = 0,
    WriteThrough = 1
}

public enum ObjectLogRecordKind : byte
{
    Put = 1,
    Delete = 2,
    Seal = 3
}

public sealed record ObjectLogProbeOptions
{
    public ObjectLogDurabilityMode DurabilityMode { get; init; } = ObjectLogDurabilityMode.Durable;

    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumCombinedWriteBytes { get; init; } = 1024 * 1024;

    public ObjectLogDurabilityStrategy DurabilityStrategy { get; init; } =
        ObjectLogDurabilityStrategy.ExplicitFlush;

    public bool FlushOnDispose { get; init; } = true;

    internal long NormalizedMaxSegmentBytes => Math.Max(MaxSegmentBytes, 4 * 1024);

    internal int NormalizedMaximumCombinedWriteBytes =>
        Math.Clamp(MaximumCombinedWriteBytes, 4 * 1024, ObjectLogFormat.MaximumFrameBytes);
}

public readonly record struct ObjectLogRecordAddress(
    long SegmentId,
    long Offset,
    int FrameLength,
    long Lsn);

public readonly record struct ObjectLogWritePosition(
    long SegmentId,
    long NextOffset,
    long LastLsn,
    uint SegmentHeaderChecksum);

internal readonly record struct ObjectLogReplayRecord(
    ObjectLogRecordKind Kind,
    string Key,
    ObjectLogRecordAddress Address,
    long TransactionId,
    uint DataChecksum);

internal readonly record struct ObjectLogAppendRequest(
    ObjectLogRecordKind Kind,
    string Key,
    ReadOnlyMemory<byte> Payload,
    long TransactionId);

public sealed record ObjectLogRecoveredRecord(
    string Key,
    byte[] Payload,
    ObjectLogRecordAddress Address);

public sealed record ObjectLogRecoveryResult(
    IReadOnlyDictionary<string, ObjectLogRecoveredRecord> Records,
    long LastLsn,
    long ActiveSegmentId,
    string? ActiveSegmentPath,
    long ActiveLength,
    long RepairedTailBytes,
    int IgnoredOrphanSegments,
    uint ActiveSegmentHeaderChecksum)
{
    internal IReadOnlyList<ObjectLogReplayRecord> TransactionRecords { get; init; } = [];

    internal IReadOnlyList<ObjectLogReplayRecord> ReplayRecords { get; init; } = [];

    public static ObjectLogRecoveryResult Empty { get; } = new(
        new Dictionary<string, ObjectLogRecoveredRecord>(StringComparer.Ordinal),
        0,
        0,
        null,
        0,
        0,
        0,
        0);
}

public interface IDurableFileSystem
{
    string DirectoryFlushStrategy { get; }

    void FlushFile(SafeFileHandle handle);

    void FlushDirectory(string directoryPath);

    void PublishFile(string temporaryPath, string finalPath);

    void ReplaceFile(string temporaryPath, string finalPath);
}

public sealed class ObjectLogCorruptionException : IOException
{
    public ObjectLogCorruptionException(string message)
        : base(message)
    {
    }
}
