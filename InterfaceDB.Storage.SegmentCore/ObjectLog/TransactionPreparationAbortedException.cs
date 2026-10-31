namespace Idb.Storage.SegmentCore.ObjectLog;

// Internal proof that all admitted preparations drained without I/O failure and no commit exists.
internal sealed class TransactionPreparationAbortedException(Exception cause, ulong preparedStripeMask = 0)
    : Exception("Transaction admission aborted safely.", cause)
{
    // Rejected preparation has no commit, but its successfully appended derived frames
    // must be durable before a checkpoint publishes replay positions beyond them.
    public ulong PreparedStripeMask { get; } = preparedStripeMask;
}
