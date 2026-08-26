using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class SegmentCoreScaleGateRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = SegmentCoreScaleGateOptions.Parse(args);
        var ownsRoot = string.IsNullOrWhiteSpace(options.RootPath);
        var root = ownsRoot
            ? Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreScale", Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(options.RootPath);
        Directory.CreateDirectory(root);
        var report = new SegmentCoreScaleGateReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Options = options,
            StoreRoot = root,
            AvailableManagedMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            LivePayloadBytes = checked((long)options.Records * options.PayloadBytes)
        };
        report.DataToManagedMemoryRatio = report.AvailableManagedMemoryBytes <= 0
            ? 0
            : (double)report.LivePayloadBytes / report.AvailableManagedMemoryBytes;
        report.DataLargerThanManagedMemory = report.LivePayloadBytes > report.AvailableManagedMemoryBytes;

        try
        {
            Require(
                !options.RequireDataLargerThanManagedMemory || report.DataLargerThanManagedMemory,
                $"Live payload {report.LivePayloadBytes:N0} bytes does not exceed the measured managed-memory "
                + $"budget of {report.AvailableManagedMemoryBytes:N0} bytes.");
            await ExecuteAsync(root, options, report).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            if (ownsRoot && !options.KeepData)
            {
                TryDeleteOwnedRoot(root);
            }
        }

        Console.WriteLine(report.Passed
            ? $"SegmentCore {options.Records:N0}-record scale gate: PASS"
            : $"SegmentCore {options.Records:N0}-record scale gate: FAIL");
        foreach (var metric in report.Metrics)
        {
            Console.WriteLine(
                $"  {metric.Name}: {metric.Elapsed.TotalMilliseconds:N1} ms; "
                + $"{metric.OperationsPerSecond:N1} ops/s; {metric.AllocatedBytes:N0} B allocated");
        }

        Console.WriteLine(
            $"  Reopen locator: {(report.ReopenedWithPagedPrimaryIndex ? "paged" : "resident")}; "
            + $"live payload / managed-memory budget: {report.DataToManagedMemoryRatio:N3}x; "
            + $"retained WAL skipped: {report.WalRecoveryPrefixBytesSkipped:N0} bytes; "
            + $"periodic checkpoints: {report.PeriodicCheckpoints:N0}");

        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task ExecuteAsync(
        string root,
        SegmentCoreScaleGateOptions options,
        SegmentCoreScaleGateReport report)
    {
        var storeOptions = CreateStoreOptions(options);
        var store = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
        try
        {
            report.Metrics.Add(await MeasureAsync(
                "Atomic batch ingestion",
                options.Records,
                async () => report.PeriodicCheckpoints = await WriteAllAsync(store, options).ConfigureAwait(false))
                .ConfigureAwait(false));
            Require(store.Count == options.Records, "Live count after ingestion is incorrect.");

            SegmentCoreCheckpointResult checkpoint = default;
            report.Metrics.Add(await MeasureAsync(
                "Full primary checkpoint",
                options.Records,
                async () => checkpoint = await store.CheckpointAsync().ConfigureAwait(false)).ConfigureAwait(false));
            Require(checkpoint.Generation > 0 && checkpoint.Entries == options.Records,
                "Checkpoint metadata is incorrect.");

            report.Metrics.Add(await MeasureAsync(
                "No-op checkpoint",
                1,
                async () =>
                {
                    var noOp = await store.CheckpointAsync().ConfigureAwait(false);
                    Require(noOp.Generation == checkpoint.Generation,
                        "No-op checkpoint published a new generation.");
                }).ConfigureAwait(false));

            SegmentCoreIntegrityResult integrity = default;
            report.Metrics.Add(await MeasureAsync(
                "Streaming integrity verification",
                options.Records,
                async () => integrity = await store.VerifyIntegrityAsync().ConfigureAwait(false)).ConfigureAwait(false));
            Require(integrity.IsValid && integrity.EntriesChecked == options.Records,
                integrity.IsValid ? "Integrity count is incorrect." : string.Join("; ", integrity.Failures));
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        SegmentCoreStore? reopened = null;
        report.Metrics.Add(await MeasureAsync(
            "Checkpoint reopen",
            1,
            async () => reopened = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false))
            .ConfigureAwait(false));
        await using var reopenedStore = reopened
            ?? throw new InvalidOperationException("Scale gate did not reopen SegmentCore.");
        Require(reopenedStore.Count == options.Records, "Reopened live count is incorrect.");
        Require(reopenedStore.Statistics.TailRecordsReplayed == 0,
            "Checkpoint reopen replayed an unexpected tail.");
        report.ReopenedWithPagedPrimaryIndex = reopenedStore.Statistics.PrimaryIndexIsPaged;
        report.WalRecoveryPrefixBytesSkipped = reopenedStore.Statistics.WalRecoveryPrefixBytesSkipped;

        var samples = Math.Min(options.SampleReads, options.Records);
        report.Metrics.Add(await MeasureAsync(
            "Deterministic sampled reads",
            samples,
            async () =>
            {
                for (var sample = 0; sample < samples; sample++)
                {
                    var ordinal = samples == 1
                        ? 0
                        : (int)((long)sample * (options.Records - 1) / (samples - 1));
                    var payload = await reopenedStore.GetAsync(Key(ordinal)).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"Missing sampled record {ordinal:N0}.");
                    ValidatePayload(payload, ordinal, options.PayloadBytes);
                }
            }).ConfigureAwait(false));
    }

    private static async Task<int> WriteAllAsync(
        SegmentCoreStore store,
        SegmentCoreScaleGateOptions options)
    {
        var batches = (options.Records + options.BatchSize - 1) / options.BatchSize;
        var waveSize = options.CheckpointEveryBatches == 0
            ? batches
            : options.CheckpointEveryBatches;
        var periodicCheckpoints = 0;
        for (var firstBatch = 0; firstBatch < batches; firstBatch += waveSize)
        {
            var waveBatches = Math.Min(waveSize, batches - firstBatch);
            await Parallel.ForEachAsync(
                Enumerable.Range(firstBatch, waveBatches),
                new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency },
                async (batch, token) =>
                {
                    var first = batch * options.BatchSize;
                    var count = Math.Min(options.BatchSize, options.Records - first);
                    await using var transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
                    for (var offset = 0; offset < count; offset++)
                    {
                        var ordinal = first + offset;
                        await transaction.SaveAsync(
                            Key(ordinal),
                            CreatePayload(ordinal, options.PayloadBytes),
                            token).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (options.CheckpointEveryBatches > 0 && firstBatch + waveBatches < batches)
            {
                _ = await store.CheckpointAsync().ConfigureAwait(false);
                periodicCheckpoints++;
            }
        }

        return periodicCheckpoints;
    }

    private static SegmentCoreOptions CreateStoreOptions(SegmentCoreScaleGateOptions options)
    {
        const int maximumWalRecordBytes = 256 * 1024 * 1024;
        var requestedWalRecordBytes = checked((long)options.BatchSize * (options.PayloadBytes + 512L));
        if (requestedWalRecordBytes > maximumWalRecordBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.BatchSize),
                $"The selected batch and payload require a {requestedWalRecordBytes:N0}-byte WAL record; "
                + $"the maximum is {maximumWalRecordBytes:N0} bytes.");
        }

        return new SegmentCoreOptions
        {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        WalRecycleMinimumReclaimBytes = 16L * 1024 * 1024,
        WalMaximumBytesBeforeForcedRecycle = 256L * 1024 * 1024,
        PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
        {
            Mode = options.PrimaryIndexMode,
            AutomaticPagingMinimumEntries = options.PagingThreshold
        },
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = options.Stripes,
            MaximumBatchRecords = 256,
            QueueCapacityPerStripe = 4_096,
            GroupCommitDelay = options.DurabilityStrategy == ObjectLogDurabilityStrategy.ExplicitFlush
                ? TimeSpan.FromMilliseconds(1)
                : TimeSpan.Zero,
            MaxSegmentBytes = 256L * 1024 * 1024,
            MaximumCombinedWriteBytes = (int)Math.Clamp(
                requestedWalRecordBytes,
                4L * 1024 * 1024,
                maximumWalRecordBytes),
            DurabilityStrategy = options.DurabilityStrategy
        },
        ReadOptions = new SegmentCoreReadOptions
        {
            MaximumParallelReads = 16,
            MaximumReadWindowBytes = 8 * 1024 * 1024,
            MaximumReadGapBytes = 128 * 1024,
            StreamingBatchRecords = 8_192
        }
        };
    }

    private static async Task<SegmentCoreScaleMetric> MeasureAsync(
        string name,
        int operations,
        Func<Task> operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await operation().ConfigureAwait(false);
        stopwatch.Stop();
        return new SegmentCoreScaleMetric(
            name,
            operations,
            stopwatch.Elapsed,
            stopwatch.Elapsed.TotalSeconds == 0 ? 0 : operations / stopwatch.Elapsed.TotalSeconds,
            GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    private static byte[] CreatePayload(int ordinal, int length)
    {
        var payload = new byte[length];
        BitConverter.TryWriteBytes(payload, ordinal);
        for (var index = sizeof(int); index < length; index++)
        {
            payload[index] = (byte)((ordinal * 31 + index * 17) & 0xFF);
        }

        return payload;
    }

    private static void ValidatePayload(byte[] payload, int ordinal, int expectedLength)
    {
        if (payload.Length != expectedLength || BitConverter.ToInt32(payload) != ordinal)
        {
            throw new InvalidDataException($"Sampled record {ordinal:N0} has an invalid payload header.");
        }

        for (var index = sizeof(int); index < payload.Length; index++)
        {
            if (payload[index] != (byte)((ordinal * 31 + index * 17) & 0xFF))
            {
                throw new InvalidDataException($"Sampled record {ordinal:N0} has corrupt payload data.");
            }
        }
    }

    private static string Key(int ordinal) => $"scale-{ordinal:D10}";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static async Task WriteReportAsync(SegmentCoreScaleGateReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static void TryDeleteOwnedRoot(string root)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreScale"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(root);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected scale-gate root '{target}'.");
        }

        try { Directory.Delete(target, recursive: true); } catch { }
    }
}
