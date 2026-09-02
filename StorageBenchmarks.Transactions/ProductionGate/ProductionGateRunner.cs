using Idb.Libraries.Abstractions.Enums;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class ProductionGateRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = ProductionGateOptions.Parse(args);
        var report = new ProductionGateReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown processor",
            Options = options
        };
        report.Notes.Add("Query timings materialize complete records, retain them through a deterministic all-field digest, and validate count plus digest against the source dataset.");
        report.Notes.Add("MetadataOnly is the canonical InterfaceDB storage-to-fresh-object mode; StrongObject and WeakObject describe cache behavior and are reported separately.");
        report.Notes.Add("Startup timings are provider-specific and the operating-system cache remains warm: InterfaceDB deserializes stored objects while rebuilding its in-memory indexes, while relational engines only reopen connections. The metric ends before any separately issued result query.");
        report.Notes.Add("Seed timings issue one independent write per object. SQL engines use one autocommit transaction per object; no encompassing transaction or bulk-copy operation is used.");
        report.Notes.Add("SQLite is measured through both direct Microsoft.Data.Sqlite commands and Dapper. SQL Server is opt-in because it requires the configured external service.");
        if (options.IncludeWriteAblations)
        {
            report.Notes.Add("Write ablations compare the normal indexed path with search indexes bypassed and with sidecar write locks disabled; these are diagnostic configurations, not production recommendations.");
        }

        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbProductionGate",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        Console.WriteLine("InterfaceDB production benchmark and stress gate");
        Console.WriteLine($"Run data: {runRoot}");
        Console.WriteLine($"Results: {Path.GetFullPath(options.OutputPath)}");

        try
        {
            foreach (var recordCount in options.RecordCounts)
            {
                var source = Enumerable.Range(0, recordCount).Select(static index => GateRecord.Create(index)).ToArray();
                foreach (var durabilityMode in options.DurabilityModes)
                {
                    foreach (var cacheMode in options.CacheModes)
                    {
                        var dataRoot = Path.Combine(
                            runRoot,
                            $"interfacedb-{recordCount}-{cacheMode}-{durabilityMode}");
                        await RunConfigurationAsync(
                            () => new InterfaceDbGateStore(dataRoot, cacheMode, durabilityMode),
                            source,
                            options,
                            report).ConfigureAwait(false);

                        if (options.IncludeWriteAblations)
                        {
                            var noIndexesRoot = Path.Combine(
                                runRoot,
                                $"interfacedb-no-indexes-{recordCount}-{cacheMode}-{durabilityMode}");
                            await RunConfigurationAsync(
                                () => new InterfaceDbGateStore(
                                    noIndexesRoot,
                                    cacheMode,
                                    durabilityMode,
                                    useSearchIndexes: false),
                                source,
                                options,
                                report).ConfigureAwait(false);

                            var noSidecarRoot = Path.Combine(
                                runRoot,
                                $"interfacedb-no-sidecar-{recordCount}-{cacheMode}-{durabilityMode}");
                            await RunConfigurationAsync(
                                () => new InterfaceDbGateStore(
                                    noSidecarRoot,
                                    cacheMode,
                                    durabilityMode,
                                    useSidecarWriteLocks: false),
                                source,
                                options,
                                report).ConfigureAwait(false);
                        }
                    }

                    var sqlitePath = Path.Combine(runRoot, $"sqlite-ado-{recordCount}-{durabilityMode}.db");
                    await RunConfigurationAsync(
                        () => new SqliteGateStore(
                            sqlitePath,
                            durabilityMode,
                            SqliteClientKind.MicrosoftDataSqlite),
                        source,
                        options,
                        report).ConfigureAwait(false);

                    var dapperSqlitePath = Path.Combine(runRoot, $"sqlite-dapper-{recordCount}-{durabilityMode}.db");
                    await RunConfigurationAsync(
                        () => new SqliteGateStore(
                            dapperSqlitePath,
                            durabilityMode,
                            SqliteClientKind.Dapper),
                        source,
                        options,
                        report).ConfigureAwait(false);
                }

                if (options.IncludeSqlServer)
                {
                    try
                    {
                        var connectionString = ResolveSqlServerConnectionString();
                        await RunConfigurationAsync(
                            () => new SqlServerGateStore(connectionString, dropOnDispose: !options.KeepData),
                            source,
                            options,
                            report).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        report.Failures.Add($"SQL Server setup failed: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }

            if (options.RunCrashRecovery)
            {
                try
                {
                    var recovery = await RunCrashRecoveryCyclesAsync(
                        runRoot,
                        options.CrashRecoveryCycles).ConfigureAwait(false);
                    report.RecoveryMetrics.AddRange(recovery);
                    foreach (var failed in recovery.Where(static metric => !metric.Passed))
                    {
                        report.Failures.Add($"Crash-recovery cycle {failed.Cycle} failed.");
                    }
                }
                catch (Exception exception)
                {
                    report.Failures.Add($"Crash-recovery runner failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            try
            {
                ApplyBaselineGate(report);
            }
            catch (Exception exception)
            {
                report.Failures.Add($"Baseline comparison failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            if (!options.KeepData)
            {
                TryDeleteRunRoot(runRoot);
            }
        }

        PrintSummary(report, options.OutputPath);
        if (!report.Passed)
        {
            throw new InvalidOperationException(
                $"Production gate failed with {report.Failures.Count} benchmark/recovery failures and "
                + $"{report.StressMetrics.Count(metric => metric.Errors != 0 || !metric.IntegrityPassed)} stress failures.");
        }
    }

    public static async Task RunCrashWriterAsync(string[] args)
    {
        var dataRoot = ProductionGateOptions.ParseString(args, "--root")
            ?? throw new ArgumentException("Crash writer requires --root.");
        var signalPath = ProductionGateOptions.ParseString(args, "--signal")
            ?? throw new ArgumentException("Crash writer requires --signal.");
        var recordsValue = ProductionGateOptions.ParseString(args, "--crash-records");
        var recordCount = recordsValue is null ? 10_000 : int.Parse(recordsValue);

        await using var store = new InterfaceDbGateStore(
            dataRoot,
            StorageObjectCacheMode.StrongObject,
            StorageDurabilityMode.Durable);
        await store.InitializeAsync().ConfigureAwait(false);
        await store.SeedAsync(Enumerable.Range(0, 10).Select(static index => GateRecord.Create(index)).ToArray())
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(signalPath, "ready").ConfigureAwait(false);
        var interruptedBatch = Enumerable.Range(0, recordCount)
            .Select(static index => GateRecord.Create(index, "crash"))
            .ToArray();
        await store.SeedAsync(interruptedBatch).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }

    private static async Task RunConfigurationAsync(
        Func<IProductionGateStore> factory,
        IReadOnlyList<GateRecord> source,
        ProductionGateOptions options,
        ProductionGateReport report)
    {
        await using var store = factory();
        Console.WriteLine();
        Console.WriteLine($"[{store.EngineName}] records={source.Count:N0}, cache={store.CacheModeName}, durability={store.DurabilityModeName}");
        try
        {
            report.Metrics.Add(await MeasureLifecycleAsync(
                store,
                source.Count,
                "initialize-empty",
                () => store.InitializeAsync()).ConfigureAwait(false));

            report.Metrics.Add(await MeasureSeedAsync(store, source).ConfigureAwait(false));
            await ValidateSnapshotAsync(store, source, "after seed").ConfigureAwait(false);

            report.Metrics.Add(await MeasureLifecycleAsync(
                store,
                source.Count,
                "provider-specific-startup-os-cache-warm-pre-query",
                () => store.ReopenAsync()).ConfigureAwait(false));
            await ValidateSnapshotAsync(store, source, "after reopen").ConfigureAwait(false);

            var exactId = source[source.Count / 2].Id;
            foreach (var queryKind in Enum.GetValues<GateQueryKind>())
            {
                var expected = ExpectedQuery(source, queryKind, exactId);
                report.Metrics.Add(await MeasureQueryAsync(
                    store,
                    source.Count,
                    queryKind,
                    exactId,
                    expected,
                    options.QueryIterations).ConfigureAwait(false));
            }

            if (options.RunStress)
            {
                foreach (var concurrency in options.ConcurrencyLevels)
                {
                    Console.WriteLine($"  stress concurrency={concurrency}, duration={options.StressDuration}");
                    var stress = await ProductionStressRunner.RunAsync(
                        store,
                        concurrency,
                        options.StressDuration).ConfigureAwait(false);
                    report.StressMetrics.Add(stress);
                    if (stress.Errors != 0 || !stress.IntegrityPassed)
                    {
                        report.Failures.Add(
                            $"{store.EngineName} stress failed at concurrency {concurrency}: "
                            + $"errors={stress.Errors}, integrity={stress.IntegrityPassed}.");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            report.Failures.Add(
                $"{store.EngineName} records={source.Count}, cache={store.CacheModeName}, "
                + $"durability={store.DurabilityModeName}: {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
    }

    private static async ValueTask<GateMetric> MeasureLifecycleAsync(
        IProductionGateStore store,
        int records,
        string operation,
        Func<ValueTask> action)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        await action().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var stats = store.GetStorageStats();
        Console.WriteLine($"  {operation,-39} {elapsed.TotalMilliseconds,12:N3} ms");
        return new GateMetric(
            store.EngineName,
            records,
            store.CacheModeName,
            store.DurabilityModeName,
            operation,
            1,
            elapsed.TotalMilliseconds,
            elapsed.TotalSeconds <= 0 ? 0 : 1 / elapsed.TotalSeconds,
            elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            allocated,
            string.Equals(operation, "initialize-empty", StringComparison.Ordinal) ? 0 : records,
            stats.Bytes,
            stats.Files);
    }

    private static async ValueTask<GateMetric> MeasureSeedAsync(
        IProductionGateStore store,
        IReadOnlyList<GateRecord> source)
    {
        var warmup = GateRecord.Create(int.MaxValue, "seed-warmup-" + Guid.NewGuid().ToString("N"));
        await store.InsertAsync(warmup).ConfigureAwait(false);
        if (!await store.DeleteAsync(warmup.Id).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{store.EngineName} could not delete the seed warmup object.");
        }

        var samples = new double[source.Count];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < source.Count; index++)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            await store.InsertAsync(source[index]).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(samples);
        var p50 = ProductionStressRunner.Percentile(samples, 0.50);
        var p95 = ProductionStressRunner.Percentile(samples, 0.95);
        var p99 = ProductionStressRunner.Percentile(samples, 0.99);
        var stats = store.GetStorageStats();
        Console.WriteLine(
            $"  {"seed-object-per-operation",-39} p50 {p50,9:N3} ms  p95 {p95,9:N3} ms  "
            + $"({source.Count / elapsed.TotalSeconds:N0} records/s)");
        return new GateMetric(
            store.EngineName,
            source.Count,
            store.CacheModeName,
            store.DurabilityModeName,
            "seed-object-per-operation",
            source.Count,
            elapsed.TotalMilliseconds,
            elapsed.TotalSeconds <= 0 ? 0 : source.Count / elapsed.TotalSeconds,
            p50,
            p95,
            p99,
            source.Count == 0 ? 0 : (double)allocated / source.Count,
            source.Count,
            stats.Bytes,
            stats.Files);
    }

    private static async ValueTask<GateMetric> MeasureQueryAsync(
        IProductionGateStore store,
        int records,
        GateQueryKind queryKind,
        string exactId,
        GateQueryResult expected,
        int iterations)
    {
        var warmup = await store.QueryAsync(queryKind, exactId).ConfigureAwait(false);
        EnsureQueryResult(store, queryKind, expected, warmup);

        var samples = new double[iterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var totalStarted = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var actual = await store.QueryAsync(queryKind, exactId).ConfigureAwait(false);
            samples[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            EnsureQueryResult(store, queryKind, expected, actual);
        }

        var total = Stopwatch.GetElapsedTime(totalStarted);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(samples);
        var p50 = ProductionStressRunner.Percentile(samples, 0.50);
        var p95 = ProductionStressRunner.Percentile(samples, 0.95);
        var p99 = ProductionStressRunner.Percentile(samples, 0.99);
        var stats = store.GetStorageStats();
        Console.WriteLine($"  {queryKind,-39} p50 {p50,9:N3} ms  p95 {p95,9:N3} ms  matches {expected.Count:N0}");
        return new GateMetric(
            store.EngineName,
            records,
            store.CacheModeName,
            store.DurabilityModeName,
            "query-" + queryKind.ToString().ToLowerInvariant(),
            iterations,
            total.TotalMilliseconds,
            total.TotalSeconds <= 0 ? 0 : iterations / total.TotalSeconds,
            p50,
            p95,
            p99,
            iterations == 0 ? 0 : (double)allocated / iterations,
            expected.Count,
            stats.Bytes,
            stats.Files);
    }

    private static GateQueryResult ExpectedQuery(
        IEnumerable<GateRecord> source,
        GateQueryKind queryKind,
        string exactId)
    {
        var matches = source.Where(record => queryKind switch
        {
            GateQueryKind.Exact => record.Id == exactId,
            GateQueryKind.Range => record.Age >= 75,
            GateQueryKind.Prefix => record.Name.StartsWith("customer-00000", StringComparison.Ordinal),
            GateQueryKind.Suffix => record.Name.EndsWith("special-tail", StringComparison.Ordinal),
            GateQueryKind.Contains => record.Content.Contains("rare-needle", StringComparison.Ordinal),
            GateQueryKind.BroadConjunction => record.Role == "student" && record.Age >= 40,
            _ => throw new ArgumentOutOfRangeException(nameof(queryKind), queryKind, null)
        });
        var count = 0;
        long checksum = 0;
        foreach (var match in matches)
        {
            count++;
            checksum += match.MaterializedChecksum;
        }

        return new GateQueryResult(count, checksum);
    }

    private static void EnsureQueryResult(
        IProductionGateStore store,
        GateQueryKind queryKind,
        GateQueryResult expected,
        GateQueryResult actual)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"{store.EngineName} {queryKind} returned count/checksum {actual.Count}/{actual.Checksum}; "
                + $"expected {expected.Count}/{expected.Checksum}.");
        }
    }

    private static async ValueTask ValidateSnapshotAsync(
        IProductionGateStore store,
        IReadOnlyList<GateRecord> expected,
        string phase)
    {
        var actual = await store.SnapshotAsync().ConfigureAwait(false);
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException(
                $"{store.EngineName} snapshot {phase} contains {actual.Count} records; expected {expected.Count}.");
        }

        foreach (var record in expected)
        {
            if (!actual.TryGetValue(record.Id, out var candidate) || candidate != record)
            {
                throw new InvalidOperationException(
                    $"{store.EngineName} snapshot {phase} differs at '{record.Id}'.");
            }
        }
    }

    private static async Task<IReadOnlyList<GateRecoveryMetric>> RunCrashRecoveryCyclesAsync(
        string runRoot,
        int cycles)
    {
        Console.WriteLine();
        Console.WriteLine($"Durable crash-recovery cycles: {cycles}");
        var results = new List<GateRecoveryMetric>(cycles);
        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var cycleRoot = Path.Combine(runRoot, "crash-recovery", cycle.ToString());
            var signalPath = Path.Combine(cycleRoot, "writer-ready.signal");
            Directory.CreateDirectory(cycleRoot);
            using var process = StartCrashWriter(cycleRoot, signalPath);
            var signalled = await WaitForFileAsync(signalPath, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!signalled)
            {
                TryKill(process);
                results.Add(new GateRecoveryMetric(cycle, 0, -1, 0, false));
                continue;
            }

            await Task.Delay(Random.Shared.Next(2, 30)).ConfigureAwait(false);
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            // Recovery starts after the production-gate profile's stale threshold. Before that
            // point a .writing file is intentionally indistinguishable from a slow live writer.
            await Task.Delay(TimeSpan.FromMilliseconds(2_100)).ConfigureAwait(false);
            var recoveredRecords = 0;
            var remainingProposals = -1;
            var passed = false;
            try
            {
                await using var store = new InterfaceDbGateStore(
                    cycleRoot,
                    StorageObjectCacheMode.StrongObject,
                    StorageDurabilityMode.Durable);
                await store.InitializeAsync().ConfigureAwait(false);
                var snapshot = await store.SnapshotAsync().ConfigureAwait(false);
                recoveredRecords = snapshot.Count;
                remainingProposals = Directory.EnumerateFiles(
                        cycleRoot,
                        "*",
                        SearchOption.AllDirectories)
                    .Count(path => Path.GetFileName(path).Contains(".pending.", StringComparison.OrdinalIgnoreCase));
                var sentinel = GateRecord.Create(cycle, "recovery-sentinel");
                await store.InsertAsync(sentinel).ConfigureAwait(false);
                var result = await store.QueryAsync(GateQueryKind.Exact, sentinel.Id).ConfigureAwait(false);
                passed = recoveredRecords >= 10 && remainingProposals == 0 && result.Count == 1;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"  crash cycle {cycle}: {exception.Message}");
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            Console.WriteLine(
                $"  cycle {cycle}: recovered={recoveredRecords:N0}, proposals={remainingProposals}, "
                + $"startup={elapsed.TotalMilliseconds:N3} ms, passed={passed}");
            results.Add(new GateRecoveryMetric(
                cycle,
                recoveredRecords,
                remainingProposals,
                elapsed.TotalMilliseconds,
                passed));
        }

        return results;
    }

    private static Process StartCrashWriter(string dataRoot, string signalPath)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the current process executable.");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--production-gate-crash-writer");
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add("--signal");
        startInfo.ArgumentList.Add(signalPath);
        startInfo.ArgumentList.Add("--crash-records");
        startInfo.ArgumentList.Add("10000");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start durable crash writer.");
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
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

    private static string ResolveSqlServerConnectionString()
    {
        IConfiguration configuration = TransactionBenchmarks.EnsureConfigurationInitialized();
        return configuration["Benchmarks:SqlServer:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Configure Benchmarks:SqlServer:ConnectionString before using --include-sqlserver.");
    }

    private static void ApplyBaselineGate(ProductionGateReport report)
    {
        var baselinePath = report.Options.BaselinePath;
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(baselinePath));
        if (!document.RootElement.TryGetProperty("MaterializationContract", out var contractElement)
            || !string.Equals(
                contractElement.GetString(),
                report.MaterializationContract,
                StringComparison.Ordinal))
        {
            var baselineContract = contractElement.ValueKind == JsonValueKind.String
                ? contractElement.GetString()
                : null;
            throw new InvalidOperationException(
                $"Baseline materialization contract '{baselineContract ?? "<missing>"}' is incompatible with "
                + $"'{report.MaterializationContract}'. Generate a new baseline with the current benchmark code.");
        }

        if (!document.RootElement.TryGetProperty("Metrics", out var metrics))
        {
            throw new InvalidOperationException("Baseline report does not contain Metrics.");
        }

        var baseline = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var metric in metrics.EnumerateArray())
        {
            var key = MetricKey(
                metric.GetProperty("Engine").GetString()!,
                metric.GetProperty("Records").GetInt32(),
                metric.GetProperty("CacheMode").GetString()!,
                metric.GetProperty("DurabilityMode").GetString()!,
                metric.GetProperty("Operation").GetString()!);
            baseline[key] = metric.GetProperty("P95Milliseconds").GetDouble();
        }

        var multiplier = 1 + report.Options.MaximumRegressionPercent / 100;
        foreach (var metric in report.Metrics)
        {
            var key = MetricKey(
                metric.Engine,
                metric.Records,
                metric.CacheMode,
                metric.DurabilityMode,
                metric.Operation);
            if (!baseline.TryGetValue(key, out var baselineP95) || baselineP95 <= 0)
            {
                continue;
            }

            if (metric.P95Milliseconds > baselineP95 * multiplier)
            {
                report.Failures.Add(
                    $"Performance regression for {key}: p95 {metric.P95Milliseconds:N3} ms versus "
                    + $"baseline {baselineP95:N3} ms ({report.Options.MaximumRegressionPercent:N1}% allowed).");
            }
        }
    }

    private static string MetricKey(
        string engine,
        int records,
        string cache,
        string durability,
        string operation) =>
        string.Join('|', engine, records, cache, durability, operation);

    private static async Task WriteReportAsync(ProductionGateReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, jsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.ChangeExtension(fullPath, ".md"),
            BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static string BuildMarkdown(ProductionGateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# InterfaceDB Production Gate");
        builder.AppendLine();
        builder.AppendLine($"- Result: **{(report.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Started: {report.StartedUtc:O}");
        builder.AppendLine($"- Completed: {report.CompletedUtc:O}");
        builder.AppendLine($"- Runtime: {report.Runtime}");
        builder.AppendLine();
        builder.AppendLine("## Benchmark metrics");
        builder.AppendLine();
        builder.AppendLine("| Engine | Records | Cache | Durability | Operation | p50 ms | p95 ms | p99 ms | ops/s | Result count |");
        builder.AppendLine("|---|---:|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var metric in report.Metrics)
        {
            builder.AppendLine(
                $"| {metric.Engine} | {metric.Records} | {metric.CacheMode} | {metric.DurabilityMode} | {metric.Operation} | "
                + $"{metric.P50Milliseconds:N3} | {metric.P95Milliseconds:N3} | {metric.P99Milliseconds:N3} | "
                + $"{metric.OperationsPerSecond:N1} | {metric.ResultCount} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Stress metrics");
        builder.AppendLine();
        builder.AppendLine("| Engine | Cache | Durability | Concurrency | ops/s | p50 ms | p95 ms | p99 ms | Errors | Integrity |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var metric in report.StressMetrics)
        {
            builder.AppendLine(
                $"| {metric.Engine} | {metric.CacheMode} | {metric.DurabilityMode} | {metric.Concurrency} | "
                + $"{metric.OperationsPerSecond:N1} | {metric.P50Milliseconds:N3} | {metric.P95Milliseconds:N3} | "
                + $"{metric.P99Milliseconds:N3} | {metric.Errors} | {metric.IntegrityPassed} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Crash recovery");
        builder.AppendLine();
        builder.AppendLine("| Cycle | Recovered records | Remaining proposals | Recovery ms | Passed |");
        builder.AppendLine("|---:|---:|---:|---:|---|");
        foreach (var metric in report.RecoveryMetrics)
        {
            builder.AppendLine(
                $"| {metric.Cycle} | {metric.RecoveredRecords} | {metric.RemainingProposalFiles} | "
                + $"{metric.RecoveryMilliseconds:N3} | {metric.Passed} |");
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
        builder.AppendLine("## Interpretation notes");
        builder.AppendLine();
        foreach (var note in report.Notes)
        {
            builder.AppendLine($"- {note}");
        }

        return builder.ToString();
    }

    private static void PrintSummary(ProductionGateReport report, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine($"Production gate: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Benchmark metrics: {report.Metrics.Count}");
        Console.WriteLine($"Stress configurations: {report.StressMetrics.Count}");
        Console.WriteLine($"Crash-recovery cycles: {report.RecoveryMetrics.Count}");
        Console.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"Markdown: {Path.ChangeExtension(Path.GetFullPath(outputPath), ".md")}");
    }

    private static void TryDeleteRunRoot(string runRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(runRoot);
            var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbProductionGate"));
            if (fullRoot.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
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
}
