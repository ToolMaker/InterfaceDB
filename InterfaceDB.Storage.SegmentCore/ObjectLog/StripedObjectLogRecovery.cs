namespace Idb.Storage.SegmentCore.ObjectLog;

public static class StripedObjectLogRecovery
{
    public static StripedObjectLogRecoveryResult Recover(
        string rootDirectory,
        bool repairActiveTails = true,
        IDurableFileSystem? durableFileSystem = null,
        IReadOnlySet<long>? committedTransactionIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullRoot = Path.GetFullPath(rootDirectory);
        var stripeCount = StripedObjectLogManifest.ReadRequired(fullRoot);
        var records = new Dictionary<string, StripedObjectLogRecoveredRecord>(StringComparer.Ordinal);
        long repairedTailBytes = 0;
        long scannedFrames = 0;
        var ignoredOrphans = 0;
        var transactionRecords = new List<StripedObjectLogReplayRecord>();
        var replayRecords = new List<StripedObjectLogReplayRecord>();

        for (var stripe = 0; stripe < stripeCount; stripe++)
        {
            var stripeRoot = StripeRoot(fullRoot, stripe);
            var recovered = ObjectLogRecovery.Recover(
                stripeRoot,
                repairActiveTails,
                durableFileSystem,
                committedTransactionIds);
            repairedTailBytes += recovered.RepairedTailBytes;
            scannedFrames += recovered.LastLsn;
            ignoredOrphans += recovered.IgnoredOrphanSegments;
            transactionRecords.AddRange(recovered.TransactionRecords.Select(record =>
                new StripedObjectLogReplayRecord(stripe, record)));
            replayRecords.AddRange(recovered.ReplayRecords.Select(record =>
                new StripedObjectLogReplayRecord(stripe, record)));
            foreach (var pair in recovered.Records)
            {
                if (!records.TryAdd(
                        pair.Key,
                        new StripedObjectLogRecoveredRecord(
                            pair.Key,
                            pair.Value.Payload,
                            new StripedObjectLogRecordAddress(stripe, pair.Value.Address))))
                {
                    throw new ObjectLogCorruptionException(
                        $"Key '{pair.Key}' was recovered from more than one physical stripe.");
                }
            }
        }

        return new StripedObjectLogRecoveryResult(
            records,
            stripeCount,
            scannedFrames,
            repairedTailBytes,
            ignoredOrphans)
        {
            TransactionRecords = transactionRecords,
            ReplayRecords = replayRecords
        };
    }

    internal static string StripeRoot(string rootDirectory, int stripe) => Path.Combine(
        rootDirectory,
        "stripes",
        stripe.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
}
