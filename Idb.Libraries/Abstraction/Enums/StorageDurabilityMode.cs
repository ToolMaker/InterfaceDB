namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Selects the persistence contract used by file-backed repositories.
/// </summary>
public enum StorageDurabilityMode
{
    /// <summary>
    /// Optimizes local write throughput by writing payload files directly without write-through flushes or proposal locks.
    /// </summary>
    Performance = 0,

    /// <summary>
    /// Optimizes crash recovery and out-of-process coordination using proposal files, write-through flushes, and sidecar locks.
    /// </summary>
    Durable = 1
}
