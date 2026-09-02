using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal static class SegmentCoreHashRoutingBenchmarkRunner
{
    private static ulong _checksum;

    public static async Task RunAsync(string[] args)
    {
        var options = SegmentCoreHashRoutingBenchmarkOptions.Parse(args);
        var rootParent = string.IsNullOrWhiteSpace(options.RootPath)
            ? Path.Combine(Path.GetTempPath(), "InterfaceDbHashRouting")
            : Path.GetFullPath(options.RootPath);
        var root = Path.Combine(rootParent, $"hash-routing-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var report = new SegmentCoreHashRoutingBenchmarkReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Options = options,
            RunRoot = root
        };

        try
        {
            Console.WriteLine("SegmentCore hash-routing comparison");
            Console.WriteLine(
                $"Algorithms: {string.Join(", ", options.Algorithms)}; stripes: {options.Stripes}; "
                + $"routing keys: {options.RoutingKeys:N0}; storage records: {options.StorageRecords:N0}; "
                + $"iterations: {options.Iterations}");

            var keySets = CreateKeySets(options.RoutingKeys);
            WarmUp(keySets, options);

            foreach (var keySet in keySets)
            {
                foreach (var algorithm in options.Algorithms)
                {
                    report.Routing.Add(MeasureRouting(keySet, algorithm, options));
                }
            }

            var storageKeySets = keySets
                .Select(set => new HashRoutingKeySet(
                    set.Name,
                    set.Keys.Take(options.StorageRecords).ToArray()))
                .ToArray();
            await WarmUpStorageAsync(root, storageKeySets[0], options).ConfigureAwait(false);
            foreach (var keySet in storageKeySets)
            {
                var samples = options.Algorithms.ToDictionary(
                    static algorithm => algorithm,
                    static _ => new List<SegmentCoreHashStorageSample>());
                for (var iteration = 0; iteration < options.Iterations; iteration++)
                {
                    var algorithms = (iteration & 1) == 0
                        ? options.Algorithms
                        : options.Algorithms.Reverse().ToArray();
                    foreach (var algorithm in algorithms)
                    {
                        var storeRoot = Path.Combine(
                            root,
                            keySet.Name,
                            algorithm.ToString(),
                            $"iteration-{iteration}");
                        samples[algorithm].Add(await MeasureStorageAsync(
                            storeRoot,
                            keySet,
                            algorithm,
                            iteration,
                            options).ConfigureAwait(false));
                    }
                }

                foreach (var algorithm in options.Algorithms)
                {
                    report.Storage.Add(SummarizeStorage(keySet.Name, algorithm, samples[algorithm]));
                }
            }

            report.Checksum = _checksum;
            report.Conclusion = BuildConclusion(report);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            try
            {
                await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            }
            finally
            {
                if (!options.KeepData && Directory.Exists(root))
                {
                    TryDeleteGeneratedDirectory(root);
                }
            }
        }

        Print(report);
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    private static HashRoutingKeySet[] CreateKeySets(int count)
    {
        var sequential = new string[count];
        var commonPrefix = new string[count];
        var guid = new string[count];
        var random = new Random(0x51E6_4A2D);
        Span<byte> guidBytes = stackalloc byte[16];
        for (var index = 0; index < count; index++)
        {
            var ordinal = index.ToString("D12", CultureInfo.InvariantCulture);
            sequential[index] = ordinal;
            commonPrefix[index] = "tenant/acme/customers/active/customer-" + ordinal;
            random.NextBytes(guidBytes);
            guid[index] = new Guid(guidBytes).ToString("N");
        }

        return
        [
            new HashRoutingKeySet("sequential", sequential),
            new HashRoutingKeySet("common-prefix", commonPrefix),
            new HashRoutingKeySet("guid", guid)
        ];
    }

    private static void WarmUp(
        IReadOnlyList<HashRoutingKeySet> keySets,
        SegmentCoreHashRoutingBenchmarkOptions options)
    {
        foreach (var algorithm in options.Algorithms)
        {
            foreach (var keySet in keySets)
            {
                var limit = Math.Min(50_000, keySet.Keys.Length);
                for (var pass = 0; pass < 3; pass++)
                {
                    for (var index = 0; index < limit; index++)
                    {
                        _checksum = System.Numerics.BitOperations.RotateLeft(_checksum, 7)
                            ^ SegmentCoreHashRouter.ComputeHash(
                                keySet.Keys[index],
                                algorithm,
                                options.HashSeed);
                    }
                }
            }
        }
    }

    private static async Task WarmUpStorageAsync(
        string root,
        HashRoutingKeySet keySet,
        SegmentCoreHashRoutingBenchmarkOptions options)
    {
        var warmUpRoot = Path.Combine(root, $".warm-up-{Guid.NewGuid():N}");
        var warmUpKeys = new HashRoutingKeySet(
            "warm-up",
            keySet.Keys.Take(Math.Min(1_000, keySet.Keys.Length)).ToArray());
        try
        {
            foreach (var algorithm in options.Algorithms)
            {
                _ = await MeasureStorageAsync(
                    Path.Combine(warmUpRoot, algorithm.ToString()),
                    warmUpKeys,
                    algorithm,
                    iteration: -1,
                    options).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(warmUpRoot))
            {
                TryDeleteGeneratedDirectory(warmUpRoot);
            }
        }
    }

    private static SegmentCoreHashRoutingMetric MeasureRouting(
        HashRoutingKeySet keySet,
        SegmentCoreHashAlgorithm algorithm,
        SegmentCoreHashRoutingBenchmarkOptions options)
    {
        var nanoseconds = new double[options.Iterations];
        var allocatedBytes = new long[options.Iterations];
        var operations = checked(keySet.Keys.Length * options.RoutingPasses);
        var preflight = 0ul;
        foreach (var key in keySet.Keys)
        {
            preflight = System.Numerics.BitOperations.RotateLeft(preflight, 7)
                ^ SegmentCoreHashRouter.ComputeHash(key, algorithm, options.HashSeed);
        }

        _checksum ^= preflight;
        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = 0ul;
            for (var pass = 0; pass < options.RoutingPasses; pass++)
            {
                foreach (var key in keySet.Keys)
                {
                    checksum = System.Numerics.BitOperations.RotateLeft(checksum, 7)
                        ^ SegmentCoreHashRouter.ComputeHash(key, algorithm, options.HashSeed);
                }
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            allocatedBytes[iteration] = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
            nanoseconds[iteration] = elapsedTicks * (1_000_000_000d / Stopwatch.Frequency) / operations;
            _checksum ^= checksum;
        }

        var counts = new int[options.Stripes];
        foreach (var key in keySet.Keys)
        {
            var hash = SegmentCoreHashRouter.ComputeHash(key, algorithm, options.HashSeed);
            counts[(int)(hash & (uint)(options.Stripes - 1))]++;
        }

        var expected = keySet.Keys.Length / (double)options.Stripes;
        var maximumDeviation = counts.Max(count => Math.Abs(count - expected) / expected);
        var variance = counts.Sum(count => Math.Pow(count - expected, 2)) / counts.Length;
        var coefficientOfVariation = Math.Sqrt(variance) / expected;
        return new SegmentCoreHashRoutingMetric
        {
            Algorithm = algorithm,
            KeyShape = keySet.Name,
            KeyCount = keySet.Keys.Length,
            MedianNanosecondsPerKey = Median(nanoseconds),
            MedianAllocatedBytes = (long)Median(allocatedBytes.Select(static value => (double)value).ToArray()),
            MaximumStripeDeviationPercent = maximumDeviation * 100,
            StripeCoefficientOfVariationPercent = coefficientOfVariation * 100,
            MinimumStripeCount = counts.Min(),
            MaximumStripeCount = counts.Max(),
            EmptyStripes = counts.Count(static count => count == 0),
            StripeCounts = counts
        };
    }

    private static async Task<SegmentCoreHashStorageSample> MeasureStorageAsync(
        string root,
        HashRoutingKeySet keySet,
        SegmentCoreHashAlgorithm algorithm,
        int iteration,
        SegmentCoreHashRoutingBenchmarkOptions options)
    {
        Directory.CreateDirectory(root);
        var storeOptions = new SegmentCoreOptions
        {
            TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            LogOptions = new StripedObjectLogProbeOptions
            {
                StripeCount = options.Stripes,
                HashAlgorithm = algorithm,
                HashSeed = options.HashSeed,
                DurabilityStrategy = options.DurabilityStrategy,
                MaximumBatchRecords = Math.Min(options.BatchSize, 16_384),
                QueueCapacityPerStripe = Math.Max(1_024, options.BatchSize * 2)
            },
            PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
            {
                Mode = SegmentCorePrimaryIndexMode.Resident
            }
        };
        var payload = new byte[options.PayloadBytes];
        new Random(0x1729).NextBytes(payload);

        var store = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
        TimeSpan writeElapsed;
        TimeSpan checkpointElapsed;
        try
        {
            var writeStarted = Stopwatch.GetTimestamp();
            for (var batchStart = 0; batchStart < keySet.Keys.Length; batchStart += options.BatchSize)
            {
                var batchEnd = Math.Min(batchStart + options.BatchSize, keySet.Keys.Length);
                await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
                for (var index = batchStart; index < batchEnd; index++)
                {
                    await transaction.SaveAsync(keySet.Keys[index], payload).ConfigureAwait(false);
                }

                await transaction.CommitAsync().ConfigureAwait(false);
            }

            writeElapsed = Stopwatch.GetElapsedTime(writeStarted);
            if (store.Count != keySet.Keys.Length)
            {
                throw new InvalidOperationException(
                    $"{algorithm}/{keySet.Name} stored {store.Count:N0} of {keySet.Keys.Length:N0} records.");
            }

            var checkpointStarted = Stopwatch.GetTimestamp();
            var checkpoint = await store.CheckpointAsync().ConfigureAwait(false);
            checkpointElapsed = Stopwatch.GetElapsedTime(checkpointStarted);
            if (checkpoint.Entries != keySet.Keys.Length)
            {
                throw new InvalidOperationException(
                    $"{algorithm}/{keySet.Name} checkpoint contains {checkpoint.Entries:N0} records.");
            }
        }
        finally
        {
            await store.DisposeAsync().ConfigureAwait(false);
        }

        SegmentCoreStore? reopened = null;
        var reopenStarted = Stopwatch.GetTimestamp();
        try
        {
            reopened = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
            var reopenElapsed = Stopwatch.GetElapsedTime(reopenStarted);
            if (reopened.Count != keySet.Keys.Length)
            {
                throw new InvalidOperationException(
                    $"{algorithm}/{keySet.Name} reopened {reopened.Count:N0} of {keySet.Keys.Length:N0} records.");
            }

            foreach (var sampleIndex in SampleIndexes(keySet.Keys.Length))
            {
                var recovered = await reopened.GetAsync(keySet.Keys[sampleIndex]).ConfigureAwait(false);
                if (recovered is null || !recovered.AsSpan().SequenceEqual(payload))
                {
                    throw new InvalidDataException(
                        $"{algorithm}/{keySet.Name} failed payload validation at record {sampleIndex:N0}.");
                }
            }

            return new SegmentCoreHashStorageSample
            {
                Iteration = iteration,
                Records = keySet.Keys.Length,
                WriteElapsed = writeElapsed,
                CheckpointElapsed = checkpointElapsed,
                ReopenElapsed = reopenElapsed,
                StoreBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(static path => new FileInfo(path).Length)
            };
        }
        finally
        {
            if (reopened is not null)
            {
                await reopened.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static int[] SampleIndexes(int count) =>
        count == 1 ? [0] : [0, count / 3, (count * 2) / 3, count - 1];

    private static SegmentCoreHashStorageMetric SummarizeStorage(
        string keyShape,
        SegmentCoreHashAlgorithm algorithm,
        IReadOnlyList<SegmentCoreHashStorageSample> samples)
    {
        return new SegmentCoreHashStorageMetric
        {
            Algorithm = algorithm,
            KeyShape = keyShape,
            Records = samples[0].Records,
            MedianWritesPerSecond = Median(samples
                .Select(static sample => sample.Records / sample.WriteElapsed.TotalSeconds)
                .ToArray()),
            MedianCheckpointMilliseconds = Median(samples
                .Select(static sample => sample.CheckpointElapsed.TotalMilliseconds)
                .ToArray()),
            MedianReopenMilliseconds = Median(samples
                .Select(static sample => sample.ReopenElapsed.TotalMilliseconds)
                .ToArray()),
            MedianStoreBytes = (long)Median(samples
                .Select(static sample => (double)sample.StoreBytes)
                .ToArray()),
            Samples = samples.ToArray()
        };
    }

    private static string BuildConclusion(SegmentCoreHashRoutingBenchmarkReport report)
    {
        if (!report.Options.Algorithms.Contains(SegmentCoreHashAlgorithm.Fnv1a32))
        {
            return "Run completed without the FNV-1a 32 compatibility baseline; include fnv1a32 to calculate comparative speed ratios.";
        }

        var baselineRouting = report.Routing
            .Where(static metric => metric.Algorithm == SegmentCoreHashAlgorithm.Fnv1a32)
            .ToDictionary(static metric => metric.KeyShape, StringComparer.Ordinal);
        var baselineStorage = report.Storage
            .Where(static metric => metric.Algorithm == SegmentCoreHashAlgorithm.Fnv1a32)
            .ToDictionary(static metric => metric.KeyShape, StringComparer.Ordinal);

        var parts = new List<string>
        {
            "FNV-1a 32 remains the durable compatibility default. Alternative selection is a store-creation decision and requires migration afterward."
        };
        foreach (var algorithm in report.Options.Algorithms.Where(
                     static value => value != SegmentCoreHashAlgorithm.Fnv1a32))
        {
            var routingRatios = report.Routing
                .Where(metric => metric.Algorithm == algorithm)
                .Select(metric => baselineRouting[metric.KeyShape].MedianNanosecondsPerKey
                    / metric.MedianNanosecondsPerKey)
                .ToArray();
            var storageRatios = report.Storage
                .Where(metric => metric.Algorithm == algorithm)
                .Select(metric => metric.MedianWritesPerSecond
                    / baselineStorage[metric.KeyShape].MedianWritesPerSecond)
                .ToArray();
            parts.Add(
                $"{algorithm}: median routing speed {GeometricMean(routingRatios):F2}x and "
                + $"end-to-end write throughput {GeometricMean(storageRatios):F2}x versus FNV-1a 32 across the tested key shapes.");
        }

        return string.Join(' ', parts);
    }

    private static void Print(SegmentCoreHashRoutingBenchmarkReport report)
    {
        Console.WriteLine();
        Console.WriteLine("Routing cost and distribution");
        Console.WriteLine("Algorithm | Shape | ns/key | Max stripe deviation | CV | Empty");
        foreach (var metric in report.Routing)
        {
            Console.WriteLine(
                $"{metric.Algorithm,-10} | {metric.KeyShape,-13} | {metric.MedianNanosecondsPerKey,8:F2} | "
                + $"{metric.MaximumStripeDeviationPercent,8:F2}% | "
                + $"{metric.StripeCoefficientOfVariationPercent,6:F2}% | {metric.EmptyStripes}");
        }

        Console.WriteLine();
        Console.WriteLine("End-to-end SegmentCore storage");
        Console.WriteLine("Algorithm | Shape | writes/s | Checkpoint | Reopen | Store MiB");
        foreach (var metric in report.Storage)
        {
            Console.WriteLine(
                $"{metric.Algorithm,-10} | {metric.KeyShape,-13} | {metric.MedianWritesPerSecond,10:N0} | "
                + $"{metric.MedianCheckpointMilliseconds,8:F2} ms | "
                + $"{metric.MedianReopenMilliseconds,7:F2} ms | "
                + $"{metric.MedianStoreBytes / (1024d * 1024d),8:F2}");
        }

        Console.WriteLine();
        Console.WriteLine(report.Conclusion);
        foreach (var failure in report.Failures)
        {
            Console.WriteLine($"FAIL: {failure}");
        }

        Console.WriteLine($"Report: {Path.GetFullPath(report.Options.OutputPath)}");
    }

    private static async Task WriteReportAsync(
        SegmentCoreHashRoutingBenchmarkReport report,
        string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(report, serializerOptions))
            .ConfigureAwait(false);
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        var middle = values.Length / 2;
        return (values.Length & 1) == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2;
    }

    private static double GeometricMean(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : Math.Exp(values.Sum(Math.Log) / values.Count);

    private static void TryDeleteGeneratedDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);
        if (!name.StartsWith("hash-routing-run-", StringComparison.Ordinal)
            && !name.StartsWith(".warm-up-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove unexpected benchmark directory '{fullPath}'.");
        }

        try
        {
            Directory.Delete(fullPath, recursive: true);
        }
        catch
        {
            // Benchmark data is non-authoritative and can be removed manually if a handle lingers.
        }
    }

    private sealed record HashRoutingKeySet(string Name, string[] Keys);
}

internal sealed record SegmentCoreHashRoutingBenchmarkOptions
{
    public int RoutingKeys { get; init; } = 250_000;
    public int RoutingPasses { get; init; } = 4;
    public int StorageRecords { get; init; } = 20_000;
    public int PayloadBytes { get; init; } = 256;
    public int BatchSize { get; init; } = 1_000;
    public int Iterations { get; init; } = 3;
    public int Stripes { get; init; } = 16;
    public long HashSeed { get; init; }
    public SegmentCoreHashAlgorithm[] Algorithms { get; init; } = Enum.GetValues<SegmentCoreHashAlgorithm>();
    public ObjectLogDurabilityStrategy DurabilityStrategy { get; init; } =
        ObjectLogDurabilityStrategy.WriteThrough;
    public string RootPath { get; init; } = string.Empty;
    public bool KeepData { get; init; }
    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "segment-core-hash-routing",
        $"segment-core-hash-routing-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public static SegmentCoreHashRoutingBenchmarkOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        var routingKeys = ParseInt(args, "--routing-keys", quick ? 50_000 : 250_000, 1_000, 5_000_000);
        var storageRecords = ParseInt(args, "--storage-records", quick ? 5_000 : 20_000, 100, routingKeys);
        var stripes = ParseInt(args, "--stripes", 16, 1, 64);
        if (!System.Numerics.BitOperations.IsPow2((uint)stripes))
        {
            throw new ArgumentOutOfRangeException("--stripes", "Stripe count must be a power of two.");
        }

        return new SegmentCoreHashRoutingBenchmarkOptions
        {
            RoutingKeys = routingKeys,
            RoutingPasses = ParseInt(args, "--routing-passes", quick ? 2 : 4, 1, 100),
            StorageRecords = storageRecords,
            PayloadBytes = ParseInt(args, "--payload-bytes", 256, 1, 1024 * 1024),
            BatchSize = ParseInt(args, "--batch-size", 1_000, 1, 16_384),
            Iterations = ParseInt(args, "--iterations", quick ? 2 : 3, 1, 25),
            Stripes = stripes,
            HashSeed = ParseLong(args, "--hash-seed", 0),
            Algorithms = ParseAlgorithms(args),
            DurabilityStrategy = ParseDurability(args),
            RootPath = Read(args, "--root") ?? string.Empty,
            KeepData = HasFlag(args, "--keep-data"),
            OutputPath = Read(args, "--output") ?? Path.Combine(
                "artifacts",
                "segment-core-hash-routing",
                $"segment-core-hash-routing-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
        };
    }

    private static SegmentCoreHashAlgorithm[] ParseAlgorithms(string[] args)
    {
        var value = Read(args, "--algorithms");
        if (string.IsNullOrWhiteSpace(value))
        {
            return Enum.GetValues<SegmentCoreHashAlgorithm>();
        }

        var algorithms = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseAlgorithm)
            .Distinct()
            .ToArray();
        return algorithms.Length == 0
            ? throw new ArgumentException("At least one hash algorithm is required.", "--algorithms")
            : algorithms;
    }

    private static SegmentCoreHashAlgorithm ParseAlgorithm(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "fnv1a32" => SegmentCoreHashAlgorithm.Fnv1a32,
            "fnv1a64" => SegmentCoreHashAlgorithm.Fnv1a64,
            "xxhash64" => SegmentCoreHashAlgorithm.XxHash64,
            "xxhash3" or "xxh3" => SegmentCoreHashAlgorithm.XxHash3,
            _ => throw new ArgumentOutOfRangeException(
                "--algorithms",
                value,
                "Expected fnv1a32, fnv1a64, xxhash64, or xxhash3.")
        };

    private static ObjectLogDurabilityStrategy ParseDurability(string[] args) =>
        (Read(args, "--durability") ?? "write-through").ToLowerInvariant() switch
        {
            "write-through" or "writethrough" => ObjectLogDurabilityStrategy.WriteThrough,
            "explicit" or "explicit-flush" => ObjectLogDurabilityStrategy.ExplicitFlush,
            _ => throw new ArgumentOutOfRangeException(
                "--durability",
                "Expected write-through or explicit-flush.")
        };

    private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var value = Read(args, name) is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : fallback;
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name);
    }

    private static long ParseLong(string[] args, string name, long fallback) =>
        Read(args, name) is { } text
            ? long.Parse(text, CultureInfo.InvariantCulture)
            : fallback;

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static string? Read(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(name.Length + 1)..];
            }

            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal sealed record SegmentCoreHashRoutingMetric
{
    public SegmentCoreHashAlgorithm Algorithm { get; init; }
    public required string KeyShape { get; init; }
    public int KeyCount { get; init; }
    public double MedianNanosecondsPerKey { get; init; }
    public long MedianAllocatedBytes { get; init; }
    public double MaximumStripeDeviationPercent { get; init; }
    public double StripeCoefficientOfVariationPercent { get; init; }
    public int MinimumStripeCount { get; init; }
    public int MaximumStripeCount { get; init; }
    public int EmptyStripes { get; init; }
    public required int[] StripeCounts { get; init; }
}

internal sealed record SegmentCoreHashStorageSample
{
    public int Iteration { get; init; }
    public int Records { get; init; }
    public TimeSpan WriteElapsed { get; init; }
    public TimeSpan CheckpointElapsed { get; init; }
    public TimeSpan ReopenElapsed { get; init; }
    public long StoreBytes { get; init; }
}

internal sealed record SegmentCoreHashStorageMetric
{
    public SegmentCoreHashAlgorithm Algorithm { get; init; }
    public required string KeyShape { get; init; }
    public int Records { get; init; }
    public double MedianWritesPerSecond { get; init; }
    public double MedianCheckpointMilliseconds { get; init; }
    public double MedianReopenMilliseconds { get; init; }
    public long MedianStoreBytes { get; init; }
    public required SegmentCoreHashStorageSample[] Samples { get; init; }
}

internal sealed record SegmentCoreHashRoutingBenchmarkReport
{
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Processor { get; init; }
    public required SegmentCoreHashRoutingBenchmarkOptions Options { get; init; }
    public required string RunRoot { get; init; }
    public ulong Checksum { get; set; }
    public string Conclusion { get; set; } = string.Empty;
    public List<SegmentCoreHashRoutingMetric> Routing { get; init; } = [];
    public List<SegmentCoreHashStorageMetric> Storage { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    public bool Passed => Failures.Count == 0;
}
