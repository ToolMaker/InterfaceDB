using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class PhaseOneRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = PhaseOneOptions.Parse(args);
        var started = DateTimeOffset.UtcNow;
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbPhaseOne",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new PhaseOneReport
        {
            StartedUtc = started,
            CompletedUtc = started,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? Environment.GetEnvironmentVariable("HOSTTYPE")
                ?? "unknown",
            WorkingDrive = Path.GetPathRoot(runRoot) ?? "unknown",
            PhysicalDisk = Environment.GetEnvironmentVariable("INTERFACEDB_PHASE_ONE_DISK")
                ?? "not supplied by runner",
            Options = options
        };
        report.Notes.Add(
            "Every caller awaits its own durable acknowledgement. The striped engine may group concurrently submitted records behind one flush per affected stripe; no caller is acknowledged before that flush completes.");
        report.Notes.Add(
            "Object construction is outside the timed operation. Complete PhaseZeroCodec serialization is inside every custom-log operation; SQLite binds every materialized object field and autocommits each object.");
        report.Notes.Add(
            "SQLite uses WAL, synchronous=FULL, one object per autocommit, and no secondary indexes. InterfaceDB uses its current durable file provider without search indexes.");
        report.Notes.Add(
            "One stripe is the safe single-active-log group-commit control. Additional stripes add independent append queues, segment chains, and durability barriers.");
        report.Notes.Add(
            "The test intentionally does not use an unsafe free-running offset allocator: without a separately durable commit watermark, out-of-order tail writes cannot distinguish an uncommitted hole from corruption after a crash.");

        try
        {
            PrintHeader(report);
            var cases = CreateSpecifications(options)
                .SelectMany(specification => options.PayloadBytes.SelectMany(payloadBytes =>
                    options.ConcurrencyLevels.Select(concurrency =>
                        new PhaseOneBenchmarkCase(specification, payloadBytes, concurrency))))
                .ToList();
            if (options.InterleaveCases)
            {
                var random = new Random(260824);
                for (var index = cases.Count - 1; index > 0; index--)
                {
                    var swap = random.Next(index + 1);
                    (cases[index], cases[swap]) = (cases[swap], cases[index]);
                }
            }

            foreach (var benchmarkCase in cases)
            {
                try
                {
                    report.Metrics.Add(await MeasureAsync(
                        benchmarkCase.Specification,
                        runRoot,
                        options,
                        benchmarkCase.PayloadBytes,
                        benchmarkCase.Concurrency).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    var failure = $"{benchmarkCase.Specification.Name}, {benchmarkCase.PayloadBytes:N0} B, concurrency {benchmarkCase.Concurrency}: "
                        + $"{exception.GetType().Name}: {exception.Message}";
                    report.Failures.Add(failure);
                    Console.WriteLine($"  FAIL {failure}");
                }
            }

            Analyze(report);
            if (options.CrashCycles > 0 && report.SelectedCrashStripeCount > 0)
            {
                report.CrashMetrics.AddRange(await RunCrashRecoveryCyclesAsync(
                    runRoot,
                    options,
                    report.SelectedCrashStripeCount,
                    Enum.Parse<ObjectLogDurabilityStrategy>(report.SelectedCrashDurability)).ConfigureAwait(false));
                foreach (var failure in report.CrashMetrics.Where(static metric => !metric.Passed))
                {
                    report.Failures.Add(
                        $"Crash cycle {failure.Cycle} failed: {failure.Failure ?? "unknown failure"}");
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
                TryDeleteRunRoot(runRoot);
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
        var root = PhaseOneOptions.ParseString(args, "--root")
            ?? throw new ArgumentException("Phase One crash writer requires --root.");
        var acknowledgementPath = PhaseOneOptions.ParseString(args, "--ack")
            ?? throw new ArgumentException("Phase One crash writer requires --ack.");
        var payloadBytes = int.Parse(
            PhaseOneOptions.ParseString(args, "--payload-bytes") ?? "512",
            CultureInfo.InvariantCulture);
        var stripeCount = int.Parse(
            PhaseOneOptions.ParseString(args, "--stripes") ?? "8",
            CultureInfo.InvariantCulture);
        var concurrency = int.Parse(
            PhaseOneOptions.ParseString(args, "--concurrency") ?? "32",
            CultureInfo.InvariantCulture);
        var maxSegmentBytes = long.Parse(
            PhaseOneOptions.ParseString(args, "--segment-bytes") ?? "4096",
            CultureInfo.InvariantCulture);
        var durabilityStrategy = Enum.Parse<ObjectLogDurabilityStrategy>(
            PhaseOneOptions.ParseString(args, "--durability") ?? "ExplicitFlush",
            ignoreCase: true);

        var options = new StripedObjectLogProbeOptions
        {
            StripeCount = stripeCount,
            MaximumBatchRecords = 64,
            MaxSegmentBytes = maxSegmentBytes,
            DurabilityStrategy = durabilityStrategy
        };
        await using var writer = await StripedObjectLogProbeWriter.OpenAsync(root, options).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(acknowledgementPath))!);

        var next = 0;
        while (true)
        {
            var first = next;
            var writes = new Task<StripedObjectLogRecordAddress>[concurrency];
            for (var index = 0; index < writes.Length; index++)
            {
                var ordinal = next++;
                var record = CreateRecord(ordinal, "striped-crash", payloadBytes);
                writes[index] = writer.AppendAsync(record.Id, PhaseZeroCodec.Encode(record)).AsTask();
            }

            _ = await Task.WhenAll(writes).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                acknowledgementPath,
                (first + writes.Length).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<PhaseOneStoreSpecification> CreateSpecifications(PhaseOneOptions options)
    {
        var specifications = new List<PhaseOneStoreSpecification>();
        var durabilityStrategies = options.WriteThroughOnly
            ? [ObjectLogDurabilityStrategy.WriteThrough]
            : options.IncludeWriteThrough
            ? new[] { ObjectLogDurabilityStrategy.ExplicitFlush, ObjectLogDurabilityStrategy.WriteThrough }
            : [ObjectLogDurabilityStrategy.ExplicitFlush];
        foreach (var durabilityStrategy in durabilityStrategies)
        {
            foreach (var stripes in options.StripeCounts)
            {
                specifications.Add(new PhaseOneStoreSpecification(
                    $"Striped object log {durabilityStrategy} ({stripes} stripe{(stripes == 1 ? string.Empty : "s")})",
                    root => new StripedObjectLogPhaseOneStore(
                        root,
                        new StripedObjectLogProbeOptions
                        {
                            StripeCount = stripes,
                            MaximumBatchRecords = options.MaximumBatchRecords,
                            GroupCommitDelay = TimeSpan.FromMicroseconds(options.GroupCommitDelayMicroseconds),
                            MaxSegmentBytes = options.MaxSegmentBytes,
                            DurabilityStrategy = durabilityStrategy
                        })));
            }
        }

        if (!options.IncludeBaselines)
        {
            return specifications;
        }

        specifications.Add(new PhaseOneStoreSpecification(
            "Original serial checksummed object log",
            root => new PhaseOneBaselineStore(new ObjectLogPhaseZeroStore(
                root,
                new ObjectLogProbeOptions
                {
                    DurabilityMode = ObjectLogDurabilityMode.Durable,
                    FlushOnDispose = false,
                    MaxSegmentBytes = options.MaxSegmentBytes
                }))));
        specifications.Add(new PhaseOneStoreSpecification(
            "SQLite Microsoft.Data.Sqlite FULL",
            root => new PhaseOneBaselineStore(new ProductionGatePhaseZeroStore(
                new SqliteGateStore(
                    Path.Combine(root, "phase-one.sqlite"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.MicrosoftDataSqlite,
                    createSecondaryIndexes: false),
                "complete object bind + INSERT + autocommit"))));
        specifications.Add(new PhaseOneStoreSpecification(
            "Current InterfaceDB file provider",
            root => new PhaseOneBaselineStore(new ProductionGatePhaseZeroStore(
                new InterfaceDbGateStore(
                    root,
                    StorageObjectCacheMode.MetadataOnly,
                    StorageDurabilityMode.Durable,
                    useSearchIndexes: false,
                    useSidecarWriteLocks: true),
                "complete object save; passthrough index manager"))));
        return specifications;
    }

    private static async Task<PhaseOneMetric> MeasureAsync(
        PhaseOneStoreSpecification specification,
        string runRoot,
        PhaseOneOptions options,
        int payloadBytes,
        int concurrency)
    {
        Console.WriteLine();
        Console.WriteLine($"{specification.Name}; payload {payloadBytes:N0} B; concurrency {concurrency}");
        var samples = new List<double>(options.Records * options.Iterations);
        var totalWallMilliseconds = 0d;
        var totalCpuMilliseconds = 0d;
        long totalAllocatedBytes = 0;
        long totalDurableBatches = 0;
        long totalDurabilityFlushes = 0;
        var largestBatch = 0;
        PhaseOneStoreStatistics lastStatistics = default;
        var lastVerifiedCount = 0;
        long lastVerifiedChecksum = 0;
        string? engineName = null;
        string? durability = null;
        var stripeCount = 0;

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var storeRoot = Path.Combine(
                runRoot,
                "metrics",
                Sanitize(specification.Name),
                $"p{payloadBytes:D8}-c{concurrency:D3}-i{iteration:D2}");
            Directory.CreateDirectory(storeRoot);
            await using var store = specification.Create(storeRoot);
            await store.InitializeAsync().ConfigureAwait(false);
            engineName = store.EngineName;
            durability = store.Durability;
            stripeCount = store.StripeCount;

            var warmupCount = Math.Max(8, concurrency * 2);
            var warmup = CreateRecords(warmupCount, $"warmup-{iteration}", payloadBytes);
            _ = await RunConcurrentWritesAsync(store, warmup, concurrency, captureLatency: false)
                .ConfigureAwait(false);

            var measured = CreateRecords(options.Records, $"measured-{iteration}", payloadBytes);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var workload = await RunConcurrentWritesAsync(
                store,
                measured,
                concurrency,
                captureLatency: true).ConfigureAwait(false);
            totalAllocatedBytes += GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            totalCpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            totalWallMilliseconds += workload.WallMilliseconds;
            samples.AddRange(workload.LatencyMicroseconds);

            var verified = await store.VerifyAsync().ConfigureAwait(false);
            var expectedCount = warmup.Length + measured.Length;
            var expectedChecksum = warmup.Sum(static record => record.MaterializedChecksum)
                + measured.Sum(static record => record.MaterializedChecksum);
            if (verified.Count != expectedCount || verified.Checksum != expectedChecksum)
            {
                throw new InvalidDataException(
                    $"Verification returned {verified.Count:N0}/{verified.Checksum:N0}; expected {expectedCount:N0}/{expectedChecksum:N0}.");
            }

            lastVerifiedCount = verified.Count;
            lastVerifiedChecksum = verified.Checksum;
            lastStatistics = store.GetStatistics();
            totalDurableBatches += lastStatistics.DurableBatches;
            totalDurabilityFlushes += lastStatistics.DurabilityFlushes;
            largestBatch = Math.Max(largestBatch, lastStatistics.LargestBatch);
            Console.WriteLine(
                $"  iteration {iteration + 1}/{options.Iterations}: {workload.OperationsPerSecond,12:N1} ops/s; "
                + $"p50 {Percentile(workload.LatencyMicroseconds, 0.50),10:N1} us; "
                + $"p99 {Percentile(workload.LatencyMicroseconds, 0.99),10:N1} us");
        }

        samples.Sort();
        var measuredOperations = checked(options.Records * options.Iterations);
        var verifiedOperations = checked(
            (options.Records + Math.Max(8, concurrency * 2)) * options.Iterations);
        var operationsPerSecond = totalWallMilliseconds <= 0
            ? 0
            : measuredOperations * 1_000d / totalWallMilliseconds;
        var metric = new PhaseOneMetric(
            engineName!,
            durability!,
            stripeCount,
            concurrency,
            payloadBytes,
            samples.Count,
            totalWallMilliseconds,
            operationsPerSecond,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples.Average(),
            measuredOperations == 0 ? 0 : (double)totalAllocatedBytes / measuredOperations,
            totalCpuMilliseconds,
            totalWallMilliseconds <= 0 ? 0 : totalCpuMilliseconds / totalWallMilliseconds * 100d,
            lastStatistics.StorageBytes,
            lastStatistics.StorageFiles,
            payloadBytes == 0 || lastVerifiedCount == 0
                ? 0
                : (double)lastStatistics.StorageBytes / (lastVerifiedCount * payloadBytes),
            totalDurableBatches,
            totalDurabilityFlushes,
            totalDurableBatches == 0 ? 0 : (double)verifiedOperations / totalDurableBatches,
            largestBatch,
            lastVerifiedCount,
            lastVerifiedChecksum);
        Console.WriteLine(
            $"  aggregate: {metric.OperationsPerSecond,12:N1} ops/s; p50 {metric.P50Microseconds,10:N1} us; "
            + $"p95 {metric.P95Microseconds,10:N1} us; p99 {metric.P99Microseconds,10:N1} us; "
            + $"average batch {metric.AverageBatchSize:N1}");
        return metric;
    }

    private static async Task<ConcurrentWorkloadResult> RunConcurrentWritesAsync(
        IPhaseOneStore store,
        IReadOnlyList<GateRecord> records,
        int concurrency,
        bool captureLatency)
    {
        var latency = captureLatency ? new double[records.Count] : [];
        var next = -1;
        var started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, Math.Min(concurrency, records.Count))
            .Select(_ => RunWorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var wallMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (captureLatency)
        {
            Array.Sort(latency);
        }

        return new ConcurrentWorkloadResult(
            wallMilliseconds,
            wallMilliseconds <= 0 ? 0 : records.Count * 1_000d / wallMilliseconds,
            latency);

        async Task RunWorkerAsync()
        {
            // Ensure every logical client is created before any provider can
            // synchronously consume the shared work queue.
            await Task.Yield();
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= records.Count)
                {
                    return;
                }

                var operationStarted = Stopwatch.GetTimestamp();
                await store.AppendAsync(records[index]).ConfigureAwait(false);
                if (captureLatency)
                {
                    latency[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMicroseconds;
                }
            }
        }
    }

    private static GateRecord[] CreateRecords(int count, string prefix, int contentBytes) =>
        Enumerable.Range(0, count)
            .Select(ordinal => CreateRecord(ordinal, prefix, contentBytes))
            .ToArray();

    private static GateRecord CreateRecord(int ordinal, string prefix, int contentBytes)
    {
        var baseline = GateRecord.Create(ordinal, prefix);
        var marker = baseline.Content + "|";
        var padding = new string((char)('a' + ordinal % 26), Math.Max(0, contentBytes - marker.Length));
        return baseline with { Content = marker + padding };
    }

    private static void Analyze(PhaseOneReport report)
    {
        var custom = report.Metrics
            .Where(static metric => metric.Engine == "Striped checksummed object log")
            .ToArray();
        if (custom.Length == 0)
        {
            report.Failures.Add("No striped object-log metrics completed.");
            return;
        }

        var highestConcurrency = report.Options.ConcurrencyLevels.Max();
        var selectionPayload = report.Options.PayloadBytes.Min();
        var selected = custom
            .Where(metric => metric.Concurrency == highestConcurrency && metric.PayloadBytes == selectionPayload)
            .OrderByDescending(static metric => metric.OperationsPerSecond)
            .ThenBy(static metric => metric.P99Microseconds)
            .FirstOrDefault();
        selected ??= custom.OrderByDescending(static metric => metric.OperationsPerSecond).First();
        report.SelectedCrashStripeCount = selected.StripeCount;
        report.SelectedCrashDurability = selected.Durability;
        report.Findings.Add(
            $"The highest-throughput striped configuration at {selectionPayload:N0} B and concurrency {highestConcurrency} used {selected.StripeCount} stripe(s) with {selected.Durability}: "
            + $"{selected.OperationsPerSecond:N1} ops/s, p50 {selected.P50Microseconds:N1} us, p99 {selected.P99Microseconds:N1} us.");

        foreach (var payload in report.Options.PayloadBytes)
        {
            foreach (var concurrency in report.Options.ConcurrencyLevels)
            {
                var candidates = custom
                    .Where(metric => metric.PayloadBytes == payload && metric.Concurrency == concurrency)
                    .OrderByDescending(static metric => metric.OperationsPerSecond)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    continue;
                }

                var best = candidates[0];
                var sqlite = report.Metrics.FirstOrDefault(metric =>
                    metric.Engine.StartsWith("SQLite Microsoft.Data.Sqlite", StringComparison.Ordinal)
                    && metric.PayloadBytes == payload
                    && metric.Concurrency == concurrency);
                var current = report.Metrics.FirstOrDefault(metric =>
                    metric.Engine == "Current InterfaceDB file provider"
                    && metric.PayloadBytes == payload
                    && metric.Concurrency == concurrency);
                var comparison = new StringBuilder(
                    $"{payload:N0} B at concurrency {concurrency}: best custom is {best.StripeCount} stripe(s) with {best.Durability}, {best.OperationsPerSecond:N1} ops/s");
                if (sqlite is not null)
                {
                    comparison.Append($", {ImprovementPercent(sqlite.OperationsPerSecond, best.OperationsPerSecond):+0.0;-0.0;0.0}% throughput versus SQLite");
                }

                if (current is not null)
                {
                    comparison.Append($", {ImprovementPercent(current.OperationsPerSecond, best.OperationsPerSecond):+0.0;-0.0;0.0}% versus the current file provider");
                }

                comparison.Append('.');
                report.Findings.Add(comparison.ToString());
            }
        }
    }

    private static async Task<IReadOnlyList<PhaseOneCrashMetric>> RunCrashRecoveryCyclesAsync(
        string runRoot,
        PhaseOneOptions options,
        int stripeCount,
        ObjectLogDurabilityStrategy durabilityStrategy)
    {
        var metrics = new List<PhaseOneCrashMetric>(options.CrashCycles);
        Console.WriteLine();
        Console.WriteLine(
            $"Forced-termination recovery: {options.CrashCycles} cycles, {stripeCount} stripes, {durabilityStrategy}");
        for (var cycle = 1; cycle <= options.CrashCycles; cycle++)
        {
            var cycleRoot = Path.Combine(runRoot, "crash", cycle.ToString("D3", CultureInfo.InvariantCulture));
            var acknowledgementPath = Path.Combine(cycleRoot, "acknowledged.txt");
            Directory.CreateDirectory(cycleRoot);
            using var process = StartCrashWriter(
                cycleRoot,
                acknowledgementPath,
                options,
                stripeCount,
                durabilityStrategy);
            var acknowledged = await WaitForAcknowledgementAsync(
                process,
                acknowledgementPath,
                minimum: 64,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (acknowledged.Count < 64)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                metrics.Add(new PhaseOneCrashMetric(
                    cycle,
                    stripeCount,
                    acknowledged.Count,
                    0,
                    0,
                    0,
                    false,
                    acknowledged.Failure ?? "Crash writer did not acknowledge 64 records within 30 seconds."));
                continue;
            }

            await Task.Delay(Random.Shared.Next(1, 15)).ConfigureAwait(false);
            var acknowledgedCount = Math.Max(acknowledged.Count, TryReadAcknowledgement(acknowledgementPath));
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var recoveryStarted = Stopwatch.GetTimestamp();
            try
            {
                var recovered = StripedObjectLogRecovery.Recover(cycleRoot, repairActiveTails: true);
                var passed = recovered.Records.Count >= acknowledgedCount;
                for (var ordinal = 0; passed && ordinal < acknowledgedCount; ordinal++)
                {
                    var key = CreateRecord(ordinal, "striped-crash", options.PayloadBytes.Min()).Id;
                    passed = recovered.Records.TryGetValue(key, out var record)
                        && PhaseZeroCodec.Decode(record.Payload).Id == key;
                }

                var elapsed = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
                metrics.Add(new PhaseOneCrashMetric(
                    cycle,
                    stripeCount,
                    acknowledgedCount,
                    recovered.Records.Count,
                    recovered.RepairedTailBytes,
                    elapsed,
                    passed,
                    passed ? null : "One or more acknowledged records were absent or invalid."));
                Console.WriteLine(
                    $"  cycle {cycle,2}: ack {acknowledgedCount,6}; recovered {recovered.Records.Count,6}; "
                    + $"tail repaired {recovered.RepairedTailBytes,6} B; {(passed ? "PASS" : "FAIL")}");
            }
            catch (Exception exception)
            {
                metrics.Add(new PhaseOneCrashMetric(
                    cycle,
                    stripeCount,
                    acknowledgedCount,
                    0,
                    0,
                    Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds,
                    false,
                    $"{exception.GetType().Name}: {exception.Message}"));
                Console.WriteLine($"  cycle {cycle,2}: FAIL {exception.Message}");
            }
        }

        return metrics;
    }

    private static Process StartCrashWriter(
        string root,
        string acknowledgementPath,
        PhaseOneOptions options,
        int stripeCount,
        ObjectLogDurabilityStrategy durabilityStrategy)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the current process executable.");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
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
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--phase-one-crash-writer");
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("--ack");
        startInfo.ArgumentList.Add(acknowledgementPath);
        startInfo.ArgumentList.Add("--payload-bytes");
        startInfo.ArgumentList.Add(options.PayloadBytes.Min().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--stripes");
        startInfo.ArgumentList.Add(stripeCount.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--concurrency");
        startInfo.ArgumentList.Add(Math.Max(32, options.ConcurrencyLevels.Max()).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--segment-bytes");
        startInfo.ArgumentList.Add(Math.Min(options.MaxSegmentBytes, 4L * 1024).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--durability");
        startInfo.ArgumentList.Add(durabilityStrategy.ToString());
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Phase One crash writer.");
    }

    private static async Task<(int Count, string? Failure)> WaitForAcknowledgementAsync(
        Process process,
        string path,
        int minimum,
        TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        var acknowledged = 0;
        while (started.Elapsed < timeout)
        {
            acknowledged = Math.Max(acknowledged, TryReadAcknowledgement(path));
            if (acknowledged >= minimum)
            {
                return (acknowledged, null);
            }

            if (process.HasExited)
            {
                var standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                var standardOutput = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
                return (acknowledged, $"Crash writer exited with code {process.ExitCode}: {detail.Trim()}");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return (acknowledged, null);
    }

    private static int TryReadAcknowledgement(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return int.TryParse(reader.ReadToEnd(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
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

    private static double Percentile(IReadOnlyList<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sortedSamples.Count) - 1;
        return sortedSamples[Math.Clamp(index, 0, sortedSamples.Count - 1)];
    }

    private static double ImprovementPercent(double baseline, double candidate) =>
        baseline <= 0 ? 0 : (candidate - baseline) / baseline * 100d;

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character)
                ? '-'
                : char.ToLowerInvariant(character))
            .ToArray());
    }

    private static async Task WriteReportAsync(PhaseOneReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.ChangeExtension(fullPath, ".md"),
            BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static string BuildMarkdown(PhaseOneReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# InterfaceDB NVMe-striped storage prototype");
        builder.AppendLine();
        builder.AppendLine($"- Correctness result: **{(report.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Platform: {report.OperatingSystem} ({report.Architecture})");
        builder.AppendLine($"- Runtime: {report.Runtime}");
        builder.AppendLine($"- Working drive: {report.WorkingDrive}");
        builder.AppendLine($"- Physical disk: {report.PhysicalDisk}");
        builder.AppendLine($"- Started: {report.StartedUtc:O}");
        builder.AppendLine($"- Completed: {report.CompletedUtc:O}");
        builder.AppendLine();
        builder.AppendLine("## Performance");
        builder.AppendLine();
        builder.AppendLine("| Engine | durability | stripes | concurrency | payload B | ops/s | p50 us | p95 us | p99 us | CPU % core | B/op | avg batch | flushes | files | write amp | verified |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var metric in report.Metrics
                     .OrderBy(static metric => metric.PayloadBytes)
                     .ThenBy(static metric => metric.Concurrency)
                     .ThenBy(static metric => metric.Engine)
                     .ThenBy(static metric => metric.StripeCount))
        {
            builder.AppendLine(
                $"| {metric.Engine} | {metric.Durability} | {metric.StripeCount} | {metric.Concurrency} | {metric.PayloadBytes} | "
                + $"{metric.OperationsPerSecond:N1} | {metric.P50Microseconds:N1} | {metric.P95Microseconds:N1} | "
                + $"{metric.P99Microseconds:N1} | {metric.CpuPercentOfOneCore:N1} | {metric.AllocatedBytesPerOperation:N0} | "
                + $"{metric.AverageBatchSize:N1} | {metric.DurabilityFlushes} | {metric.StorageFiles} | "
                + $"{metric.ApproximateWriteAmplification:N2} | {metric.VerifiedRecords} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }

        builder.AppendLine();
        builder.AppendLine("## Forced-termination recovery");
        builder.AppendLine();
        builder.AppendLine("| cycle | stripes | acknowledged | recovered | repaired tail B | recovery ms | result |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---|");
        foreach (var metric in report.CrashMetrics)
        {
            builder.AppendLine(
                $"| {metric.Cycle} | {metric.StripeCount} | {metric.AcknowledgedRecords} | "
                + $"{metric.RecoveredRecords} | {metric.RepairedTailBytes} | {metric.RecoveryMilliseconds:N2} | "
                + $"{(metric.Passed ? "PASS" : "FAIL")} |");
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

        builder.AppendLine();
        builder.AppendLine("## Method notes");
        builder.AppendLine();
        foreach (var note in report.Notes)
        {
            builder.AppendLine($"- {note}");
        }

        return builder.ToString();
    }

    private static void PrintHeader(PhaseOneReport report)
    {
        Console.WriteLine("InterfaceDB NVMe-striped storage prototype");
        Console.WriteLine($"  OS: {report.OperatingSystem}");
        Console.WriteLine($"  Runtime: {report.Runtime}");
        Console.WriteLine($"  Records: {report.Options.Records:N0} x {report.Options.Iterations:N0} iterations");
        Console.WriteLine($"  Payloads: {string.Join(", ", report.Options.PayloadBytes.Select(static value => $"{value:N0} B"))}");
        Console.WriteLine($"  Concurrency: {string.Join(", ", report.Options.ConcurrencyLevels)}");
        Console.WriteLine($"  Stripes: {string.Join(", ", report.Options.StripeCounts)}");
    }

    private static void PrintSummary(PhaseOneReport report, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine($"Phase One correctness: {(report.Passed ? "PASS" : "FAIL")}");
        foreach (var finding in report.Findings)
        {
            Console.WriteLine($"  {finding}");
        }

        Console.WriteLine($"Crash cycles passed: {report.CrashMetrics.Count(static metric => metric.Passed)}/{report.CrashMetrics.Count}");
        Console.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"Markdown: {Path.ChangeExtension(Path.GetFullPath(outputPath), ".md")}");
    }

    private static void TryDeleteRunRoot(string runRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(runRoot);
            var allowedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbPhaseOne"))
                + Path.DirectorySeparatorChar;
            if (fullRoot.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct ConcurrentWorkloadResult(
        double WallMilliseconds,
        double OperationsPerSecond,
        double[] LatencyMicroseconds);

    private sealed record PhaseOneStoreSpecification(
        string Name,
        Func<string, IPhaseOneStore> Create);

    private sealed record PhaseOneBenchmarkCase(
        PhaseOneStoreSpecification Specification,
        int PayloadBytes,
        int Concurrency);
}
