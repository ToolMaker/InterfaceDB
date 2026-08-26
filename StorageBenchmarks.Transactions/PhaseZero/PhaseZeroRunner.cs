using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class PhaseZeroRunner
{
    private const int WarmupRecords = 4;

    public static async Task RunAsync(string[] args)
    {
        var options = PhaseZeroOptions.Parse(args);
        var started = DateTimeOffset.UtcNow;
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbPhaseZero",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new PhaseZeroReport
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
            DirectoryFlushStrategy = PlatformDurableFileSystem.Instance.DirectoryFlushStrategy,
            Options = options
        };

        report.Notes.Add(
            "Every measured save is independent: no benchmark wraps multiple objects in one transaction or amortizes one durability flush across a batch.");
        report.Notes.Add(
            "The append-log, raw-log, and FASTER controls include PhaseZeroCodec object serialization inside each timed operation; SQLite binds all object fields and InterfaceDB performs its normal object serialization.");
        report.Notes.Add(
            "SQLite uses WAL, synchronous=FULL, autocommit per object, and no secondary indexes for this storage-layer admission comparison.");
        report.Notes.Add(
            "Microsoft.FASTER.Core 2.6.5 is the available Microsoft log-family package control; it is not presented as a current standalone Tsavorite NuGet package.");
        if (string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            report.Notes.Add(
                "This report was produced in a container. Treat it as virtualized Linux development evidence, not the bare-metal Linux NVMe release gate.");
        }

        try
        {
            Console.WriteLine("InterfaceDB object-native storage Phase 0 gate");
            Console.WriteLine($"  OS: {report.OperatingSystem}");
            Console.WriteLine($"  Runtime: {report.Runtime}");
            Console.WriteLine($"  Records: {options.Records:N0} x {options.Iterations:N0} iterations");
            Console.WriteLine($"  Content bytes: {options.ContentBytes:N0}");
            Console.WriteLine($"  Crash cycles: {options.CrashCycles:N0}");

            foreach (var specification in CreateStoreSpecifications(options))
            {
                try
                {
                    report.Metrics.Add(await MeasureAsync(
                        specification,
                        runRoot,
                        options).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    var failure = $"{specification.Name} benchmark failed: {exception.GetType().Name}: {exception.Message}";
                    report.Failures.Add(failure);
                    Console.WriteLine($"  FAIL {failure}");
                }
            }

            report.CrashMetrics.AddRange(await RunCrashRecoveryCyclesAsync(runRoot, options).ConfigureAwait(false));
            foreach (var failedCrash in report.CrashMetrics.Where(static metric => !metric.Passed))
            {
                report.Failures.Add(
                    $"Crash cycle {failedCrash.Cycle} failed after {failedCrash.AcknowledgedRecords:N0} acknowledgements: {failedCrash.Failure}");
            }

            EvaluateAdmission(report);
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
        var root = PhaseZeroOptions.ParseString(args, "--root")
            ?? throw new ArgumentException("Phase 0 crash writer requires --root.");
        var acknowledgementPath = PhaseZeroOptions.ParseString(args, "--ack")
            ?? throw new ArgumentException("Phase 0 crash writer requires --ack.");
        var contentBytes = int.Parse(
            PhaseZeroOptions.ParseString(args, "--payload-bytes") ?? "512",
            CultureInfo.InvariantCulture);
        var segmentBytes = long.Parse(
            PhaseZeroOptions.ParseString(args, "--segment-bytes") ?? "16384",
            CultureInfo.InvariantCulture);
        var records = int.Parse(
            PhaseZeroOptions.ParseString(args, "--crash-records") ?? "1000000",
            CultureInfo.InvariantCulture);

        var options = new ObjectLogProbeOptions
        {
            DurabilityMode = ObjectLogDurabilityMode.Durable,
            FlushOnDispose = false,
            MaxSegmentBytes = segmentBytes
        };
        await using var writer = await ObjectLogProbeWriter.OpenAsync(root, options).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(acknowledgementPath))!);

        for (var index = 0; index < records; index++)
        {
            var record = CreateRecord(index, "crash", contentBytes);
            _ = await writer.AppendAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                acknowledgementPath,
                (index + 1).ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<PhaseZeroStoreSpecification> CreateStoreSpecifications(PhaseZeroOptions options) =>
    [
        new(
            "Raw reused append handle",
            root => new RawAppendPhaseZeroStore(root)),
        new(
            "Phase 0 checksummed object log (Durable)",
            root => new ObjectLogPhaseZeroStore(root, new ObjectLogProbeOptions
            {
                DurabilityMode = ObjectLogDurabilityMode.Durable,
                FlushOnDispose = false,
                MaxSegmentBytes = options.MaxSegmentBytes
            })),
        new(
            "Phase 0 checksummed object log (WriteThrough)",
            root => new ObjectLogPhaseZeroStore(root, new ObjectLogProbeOptions
            {
                DurabilityMode = ObjectLogDurabilityMode.Durable,
                DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough,
                FlushOnDispose = false,
                MaxSegmentBytes = options.MaxSegmentBytes
            })),
        new(
            "Microsoft FASTER Log 2.6.5",
            root => new FasterLogPhaseZeroStore(root)),
        new(
            "SQLite Microsoft.Data.Sqlite FULL",
            root => new ProductionGatePhaseZeroStore(
                new SqliteGateStore(
                    Path.Combine(root, "phase-zero.sqlite"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.MicrosoftDataSqlite,
                    createSecondaryIndexes: false),
                "complete object bind + INSERT + autocommit")),
        new(
            "SQLite Dapper FULL",
            root => new ProductionGatePhaseZeroStore(
                new SqliteGateStore(
                    Path.Combine(root, "phase-zero.sqlite"),
                    StorageDurabilityMode.Durable,
                    SqliteClientKind.Dapper,
                    createSecondaryIndexes: false),
                "complete object map + INSERT + autocommit")),
        new(
            "Current InterfaceDB file provider",
            root => new ProductionGatePhaseZeroStore(
                new InterfaceDbGateStore(
                    root,
                    StorageObjectCacheMode.MetadataOnly,
                    StorageDurabilityMode.Durable,
                    useSearchIndexes: false,
                    useSidecarWriteLocks: true),
                "complete object save; passthrough index manager")),
        new(
            "Phase 0 checksummed object log (Performance)",
            root => new ObjectLogPhaseZeroStore(root, new ObjectLogProbeOptions
            {
                DurabilityMode = ObjectLogDurabilityMode.Performance,
                FlushOnDispose = true,
                MaxSegmentBytes = options.MaxSegmentBytes
            }))
    ];

    private static async Task<PhaseZeroMetric> MeasureAsync(
        PhaseZeroStoreSpecification specification,
        string runRoot,
        PhaseZeroOptions options)
    {
        Console.WriteLine();
        Console.WriteLine(specification.Name);
        var samples = new List<double>(options.Records * options.Iterations);
        long totalAllocated = 0;
        PhaseZeroStorageStats lastStats = default;
        var lastVerifiedCount = 0;
        long lastVerifiedChecksum = 0;
        string? engineName = null;
        string? durabilityName = null;
        string? scope = null;

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var storeRoot = Path.Combine(
                runRoot,
                "metrics",
                Sanitize(specification.Name),
                iteration.ToString("D2", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(storeRoot);
            await using var store = specification.Create(storeRoot);
            await store.InitializeAsync().ConfigureAwait(false);
            engineName = store.EngineName;
            durabilityName = store.DurabilityName;
            scope = store.Scope;

            for (var warmup = 0; warmup < WarmupRecords; warmup++)
            {
                await store.AppendAsync(CreateRecord(warmup, $"warmup-{iteration}", options.ContentBytes))
                    .ConfigureAwait(false);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var expectedChecksum = 0L;
            for (var ordinal = 0; ordinal < options.Records; ordinal++)
            {
                var record = CreateRecord(ordinal, $"measured-{iteration}", options.ContentBytes);
                expectedChecksum += record.MaterializedChecksum;
                var started = Stopwatch.GetTimestamp();
                await store.AppendAsync(record).ConfigureAwait(false);
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
            }

            totalAllocated += GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var verified = await store.VerifyAsync().ConfigureAwait(false);
            var expectedCount = options.Records + WarmupRecords;
            var warmupChecksum = Enumerable.Range(0, WarmupRecords)
                .Select(ordinal => CreateRecord(ordinal, $"warmup-{iteration}", options.ContentBytes))
                .Sum(static record => record.MaterializedChecksum);
            if (verified.Count != expectedCount || verified.Checksum != expectedChecksum + warmupChecksum)
            {
                throw new InvalidDataException(
                    $"Verification returned {verified.Count:N0}/{verified.Checksum:N0}; expected {expectedCount:N0}/{expectedChecksum + warmupChecksum:N0}.");
            }

            lastVerifiedCount = verified.Count;
            lastVerifiedChecksum = verified.Checksum;
            lastStats = store.GetStorageStats();
            Console.WriteLine($"  iteration {iteration + 1}/{options.Iterations} verified {verified.Count:N0} records");
        }

        samples.Sort();
        var totalMicroseconds = samples.Sum();
        var metric = new PhaseZeroMetric(
            engineName!,
            durabilityName!,
            scope!,
            samples.Count,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            Percentile(samples, 0.99),
            samples.Average(),
            totalMicroseconds <= 0 ? 0 : samples.Count * 1_000_000d / totalMicroseconds,
            samples.Count == 0 ? 0 : (double)totalAllocated / samples.Count,
            lastStats.Bytes,
            lastStats.Files,
            lastVerifiedCount,
            lastVerifiedChecksum);
        Console.WriteLine(
            $"  p50 {metric.P50Microseconds,10:N1} us  p95 {metric.P95Microseconds,10:N1} us  "
            + $"mean {metric.MeanMicroseconds,10:N1} us  {metric.OperationsPerSecond,10:N1} ops/s");
        return metric;
    }

    private static async Task<IReadOnlyList<PhaseZeroCrashMetric>> RunCrashRecoveryCyclesAsync(
        string runRoot,
        PhaseZeroOptions options)
    {
        var metrics = new List<PhaseZeroCrashMetric>(options.CrashCycles);
        if (options.CrashCycles == 0)
        {
            return metrics;
        }

        Console.WriteLine();
        Console.WriteLine($"Forced-termination recovery: {options.CrashCycles} cycles");
        for (var cycle = 1; cycle <= options.CrashCycles; cycle++)
        {
            var cycleRoot = Path.Combine(runRoot, "crash", cycle.ToString("D3", CultureInfo.InvariantCulture));
            var acknowledgementPath = Path.Combine(cycleRoot, "acknowledged.txt");
            Directory.CreateDirectory(cycleRoot);
            using var process = StartCrashWriter(cycleRoot, acknowledgementPath, options);
            var acknowledgement = await WaitForAcknowledgementAsync(
                process,
                acknowledgementPath,
                minimum: 8,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var acknowledged = acknowledgement.Acknowledged;
            if (acknowledged < 8)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                metrics.Add(new PhaseZeroCrashMetric(
                    cycle,
                    acknowledged,
                    0,
                    0,
                    0,
                    0,
                    false,
                    acknowledgement.Failure ?? "Crash writer did not acknowledge eight records within 30 seconds."));
                Console.WriteLine($"  cycle {cycle,2}: FAIL {acknowledgement.Failure ?? "writer timeout"}");
                continue;
            }

            await Task.Delay(Random.Shared.Next(1, 20)).ConfigureAwait(false);
            acknowledged = Math.Max(acknowledged, TryReadAcknowledgement(acknowledgementPath));
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);

            var started = Stopwatch.GetTimestamp();
            try
            {
                var recovered = ObjectLogRecovery.Recover(cycleRoot, repairActiveTail: true);
                var passed = recovered.Records.Count >= acknowledged;
                for (var ordinal = 0; passed && ordinal < acknowledged; ordinal++)
                {
                    var key = CreateRecord(ordinal, "crash", options.ContentBytes).Id;
                    passed = recovered.Records.TryGetValue(key, out var recoveredRecord)
                        && PhaseZeroCodec.Decode(recoveredRecord.Payload).Id == key;
                }

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                metrics.Add(new PhaseZeroCrashMetric(
                    cycle,
                    acknowledged,
                    recovered.Records.Count,
                    recovered.RepairedTailBytes,
                    recovered.IgnoredOrphanSegments,
                    elapsed,
                    passed,
                    passed ? null : "One or more acknowledged records were absent or invalid."));
                Console.WriteLine(
                    $"  cycle {cycle,2}: ack {acknowledged,5}  recovered {recovered.Records.Count,5}  "
                    + $"tail repaired {recovered.RepairedTailBytes,5} B  {(passed ? "PASS" : "FAIL")}");
            }
            catch (Exception exception)
            {
                metrics.Add(new PhaseZeroCrashMetric(
                    cycle,
                    acknowledged,
                    0,
                    0,
                    0,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    false,
                    $"{exception.GetType().Name}: {exception.Message}"));
                Console.WriteLine($"  cycle {cycle,2}: FAIL {exception.Message}");
            }
        }

        return metrics;
    }

    private static void EvaluateAdmission(PhaseZeroReport report)
    {
        var objectLog = report.Metrics.SingleOrDefault(metric =>
            metric.Engine == "Phase 0 checksummed object log"
            && metric.Durability == "Durable/ExplicitFlush");
        var writeThroughObjectLog = report.Metrics.SingleOrDefault(metric =>
            metric.Engine == "Phase 0 checksummed object log"
            && metric.Durability == "Durable/WriteThrough");
        var sqlite = report.Metrics.SingleOrDefault(metric =>
            metric.Engine.StartsWith("SQLite Microsoft.Data.Sqlite", StringComparison.Ordinal));
        if (objectLog is null || writeThroughObjectLog is null || sqlite is null)
        {
            report.Failures.Add("The strict object-log or SQLite direct FULL admission metric is missing.");
            return;
        }

        report.StrictP50ImprovementPercent = ImprovementPercent(
            sqlite.P50Microseconds,
            objectLog.P50Microseconds);
        report.StrictP95ImprovementPercent = ImprovementPercent(
            sqlite.P95Microseconds,
            objectLog.P95Microseconds);
        report.WriteThroughP50ImprovementPercent = ImprovementPercent(
            sqlite.P50Microseconds,
            writeThroughObjectLog.P50Microseconds);
        report.WriteThroughP95ImprovementPercent = ImprovementPercent(
            sqlite.P95Microseconds,
            writeThroughObjectLog.P95Microseconds);
        report.WriteThroughAlternativePassed = report.WriteThroughP50ImprovementPercent >= 15
            && writeThroughObjectLog.P95Microseconds <= sqlite.P95Microseconds
            && report.CrashMetrics.All(static metric => metric.Passed);
        report.WindowsOrLinuxAdmissionPassed = report.StrictP50ImprovementPercent >= 15
            && objectLog.P95Microseconds <= sqlite.P95Microseconds
            && report.CrashMetrics.All(static metric => metric.Passed);

        if (report.StrictP50ImprovementPercent < 15)
        {
            report.Failures.Add(
                $"Strict object-log p50 improvement is {report.StrictP50ImprovementPercent:N1}%; Phase 0 requires at least 15.0% versus SQLite direct FULL.");
        }

        if (objectLog.P95Microseconds > sqlite.P95Microseconds)
        {
            report.Failures.Add(
                $"Strict object-log p95 is {objectLog.P95Microseconds:N1} us versus SQLite direct FULL {sqlite.P95Microseconds:N1} us; Phase 0 requires no slower p95.");
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            report.Failures.Add("Phase 0 admission is defined only for the Windows and Linux gates.");
        }

        report.Notes.Add(
            $"This { (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "local") } run's strict admission result is {(report.WindowsOrLinuxAdmissionPassed ? "PASS" : "FAIL")}: "
            + $"p50 improvement {report.StrictP50ImprovementPercent:N1}%, p95 improvement {report.StrictP95ImprovementPercent:N1}%.");
        report.Notes.Add(
            $"The OS write-through alternative is {(report.WriteThroughAlternativePassed ? "PASS" : "FAIL")}: "
            + $"p50 improvement {report.WriteThroughP50ImprovementPercent:N1}%, p95 improvement {report.WriteThroughP95ImprovementPercent:N1}%. "
            + "It is reported separately because the approved Phase 0 gate explicitly requires FlushToDisk.");
        report.Notes.Add(
            "A final production-engine go decision requires passing this same gate on both Windows and Linux; one platform report is intentionally not treated as cross-platform proof.");
    }

    private static Process StartCrashWriter(
        string dataRoot,
        string acknowledgementPath,
        PhaseZeroOptions options)
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

        startInfo.ArgumentList.Add("--phase-zero-crash-writer");
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add("--ack");
        startInfo.ArgumentList.Add(acknowledgementPath);
        startInfo.ArgumentList.Add("--payload-bytes");
        startInfo.ArgumentList.Add(options.ContentBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--segment-bytes");
        startInfo.ArgumentList.Add(Math.Min(options.MaxSegmentBytes, 4L * 1024).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--crash-records");
        startInfo.ArgumentList.Add("1000000");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Phase 0 crash writer.");
    }

    private static async Task<(int Acknowledged, string? Failure)> WaitForAcknowledgementAsync(
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
                return (
                    acknowledged,
                    $"Crash writer exited with code {process.ExitCode}: {detail.Trim()}");
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

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
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

    private static GateRecord CreateRecord(int ordinal, string prefix, int contentBytes)
    {
        var baseline = GateRecord.Create(ordinal, prefix);
        var marker = baseline.Content + "|";
        var padding = new string((char)('a' + ordinal % 26), Math.Max(0, contentBytes - marker.Length));
        return baseline with { Content = marker + padding };
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
        baseline <= 0 ? 0 : (baseline - candidate) / baseline * 100d;

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : char.ToLowerInvariant(character))
            .ToArray());
    }

    private static async Task WriteReportAsync(PhaseZeroReport report, string outputPath)
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

    private static string BuildMarkdown(PhaseZeroReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# InterfaceDB object-native storage Phase 0 gate");
        builder.AppendLine();
        builder.AppendLine($"- Local result: **{(report.Passed ? "PASS" : "FAIL")}**");
        builder.AppendLine($"- Platform: {report.OperatingSystem} ({report.Architecture})");
        builder.AppendLine($"- Runtime: {report.Runtime}");
        builder.AppendLine($"- Started: {report.StartedUtc:O}");
        builder.AppendLine($"- Completed: {report.CompletedUtc:O}");
        builder.AppendLine($"- Directory durability: {report.DirectoryFlushStrategy}");
        builder.AppendLine();
        builder.AppendLine("## Object admission metrics");
        builder.AppendLine();
        builder.AppendLine("| Engine | Durability | p50 us | p95 us | p99 us | mean us | ops/s | B/op | files | scope |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var metric in report.Metrics)
        {
            builder.AppendLine(
                $"| {metric.Engine} | {metric.Durability} | {metric.P50Microseconds:N1} | {metric.P95Microseconds:N1} | "
                + $"{metric.P99Microseconds:N1} | {metric.MeanMicroseconds:N1} | {metric.OperationsPerSecond:N1} | "
                + $"{metric.AllocatedBytesPerOperation:N0} | {metric.StorageFiles:N0} | {metric.Scope} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Forced-termination recovery");
        builder.AppendLine();
        builder.AppendLine("| cycle | acknowledged | recovered | repaired tail B | orphans | recovery ms | result |");
        builder.AppendLine("|---:|---:|---:|---:|---:|---:|---|");
        foreach (var metric in report.CrashMetrics)
        {
            builder.AppendLine(
                $"| {metric.Cycle} | {metric.AcknowledgedRecords} | {metric.RecoveredRecords} | "
                + $"{metric.RepairedTailBytes} | {metric.IgnoredOrphanSegments} | {metric.RecoveryMilliseconds:N2} | "
                + $"{(metric.Passed ? "PASS" : "FAIL")} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Admission decision");
        builder.AppendLine();
        builder.AppendLine($"- Strict p50 improvement over SQLite direct FULL: {report.StrictP50ImprovementPercent:N1}% (required: at least 15%).");
        builder.AppendLine($"- Strict p95 improvement over SQLite direct FULL: {report.StrictP95ImprovementPercent:N1}% (required: at least 0%).");
        builder.AppendLine($"- This platform: {(report.WindowsOrLinuxAdmissionPassed ? "PASS" : "FAIL")}.");
        builder.AppendLine($"- OS write-through alternative p50/p95 improvement: {report.WriteThroughP50ImprovementPercent:N1}% / {report.WriteThroughP95ImprovementPercent:N1}% ({(report.WriteThroughAlternativePassed ? "PASS" : "FAIL")}, informational).");

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

    private static void PrintSummary(PhaseZeroReport report, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine($"Phase 0 local gate: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Strict p50 improvement: {report.StrictP50ImprovementPercent:N1}%");
        Console.WriteLine($"Strict p95 improvement: {report.StrictP95ImprovementPercent:N1}%");
        Console.WriteLine($"Write-through p50/p95 improvement: {report.WriteThroughP50ImprovementPercent:N1}% / {report.WriteThroughP95ImprovementPercent:N1}%");
        Console.WriteLine($"Crash cycles passed: {report.CrashMetrics.Count(static metric => metric.Passed)}/{report.CrashMetrics.Count}");
        Console.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"Markdown: {Path.ChangeExtension(Path.GetFullPath(outputPath), ".md")}");
    }

    private static void TryDeleteRunRoot(string runRoot)
    {
        try
        {
            var fullRoot = Path.GetFullPath(runRoot);
            var allowedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbPhaseZero"))
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

    private sealed record PhaseZeroStoreSpecification(
        string Name,
        Func<string, IPhaseZeroStore> Create);
}
