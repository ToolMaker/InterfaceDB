using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// Low-overhead, listener-driven diagnostics for SegmentCore. Instruments are disabled by
/// default and do not change the public API surface or storage format.
/// </summary>
internal static class SegmentCoreDiagnostics
{
    internal const string MeterName = "InterfaceDB.Storage.SegmentCore";
    internal const string OpenStageDurationInstrumentName =
        "interfacedb.segmentcore.open.stage.duration";
    internal const string OperationDurationInstrumentName =
        "interfacedb.segmentcore.operation.duration";
    internal const string OperationCountInstrumentName =
        "interfacedb.segmentcore.operation.count";
    internal const string StorageBytesInstrumentName =
        "interfacedb.segmentcore.storage.bytes";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> OpenStageDuration = Meter.CreateHistogram<double>(
        OpenStageDurationInstrumentName,
        unit: "ms",
        description: "Elapsed time for a SegmentCore store-open stage.");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        OperationDurationInstrumentName,
        unit: "ms",
        description: "Elapsed time for a SegmentCore maintenance or recovery operation.");
    private static readonly Counter<long> OperationCount = Meter.CreateCounter<long>(
        OperationCountInstrumentName,
        unit: "{operation}",
        description: "Completed SegmentCore maintenance or recovery operations.");
    private static readonly Histogram<long> StorageBytes = Meter.CreateHistogram<long>(
        StorageBytesInstrumentName,
        unit: "By",
        description: "SegmentCore storage bytes observed at maintenance boundaries.");

    internal static long StartTimestamp() => Stopwatch.GetTimestamp();

    internal static void RecordOpenStage(
        string stage,
        long startedTimestamp,
        SegmentCoreTransactionProtocol protocol,
        bool succeeded = true)
    {
        if (!OpenStageDuration.Enabled)
        {
            return;
        }

        try
        {
            OpenStageDuration.Record(
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("transaction.protocol", ProtocolName(protocol)),
                new KeyValuePair<string, object?>("result", succeeded ? "success" : "failure"));
        }
        catch
        {
            // Diagnostics listeners are external observers and must not affect storage correctness.
        }
    }

    internal static void RecordOperation(
        string operation,
        long startedTimestamp,
        SegmentCoreTransactionProtocol protocol,
        bool succeeded)
    {
        var protocolName = ProtocolName(protocol);
        var result = succeeded ? "success" : "failure";
        if (OperationDuration.Enabled)
        {
            try
            {
                OperationDuration.Record(
                    Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                    new KeyValuePair<string, object?>("operation", operation),
                    new KeyValuePair<string, object?>("transaction.protocol", protocolName),
                    new KeyValuePair<string, object?>("result", result));
            }
            catch
            {
                // Diagnostics listeners are external observers and must not affect storage correctness.
            }
        }

        if (OperationCount.Enabled)
        {
            try
            {
                OperationCount.Add(
                    1,
                    new KeyValuePair<string, object?>("operation", operation),
                    new KeyValuePair<string, object?>("transaction.protocol", protocolName),
                    new KeyValuePair<string, object?>("result", result));
            }
            catch
            {
                // Diagnostics listeners are external observers and must not affect storage correctness.
            }
        }
    }

    internal static void RecordStorageBytes(string component, long bytes)
    {
        if (!StorageBytes.Enabled)
        {
            return;
        }

        try
        {
            StorageBytes.Record(
                bytes,
                new KeyValuePair<string, object?>("component", component));
        }
        catch
        {
            // Diagnostics listeners are external observers and must not affect storage correctness.
        }
    }

    private static string ProtocolName(SegmentCoreTransactionProtocol protocol) =>
        protocol == SegmentCoreTransactionProtocol.MutationWriteAheadLog
            ? "mutation_wal"
            : "participant_commit";
}
