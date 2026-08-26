using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class PhaseTwoRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = PhaseTwoOptions.Parse(args);
        var started = DateTimeOffset.UtcNow;
        var runRoot = Path.Combine(Path.GetTempPath(), "InterfaceDbPhaseTwo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new PhaseTwoReport
        {
            StartedUtc = started,
            CompletedUtc = started,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? Environment.GetEnvironmentVariable("HOSTTYPE")
                ?? "unknown",
            PhysicalDisk = Environment.GetEnvironmentVariable("INTERFACEDB_PHASE_TWO_DISK")
                ?? Environment.GetEnvironmentVariable("INTERFACEDB_PHASE_ONE_DISK")
                ?? "not supplied by runner",
            Options = options
        };
        report.Notes.Add("Every SegmentCore write is acknowledged only after its write-through object-log frame is durable and its primary locator is published in memory.");
        report.Notes.Add($"SegmentCore uses a bounded {options.QueueCapacityPerStripe:N0}-write queue per stripe in wait/backpressure mode and retains {options.CheckpointGenerationsToKeep} checkpoint generations.");
        report.Notes.Add("Exact reads include locator lookup, direct segment I/O, frame/header/footer CRC validation, payload extraction, and complete GateRecord deserialization.");
        report.Notes.Add("SQLite uses WAL, synchronous=FULL, one autocommit per object mutation, a primary-key lookup, no secondary indexes, and complete six-column GateRecord materialization.");
        report.Notes.Add("A checkpoint is a separately measured, crash-safe generation. Reopen loads it and replays only later log frames.");
        report.Notes.Add("Mixed workload is 80% exact reads, 10% updates, 5% inserts, and 5% deletes with disjoint update/delete keys.");

        try
        {
            PrintHeader(report);
            var cases = CreateSpecifications(options)
                .SelectMany(specification => options.PayloadBytes.SelectMany(payload =>
                    options.ConcurrencyLevels.Select(concurrency => new BenchmarkCase(specification, payload, concurrency))))
                .ToList();
            var random = new Random(250825);
            for (var index = cases.Count - 1; index > 0; index--)
            {
                var swap = random.Next(index + 1);
                (cases[index], cases[swap]) = (cases[swap], cases[index]);
            }

            foreach (var benchmarkCase in cases)
            {
                try
                {
                    report.Metrics.AddRange(await MeasureCaseAsync(
                        benchmarkCase,
                        runRoot,
                        options).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    var failure = $"{benchmarkCase.Specification.Name}, {benchmarkCase.PayloadBytes:N0} B, concurrency {benchmarkCase.Concurrency}: "
                        + $"{exception.GetType().Name}: {exception.Message}";
                    report.Failures.Add(failure);
                    Console.WriteLine($"  FAIL {failure}");
                    Console.WriteLine(exception);
                }
            }

            Analyze(report);
            if (options.CrashCycles > 0)
            {
                var selectedStripe = report.Metrics
                    .Where(static metric => metric.Engine == "SegmentCore" && metric.Operation == "Save")
                    .OrderByDescending(static metric => metric.OperationsPerSecond)
                    .Select(static metric => metric.StripeCount)
                    .FirstOrDefault();
                if (selectedStripe > 0)
                {
                    report.CrashMetrics.AddRange(await RunCrashCyclesAsync(
                        runRoot,
                        options,
                        selectedStripe).ConfigureAwait(false));
                    foreach (var failed in report.CrashMetrics.Where(static metric => !metric.Passed))
                    {
                        report.Failures.Add($"Phase Two crash cycle {failed.Cycle} failed: {failed.Failure}");
                    }
                }
            }
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            PrintSummary(report, options.OutputPath);
            if (!options.KeepData)
            {
                TryDelete(runRoot);
            }
            else
            {
                Console.WriteLine($"Run data retained at: {runRoot}");
            }
        }

        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    public static async Task RunCrashWriterAsync(string[] args)
    {
        var root = PhaseTwoOptions.ParseString(args, "--root")
            ?? throw new ArgumentException("Phase Two crash writer requires --root.");
        var acknowledgementPath = PhaseTwoOptions.ParseString(args, "--ack")
            ?? throw new ArgumentException("Phase Two crash writer requires --ack.");
        var stripeCount = int.Parse(
            PhaseTwoOptions.ParseString(args, "--stripes") ?? "8",
            CultureInfo.InvariantCulture);
        var payloadBytes = int.Parse(
            PhaseTwoOptions.ParseString(args, "--payload-bytes") ?? "512",
            CultureInfo.InvariantCulture);
        var concurrency = int.Parse(
            PhaseTwoOptions.ParseString(args, "--concurrency") ?? "32",
            CultureInfo.InvariantCulture);
        var queueCapacity = int.Parse(
            PhaseTwoOptions.ParseString(args, "--queue-capacity") ?? "1024",
            CultureInfo.InvariantCulture);
        var checkpointGenerations = int.Parse(
            PhaseTwoOptions.ParseString(args, "--checkpoint-generations") ?? "2",
            CultureInfo.InvariantCulture);
        var options = new SegmentCoreOptions
        {
            CheckpointGenerationsToKeep = checkpointGenerations,
            LogOptions = new StripedObjectLogProbeOptions
            {
                StripeCount = stripeCount,
                MaximumBatchRecords = 64,
                QueueCapacityPerStripe = queueCapacity,
                MaxSegmentBytes = 4 * 1024,
                DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
            }
        };
        await using var store = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
        if (store.Statistics.CheckpointGeneration == 0)
        {
            for (var index = 0; index < 32; index++)
            {
                var seed = CreateRecord(index, "checkpoint-base", payloadBytes);
                await store.SaveAsync(seed.Id, PhaseZeroCodec.Encode(seed)).ConfigureAwait(false);
            }

            _ = await store.CheckpointAsync().ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(acknowledgementPath))!);
        var next = 0;
        while (true)
        {
            var first = next;
            var writes = new Task[concurrency];
            for (var index = 0; index < writes.Length; index++)
            {
                var ordinal = next++;
                var record = CreateRecord(ordinal, "indexed-crash", payloadBytes);
                writes[index] = store.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).AsTask();
            }

            await Task.WhenAll(writes).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                acknowledgementPath,
                (first + writes.Length).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<StoreSpecification> CreateSpecifications(PhaseTwoOptions options)
    {
        var result = new List<StoreSpecification>();
        foreach (var stripeCount in options.StripeCounts)
        {
            result.Add(new StoreSpecification(
                $"SegmentCore ({stripeCount} stripes)",
                root => new SegmentCorePhaseTwoStore(
                    root,
                    stripeCount,
                    options.MaxSegmentBytes,
                    options.QueueCapacityPerStripe,
                    options.CheckpointGenerationsToKeep)));
        }

        if (options.CustomOnly)
        {
            return result;
        }

        if (!options.DapperOnly)
        {
            result.Add(new StoreSpecification(
                "SQLite Microsoft.Data.Sqlite FULL",
                root => new ProductionGatePhaseTwoStore(new SqliteGateStore(
                    Path.Combine(root, "phase-two.sqlite"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.MicrosoftDataSqlite,
                    createSecondaryIndexes: false))));
        }

        if (options.IncludeDapper || options.DapperOnly)
        {
            result.Add(new StoreSpecification(
                "SQLite Dapper FULL",
                root => new ProductionGatePhaseTwoStore(new SqliteGateStore(
                    Path.Combine(root, "phase-two-dapper.sqlite"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.Dapper,
                    createSecondaryIndexes: false))));
        }
        if (options.IncludeCurrentProvider)
        {
            result.Add(new StoreSpecification(
                "Current InterfaceDB file provider",
                root => new ProductionGatePhaseTwoStore(new InterfaceDbGateStore(
                    root,
                    StorageObjectCacheMode.MetadataOnly,
                    StorageDurabilityMode.Durable,
                    useSearchIndexes: false,
                    useSidecarWriteLocks: true))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<PhaseTwoMetric>> MeasureCaseAsync(
        BenchmarkCase benchmarkCase,
        string runRoot,
        PhaseTwoOptions options)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"{benchmarkCase.Specification.Name}; payload {benchmarkCase.PayloadBytes:N0} B; concurrency {benchmarkCase.Concurrency}");
        var accumulators = new Dictionary<string, MetricAccumulator>(StringComparer.Ordinal);
        PhaseTwoStorageStats lastStorage = default;
        string? engineName = null;
        var stripeCount = 0;

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var storeRoot = Path.Combine(
                runRoot,
                "metrics",
                Sanitize(benchmarkCase.Specification.Name),
                $"p{benchmarkCase.PayloadBytes:D8}-c{benchmarkCase.Concurrency:D3}-i{iteration:D2}");
            Directory.CreateDirectory(storeRoot);
            await using var store = benchmarkCase.Specification.Create(storeRoot);
            await store.InitializeAsync().ConfigureAwait(false);
            engineName = store.EngineName;
            stripeCount = store.StripeCount;
            var warmupRecords = CreateRecords(
                Math.Max(32, benchmarkCase.Concurrency * 2),
                $"warmup-{iteration}",
                benchmarkCase.PayloadBytes);
            _ = await MeasureAsync(
                warmupRecords.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    await store.SaveAsync(warmupRecords[index]).ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
            var records = CreateRecords(
                options.Records,
                $"phase2-{iteration}",
                benchmarkCase.PayloadBytes);
            var expected = warmupRecords
                .Concat(records)
                .ToDictionary(static record => record.Id, StringComparer.Ordinal);

            Add(accumulators, "Save", await MeasureAsync(
                records.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    await store.SaveAsync(records[index]).ConfigureAwait(false);
                    return records[index].MaterializedChecksum;
                }).ConfigureAwait(false));

            await AddCheckpointAsync(accumulators, store).ConfigureAwait(false);
            foreach (var record in records.Take(Math.Min(64, records.Length)))
            {
                var warm = await store.GetAsync(record.Id).ConfigureAwait(false);
                if (warm.Count != 1 || warm.Checksum != record.MaterializedChecksum)
                {
                    throw new InvalidDataException($"Warm exact read failed for '{record.Id}'.");
                }
            }

            var readKeys = CreateReadKeys(expected.Keys.ToArray(), options.ReadOperations, seed: 19 + iteration);
            Add(accumulators, "GetWarm", await MeasureAsync(
                readKeys.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    var result = await store.GetAsync(readKeys[index]).ConfigureAwait(false);
                    if (result.Count != 1)
                    {
                        throw new InvalidDataException($"Exact read failed for '{readKeys[index]}'.");
                    }

                    return result.Checksum;
                }).ConfigureAwait(false));

            var updateCount = Math.Max(1, records.Length / 4);
            var updates = records.Take(updateCount)
                .Select(record => record with { Age = record.Age + 1, Revision = 1 })
                .ToArray();
            Add(accumulators, "Update", await MeasureAsync(
                updates.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    if (!await store.UpdateAsync(updates[index]).ConfigureAwait(false))
                    {
                        throw new InvalidDataException($"Update failed for '{updates[index].Id}'.");
                    }

                    return updates[index].MaterializedChecksum;
                }).ConfigureAwait(false));
            foreach (var update in updates)
            {
                expected[update.Id] = update;
            }

            var deleteCount = Math.Max(1, records.Length / 10);
            var deleteKeys = records.TakeLast(deleteCount).Select(static record => record.Id).ToArray();
            Add(accumulators, "Delete", await MeasureAsync(
                deleteKeys.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    if (!await store.DeleteAsync(deleteKeys[index]).ConfigureAwait(false))
                    {
                        throw new InvalidDataException($"Delete failed for '{deleteKeys[index]}'.");
                    }

                    return 1;
                }).ConfigureAwait(false));
            foreach (var key in deleteKeys)
            {
                expected.Remove(key);
            }

            await AddCheckpointAsync(accumulators, store).ConfigureAwait(false);
            Add(accumulators, "Reopen", await MeasureAsync(
                1,
                1,
                async _ =>
                {
                    await store.ReopenAsync().ConfigureAwait(false);
                    return store.Count;
                }).ConfigureAwait(false));

            var reopenedKeys = CreateReadKeys(expected.Keys.ToArray(), options.ReadOperations, seed: 37 + iteration);
            Add(accumulators, "GetAfterReopen", await MeasureAsync(
                reopenedKeys.Length,
                benchmarkCase.Concurrency,
                async index =>
                {
                    var result = await store.GetAsync(reopenedKeys[index]).ConfigureAwait(false);
                    if (result.Count != 1)
                    {
                        throw new InvalidDataException($"Post-reopen read failed for '{reopenedKeys[index]}'.");
                    }

                    return result.Checksum;
                }).ConfigureAwait(false));

            var mixed = BuildMixedOperations(records, expected, options.MixedOperations, iteration, benchmarkCase.PayloadBytes);
            Add(accumulators, "Mixed80R10U5I5D", await MeasureAsync(
                mixed.Length,
                benchmarkCase.Concurrency,
                index => ExecuteMixedAsync(store, mixed[index])).ConfigureAwait(false));

            var snapshot = await store.SnapshotAsync().ConfigureAwait(false);
            if (snapshot.Count != expected.Count)
            {
                throw new InvalidDataException($"Snapshot has {snapshot.Count:N0} objects; expected {expected.Count:N0}.");
            }

            foreach (var pair in expected)
            {
                if (!snapshot.TryGetValue(pair.Key, out var actual)
                    || actual.MaterializedChecksum != pair.Value.MaterializedChecksum)
                {
                    throw new InvalidDataException($"Final materialized object mismatch for '{pair.Key}'.");
                }
            }

            lastStorage = store.GetStorageStats();
            Console.WriteLine(
                $"  iteration {iteration + 1}/{options.Iterations}: save {accumulators["Save"].LastOperationsPerSecond,10:N1} ops/s; "
                + $"warm get {accumulators["GetWarm"].LastOperationsPerSecond,10:N1}; mixed {accumulators["Mixed80R10U5I5D"].LastOperationsPerSecond,10:N1}");
        }

        return accumulators
            .Select(pair => pair.Value.ToMetric(
                engineName!,
                stripeCount,
                benchmarkCase.PayloadBytes,
                benchmarkCase.Concurrency,
                pair.Key,
                lastStorage))
            .OrderBy(static metric => metric.Operation, StringComparer.Ordinal)
            .ToArray();
    }

    private static MixedOperation[] BuildMixedOperations(
        IReadOnlyList<GateRecord> original,
        Dictionary<string, GateRecord> expected,
        int operationCount,
        int iteration,
        int payloadBytes)
    {
        var operations = new MixedOperation[operationCount];
        var updateCandidates = original.Take(Math.Max(1, original.Count / 2)).ToArray();
        var liveForReads = updateCandidates.Select(static record => record.Id).ToArray();
        var deleteCandidates = original
            .Skip(Math.Max(1, original.Count / 2))
            .Where(record => expected.ContainsKey(record.Id))
            .Reverse()
            .ToArray();
        var updates = 0;
        var inserts = 0;
        var deletes = 0;
        for (var index = 0; index < operations.Length; index++)
        {
            switch (index % 20)
            {
                case 0:
                {
                    var record = CreateRecord(inserts++, $"mixed-new-{iteration}", payloadBytes);
                    operations[index] = new MixedOperation(MixedKind.Insert, record.Id, record);
                    expected.Add(record.Id, record);
                    break;
                }
                case 1:
                {
                    if (deletes >= deleteCandidates.Length)
                    {
                        goto default;
                    }

                    var record = deleteCandidates[deletes++];
                    operations[index] = new MixedOperation(MixedKind.Delete, record.Id, null);
                    expected.Remove(record.Id);
                    break;
                }
                case 2:
                case 3:
                {
                    var source = updateCandidates[updates % updateCandidates.Length];
                    var updated = source with { Age = source.Age + 2, Revision = 2 + updates };
                    updates++;
                    operations[index] = new MixedOperation(MixedKind.Update, updated.Id, updated);
                    expected[updated.Id] = updated;
                    break;
                }
                default:
                {
                    var key = liveForReads[(int)(((long)index * 7919) % liveForReads.Length)];
                    operations[index] = new MixedOperation(MixedKind.Read, key, null);
                    break;
                }
            }
        }

        return operations;
    }

    private static async ValueTask<long> ExecuteMixedAsync(IPhaseTwoStore store, MixedOperation operation)
    {
        switch (operation.Kind)
        {
            case MixedKind.Read:
            {
                var result = await store.GetAsync(operation.Key).ConfigureAwait(false);
                if (result.Count != 1)
                {
                    throw new InvalidDataException($"Mixed exact read failed for '{operation.Key}'.");
                }

                return result.Checksum;
            }
            case MixedKind.Update:
                return await store.UpdateAsync(operation.Record!).ConfigureAwait(false) ? 1 : throw new InvalidDataException("Mixed update failed.");
            case MixedKind.Insert:
                await store.SaveAsync(operation.Record!).ConfigureAwait(false);
                return 1;
            case MixedKind.Delete:
                return await store.DeleteAsync(operation.Key).ConfigureAwait(false) ? 1 : throw new InvalidDataException("Mixed delete failed.");
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async ValueTask AddCheckpointAsync(
        Dictionary<string, MetricAccumulator> accumulators,
        IPhaseTwoStore store)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var checkpoint = await store.CheckpointAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (checkpoint.Bytes <= 0)
        {
            return;
        }

        var result = new WorkloadResult(
            1,
            elapsed,
            [elapsed * 1000d],
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            checkpoint.Bytes);
        Add(accumulators, "Checkpoint", result);
    }

    private static async Task<WorkloadResult> MeasureAsync(
        int operations,
        int concurrency,
        Func<int, ValueTask<long>> operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var latency = new double[operations];
        long checksum = 0;
        var next = -1;
        var started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, Math.Min(concurrency, operations))
            .Select(_ => WorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Array.Sort(latency);
        return new WorkloadResult(
            operations,
            wall,
            latency,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            checksum);

        async Task WorkerAsync()
        {
            await Task.Yield();
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= operations)
                {
                    return;
                }

                var operationStarted = Stopwatch.GetTimestamp();
                var result = await operation(index).ConfigureAwait(false);
                latency[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMicroseconds;
                Interlocked.Add(ref checksum, result);
            }
        }
    }

    private static void Add(
        Dictionary<string, MetricAccumulator> accumulators,
        string operation,
        WorkloadResult workload)
    {
        if (!accumulators.TryGetValue(operation, out var accumulator))
        {
            accumulator = new MetricAccumulator();
            accumulators.Add(operation, accumulator);
        }

        accumulator.Add(workload);
    }

    private static GateRecord[] CreateRecords(int count, string prefix, int contentBytes) =>
        Enumerable.Range(0, count).Select(ordinal => CreateRecord(ordinal, prefix, contentBytes)).ToArray();

    private static GateRecord CreateRecord(int ordinal, string prefix, int contentBytes)
    {
        var baseline = GateRecord.Create(ordinal, prefix);
        var marker = baseline.Content + "|";
        return baseline with
        {
            Content = marker + new string((char)('a' + ordinal % 26), Math.Max(0, contentBytes - marker.Length))
        };
    }

    private static string[] CreateReadKeys(IReadOnlyList<string> keys, int count, int seed)
    {
        var random = new Random(seed);
        var result = new string[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = keys[random.Next(keys.Count)];
        }

        return result;
    }

    private static void Analyze(PhaseTwoReport report)
    {
        foreach (var payload in report.Options.PayloadBytes)
        {
            foreach (var concurrency in report.Options.ConcurrencyLevels)
            {
                foreach (var operation in new[] { "Save", "GetWarm", "GetAfterReopen", "Update", "Delete", "Mixed80R10U5I5D" })
                {
                    var custom = report.Metrics
                        .Where(metric => metric.Engine == "SegmentCore"
                            && metric.PayloadBytes == payload
                            && metric.Concurrency == concurrency
                            && metric.Operation == operation)
                        .OrderByDescending(static metric => metric.OperationsPerSecond)
                        .FirstOrDefault();
                    if (custom is null)
                    {
                        continue;
                    }

                    var sqlite = report.Metrics
                        .Where(metric => metric.Engine.StartsWith("SQLite", StringComparison.Ordinal)
                            && metric.PayloadBytes == payload
                            && metric.Concurrency == concurrency
                            && metric.Operation == operation)
                        .OrderByDescending(static metric => metric.OperationsPerSecond)
                        .FirstOrDefault();
                    if (sqlite is not null)
                    {
                        report.Findings.Add(
                            $"{operation}, {payload:N0} B, concurrency {concurrency}: custom {custom.StripeCount} stripes {custom.OperationsPerSecond:N1} ops/s vs fastest SQLite ({sqlite.Engine}) {sqlite.OperationsPerSecond:N1} ops/s ({Ratio(custom.OperationsPerSecond, sqlite.OperationsPerSecond):N1}x)." );
                    }
                }
            }
        }

        var checkpoints = report.Metrics.Where(static metric => metric.Operation == "Checkpoint").ToArray();
        if (checkpoints.Length > 0)
        {
            report.Findings.Add(
                $"Primary-index checkpoint median latency across measured configurations was {Percentile(checkpoints.Select(static metric => metric.P50Microseconds).Order().ToArray(), 0.50) / 1000d:N3} ms.");
        }
    }

    private static async Task<IReadOnlyList<PhaseTwoCrashMetric>> RunCrashCyclesAsync(
        string runRoot,
        PhaseTwoOptions options,
        int stripeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"Checkpoint-tail forced-termination recovery: {options.CrashCycles} cycles, {stripeCount} stripes");
        var metrics = new List<PhaseTwoCrashMetric>();
        for (var cycle = 1; cycle <= options.CrashCycles; cycle++)
        {
            var root = Path.Combine(runRoot, "crash", cycle.ToString("D3", CultureInfo.InvariantCulture));
            var ackPath = Path.Combine(root, "acknowledged.txt");
            Directory.CreateDirectory(root);
            using var process = StartCrashWriter(
                root,
                ackPath,
                stripeCount,
                options.PayloadBytes.Min(),
                options.QueueCapacityPerStripe,
                options.CheckpointGenerationsToKeep);
            var acknowledged = await WaitForAcknowledgementAsync(process, ackPath, 64, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (acknowledged < 64)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                metrics.Add(new PhaseTwoCrashMetric(cycle, acknowledged, 0, 0, 0, false, "Writer did not acknowledge 64 tail records."));
                continue;
            }

            await Task.Delay(Random.Shared.Next(1, 15)).ConfigureAwait(false);
            acknowledged = Math.Max(acknowledged, TryReadAcknowledgement(ackPath));
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var reopened = 0;
            var started = Stopwatch.GetTimestamp();
            try
            {
                var probeOptions = new SegmentCoreOptions
                {
                    CheckpointGenerationsToKeep = options.CheckpointGenerationsToKeep,
                    LogOptions = new StripedObjectLogProbeOptions
                    {
                        StripeCount = stripeCount,
                        MaximumBatchRecords = 64,
                        QueueCapacityPerStripe = options.QueueCapacityPerStripe,
                        MaxSegmentBytes = 4 * 1024,
                        DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
                    }
                };
                await using var store = await SegmentCoreStore.OpenAsync(root, probeOptions).ConfigureAwait(false);
                for (var ordinal = 0; ordinal < acknowledged; ordinal++)
                {
                    var key = CreateRecord(ordinal, "indexed-crash", options.PayloadBytes.Min()).Id;
                    var payload = await store.GetAsync(key).ConfigureAwait(false);
                    if (payload is not null && PhaseZeroCodec.Decode(payload).Id == key)
                    {
                        reopened++;
                    }
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var passed = reopened == acknowledged;
                metrics.Add(new PhaseTwoCrashMetric(
                    cycle,
                    acknowledged,
                    reopened,
                    store.Statistics.TailRecordsReplayed,
                    elapsed,
                    passed,
                    passed ? null : "One or more acknowledged post-checkpoint records were missing or invalid."));
                Console.WriteLine(
                    $"  cycle {cycle,2}: ack {acknowledged,6}; verified {reopened,6}; tail replay {store.Statistics.TailRecordsReplayed,6}; {(passed ? "PASS" : "FAIL")}");
            }
            catch (Exception exception)
            {
                metrics.Add(new PhaseTwoCrashMetric(
                    cycle,
                    acknowledged,
                    reopened,
                    0,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    false,
                    $"{exception.GetType().Name}: {exception.Message}"));
            }
        }

        return metrics;
    }

    private static Process StartCrashWriter(
        string root,
        string ackPath,
        int stripes,
        int payloadBytes,
        int queueCapacity,
        int checkpointGenerations)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve process path.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in new[]
                 {
                     "--phase-two-crash-writer", "--root", root, "--ack", ackPath,
                     "--stripes", stripes.ToString(CultureInfo.InvariantCulture),
                     "--payload-bytes", payloadBytes.ToString(CultureInfo.InvariantCulture),
                     "--concurrency", "32",
                     "--queue-capacity", queueCapacity.ToString(CultureInfo.InvariantCulture),
                     "--checkpoint-generations", checkpointGenerations.ToString(CultureInfo.InvariantCulture)
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start Phase Two crash writer.");
    }

    private static async Task<int> WaitForAcknowledgementAsync(Process process, string path, int minimum, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        var acknowledged = 0;
        while (timer.Elapsed < timeout)
        {
            acknowledged = Math.Max(acknowledged, TryReadAcknowledgement(path));
            if (acknowledged >= minimum || process.HasExited)
            {
                return acknowledged;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return acknowledged;
    }

    private static int TryReadAcknowledgement(string path)
    {
        try
        {
            return File.Exists(path)
                && int.TryParse(File.ReadAllText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task WriteReportAsync(PhaseTwoReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.ChangeExtension(fullPath, ".md"), BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static string BuildMarkdown(PhaseTwoReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# InterfaceDB SegmentCore prototype");
        builder.AppendLine();
        builder.AppendLine($"- Correctness result: **{(report.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Platform: {report.OperatingSystem} ({report.Architecture})");
        builder.AppendLine($"- Runtime: {report.Runtime}");
        builder.AppendLine($"- Physical disk: {report.PhysicalDisk}");
        builder.AppendLine($"- Started: {report.StartedUtc:O}");
        builder.AppendLine($"- Completed: {report.CompletedUtc:O}");
        builder.AppendLine();
        builder.AppendLine("## Performance");
        builder.AppendLine();
        builder.AppendLine("| Engine | stripes | payload B | concurrency | operation | ops/s | p50 us | p95 us | p99 us | B/op | storage B | files |");
        builder.AppendLine("|---|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var metric in report.Metrics
                     .OrderBy(static metric => metric.PayloadBytes)
                     .ThenBy(static metric => metric.Concurrency)
                     .ThenBy(static metric => metric.Operation)
                     .ThenBy(static metric => metric.Engine))
        {
            builder.AppendLine(
                $"| {metric.Engine} | {metric.StripeCount} | {metric.PayloadBytes} | {metric.Concurrency} | {metric.Operation} | "
                + $"{metric.OperationsPerSecond:N1} | {metric.P50Microseconds:N1} | {metric.P95Microseconds:N1} | {metric.P99Microseconds:N1} | "
                + $"{metric.AllocatedBytesPerOperation:N0} | {metric.StorageBytes} | {metric.StorageFiles} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }

        builder.AppendLine();
        builder.AppendLine("## Forced termination after checkpoint");
        builder.AppendLine();
        builder.AppendLine("| cycle | acknowledged | verified | tail replayed | reopen ms | result |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---|");
        foreach (var crash in report.CrashMetrics)
        {
            builder.AppendLine(
                $"| {crash.Cycle} | {crash.AcknowledgedRecords} | {crash.RecoveredAcknowledgedRecords} | "
                + $"{crash.TailRecordsReplayed} | {crash.ReopenMilliseconds:N3} | {(crash.Passed ? "PASS" : "FAIL")} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Method notes");
        builder.AppendLine();
        foreach (var note in report.Notes)
        {
            builder.AppendLine($"- {note}");
        }

        if (report.Failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failures");
            builder.AppendLine();
            foreach (var failure in report.Failures)
            {
                builder.AppendLine($"- {failure}");
            }
        }

        return builder.ToString();
    }

    private static void PrintHeader(PhaseTwoReport report)
    {
        Console.WriteLine("InterfaceDB Phase Two SegmentCore prototype");
        Console.WriteLine($"Runtime: {report.Runtime}");
        Console.WriteLine($"OS: {report.OperatingSystem}");
        Console.WriteLine($"Disk: {report.PhysicalDisk}");
    }

    private static void PrintSummary(PhaseTwoReport report, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine($"Phase Two result: {(report.Passed ? "PASS" : "FAIL")}");
        foreach (var finding in report.Findings.Take(20))
        {
            Console.WriteLine($"  {finding}");
        }

        Console.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static double Ratio(double candidate, double baseline) => baseline <= 0 ? 0 : candidate / baseline;

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character)
            ? '-'
            : char.ToLowerInvariant(character)).ToArray());
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Benchmark cleanup is best effort.
        }
    }

    private sealed record StoreSpecification(string Name, Func<string, IPhaseTwoStore> Create);

    private sealed record BenchmarkCase(StoreSpecification Specification, int PayloadBytes, int Concurrency);

    private sealed record MixedOperation(MixedKind Kind, string Key, GateRecord? Record);

    private enum MixedKind
    {
        Read,
        Update,
        Insert,
        Delete
    }

    private sealed record WorkloadResult(
        int Operations,
        double WallMilliseconds,
        IReadOnlyList<double> LatencyMicroseconds,
        long AllocatedBytes,
        long Checksum);

    private sealed class MetricAccumulator
    {
        private readonly List<double> _latency = [];
        private int _operations;
        private double _wallMilliseconds;
        private long _allocatedBytes;
        private long _checksum;

        public double LastOperationsPerSecond { get; private set; }

        public void Add(WorkloadResult workload)
        {
            _operations += workload.Operations;
            _wallMilliseconds += workload.WallMilliseconds;
            _allocatedBytes += workload.AllocatedBytes;
            _checksum += workload.Checksum;
            _latency.AddRange(workload.LatencyMicroseconds);
            LastOperationsPerSecond = workload.WallMilliseconds <= 0
                ? 0
                : workload.Operations * 1000d / workload.WallMilliseconds;
        }

        public PhaseTwoMetric ToMetric(
            string engine,
            int stripeCount,
            int payloadBytes,
            int concurrency,
            string operation,
            PhaseTwoStorageStats storage)
        {
            _latency.Sort();
            return new PhaseTwoMetric(
                engine,
                stripeCount,
                payloadBytes,
                concurrency,
                operation,
                _operations,
                _wallMilliseconds,
                _wallMilliseconds <= 0 ? 0 : _operations * 1000d / _wallMilliseconds,
                Percentile(_latency, 0.50),
                Percentile(_latency, 0.95),
                Percentile(_latency, 0.99),
                _operations == 0 ? 0 : (double)_allocatedBytes / _operations,
                _checksum,
                storage.Bytes,
                storage.Files);
        }
    }
}
