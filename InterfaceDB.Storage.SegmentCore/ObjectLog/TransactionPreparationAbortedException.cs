namespace Idb.Storage.SegmentCore.ObjectLog;

// Internal proof that all admitted preparations drained without I/O failure and no commit exists.
internal sealed class TransactionPreparationAbortedException(Exception cause) : Exception("Transaction admission aborted safely.", cause);
