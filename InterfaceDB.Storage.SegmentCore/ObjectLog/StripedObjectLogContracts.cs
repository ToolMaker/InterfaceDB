namespace Idb.Storage.SegmentCore.ObjectLog;

public enum ObjectLogQueueFullBehavior
{
    Wait = 0,
    Reject = 1
}

public sealed class ObjectLogBackpressureException : IOException
{
    public ObjectLogBackpressureException(string message)
        : base(message)
    {
    }
}

public sealed record StripedObjectLogProbeOptions
{
    public int StripeCount { get; init; } = 8;

    public int MaximumBatchRecords { get; init; } = 64;

    public int QueueCapacityPerStripe { get; init; } = 1_024;

    public ObjectLogQueueFullBehavior QueueFullBehavior { get; init; } =
        ObjectLogQueueFullBehavior.Wait;

    public TimeSpan GroupCommitDelay { get; init; } = TimeSpan.Zero;

    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumCombinedWriteBytes { get; init; } = 1024 * 1024;

    public ObjectLogDurabilityStrategy DurabilityStrategy { get; init; } =
        ObjectLogDurabilityStrategy.ExplicitFlush;

    internal void Validate()
    {
        if (StripeCount is < 1 or > 64 || !System.Numerics.BitOperations.IsPow2((uint)StripeCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(StripeCount),
                "The stripe count must be a power of two between 1 and 64.");
        }

        if (MaximumBatchRecords is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumBatchRecords),
                "The maximum batch size must be between 1 and 16,384 records.");
        }

        if (QueueCapacityPerStripe is < 1 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueueCapacityPerStripe),
                "The per-stripe queue capacity must be between 1 and 1,048,576 writes.");
        }

        if (!Enum.IsDefined(QueueFullBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(QueueFullBehavior));
        }

        if (GroupCommitDelay < TimeSpan.Zero || GroupCommitDelay > TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(GroupCommitDelay),
                "The group-commit delay must be between zero and 100 milliseconds.");
        }

        if (MaxSegmentBytes < 4 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentBytes),
                "Segments must be at least 4 KiB.");
        }

        if (MaximumCombinedWriteBytes is < 4 * 1024 or > ObjectLogFormat.MaximumFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCombinedWriteBytes));
        }

        if (!Enum.IsDefined(DurabilityStrategy))
        {
            throw new ArgumentOutOfRangeException(nameof(DurabilityStrategy));
        }
    }
}

public readonly record struct StripedObjectLogRecordAddress(
    int Stripe,
    ObjectLogRecordAddress Address);

public sealed record StripedObjectLogRecoveredRecord(
    string Key,
    byte[] Payload,
    StripedObjectLogRecordAddress Address);

public sealed record StripedObjectLogRecoveryResult(
    IReadOnlyDictionary<string, StripedObjectLogRecoveredRecord> Records,
    int StripeCount,
    long ScannedFrames,
    long RepairedTailBytes,
    int IgnoredOrphanSegments)
{
    internal IReadOnlyList<StripedObjectLogReplayRecord> TransactionRecords { get; init; } = [];

    internal IReadOnlyList<StripedObjectLogReplayRecord> ReplayRecords { get; init; } = [];
}

internal readonly record struct StripedObjectLogReplayRecord(
    int Stripe,
    ObjectLogReplayRecord Record);

public readonly record struct StripedObjectLogStatistics(
    long SubmittedRecords,
    long DurableBatches,
    long DurabilityFlushes,
    int LargestBatch,
    long BackpressuredRecords,
    long RejectedRecords,
    int CurrentOutstandingWrites,
    int PeakOutstandingWritesPerStripe)
{
    public double AverageBatchSize => DurableBatches == 0
        ? 0
        : (double)SubmittedRecords / DurableBatches;
}
