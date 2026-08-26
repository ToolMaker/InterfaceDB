using System.Globalization;

internal sealed record PhaseOneOptions
{
    public int Records { get; init; } = 2_000;

    public int Iterations { get; init; } = 3;

    public int[] PayloadBytes { get; init; } = [512, 4 * 1024, 64 * 1024];

    public int[] ConcurrencyLevels { get; init; } = [1, 4, 16, 64];

    public int[] StripeCounts { get; init; } = [1, 2, 4, 8, 16];

    public int MaximumBatchRecords { get; init; } = 64;

    public int GroupCommitDelayMicroseconds { get; init; }

    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;

    public int CrashCycles { get; init; } = 8;

    public bool IncludeWriteThrough { get; init; } = true;

    public bool WriteThroughOnly { get; init; }

    public bool InterleaveCases { get; init; } = true;

    public bool IncludeBaselines { get; init; } = true;

    public bool KeepData { get; init; }

    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "phase-one",
        $"phase-one-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public static PhaseOneOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        return new PhaseOneOptions
        {
            Records = ParsePositiveInt(args, "--records", quick ? 320 : 2_000),
            Iterations = ParsePositiveInt(args, "--iterations", quick ? 2 : 3),
            PayloadBytes = ParsePositiveInts(args, "--payload-bytes", quick ? [512] : [512, 4 * 1024, 64 * 1024]),
            ConcurrencyLevels = ParsePositiveInts(args, "--concurrency", quick ? [1, 8, 32] : [1, 4, 16, 64]),
            StripeCounts = ParseStripeCounts(args, quick ? [1, 4, 8] : [1, 2, 4, 8, 16]),
            MaximumBatchRecords = ParsePositiveInt(args, "--batch-records", 64),
            GroupCommitDelayMicroseconds = ParseNonNegativeInt(args, "--group-delay-us", 0),
            MaxSegmentBytes = ParsePositiveLong(args, "--segment-bytes", quick ? 1024 * 1024 : 64L * 1024 * 1024),
            CrashCycles = ParseNonNegativeInt(args, "--crash-cycles", quick ? 2 : 8),
            IncludeWriteThrough = !HasFlag(args, "--no-write-through"),
            WriteThroughOnly = HasFlag(args, "--write-through-only"),
            InterleaveCases = !HasFlag(args, "--ordered"),
            IncludeBaselines = !HasFlag(args, "--custom-only"),
            KeepData = HasFlag(args, "--keep-data"),
            OutputPath = ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "phase-one",
                $"phase-one-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
        };
    }

    internal static string? ParseString(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(name.Length + 1)..];
            }

            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int[] ParseStripeCounts(string[] args, int[] fallback)
    {
        var values = ParsePositiveInts(args, "--stripes", fallback);
        if (values.Any(value => value > 64 || !System.Numerics.BitOperations.IsPow2((uint)value)))
        {
            throw new ArgumentOutOfRangeException(
                "--stripes",
                "Stripe counts must be powers of two between 1 and 64.");
        }

        return values;
    }

    private static int[] ParsePositiveInts(string[] args, string name, int[] fallback)
    {
        var value = ParseString(args, name);
        var values = value is null
            ? fallback
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.Parse(item, CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray();
        if (values.Length == 0 || values.Any(static item => item <= 0))
        {
            throw new ArgumentOutOfRangeException(name, "Values must be positive integers.");
        }

        return values;
    }

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static int ParseNonNegativeInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        return parsed >= 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static long ParsePositiveLong(string[] args, string name, long fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : long.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record PhaseOneMetric(
    string Engine,
    string Durability,
    int StripeCount,
    int Concurrency,
    int PayloadBytes,
    int Samples,
    double WallMilliseconds,
    double OperationsPerSecond,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MeanMicroseconds,
    double AllocatedBytesPerOperation,
    double CpuMilliseconds,
    double CpuPercentOfOneCore,
    long StorageBytes,
    int StorageFiles,
    double ApproximateWriteAmplification,
    long DurableBatches,
    long DurabilityFlushes,
    double AverageBatchSize,
    int LargestBatch,
    int VerifiedRecords,
    long VerifiedChecksum);

internal sealed record PhaseOneCrashMetric(
    int Cycle,
    int StripeCount,
    int AcknowledgedRecords,
    int RecoveredRecords,
    long RepairedTailBytes,
    double RecoveryMilliseconds,
    bool Passed,
    string? Failure);

internal sealed record PhaseOneReport
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset CompletedUtc { get; set; }

    public required string Runtime { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required string Processor { get; init; }

    public required string WorkingDrive { get; init; }

    public required string PhysicalDisk { get; init; }

    public required PhaseOneOptions Options { get; init; }

    public List<PhaseOneMetric> Metrics { get; init; } = [];

    public List<PhaseOneCrashMetric> CrashMetrics { get; init; } = [];

    public List<string> Failures { get; init; } = [];

    public List<string> Findings { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    public int SelectedCrashStripeCount { get; set; }

    public string SelectedCrashDurability { get; set; } = "ExplicitFlush";

    public bool Passed => Failures.Count == 0 && CrashMetrics.All(static metric => metric.Passed);
}

internal readonly record struct PhaseOneStoreStatistics(
    long StorageBytes,
    int StorageFiles,
    long DurableBatches,
    long DurabilityFlushes,
    double AverageBatchSize,
    int LargestBatch);

internal interface IPhaseOneStore : IAsyncDisposable
{
    string EngineName { get; }

    string Durability { get; }

    int StripeCount { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default);

    ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default);

    PhaseOneStoreStatistics GetStatistics();
}
