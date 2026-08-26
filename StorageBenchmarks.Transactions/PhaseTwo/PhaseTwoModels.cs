using System.Globalization;

internal sealed record PhaseTwoOptions
{
    public int Records { get; init; } = 500;

    public int Iterations { get; init; } = 3;

    public int[] PayloadBytes { get; init; } = [512, 4 * 1024, 64 * 1024];

    public int[] ConcurrencyLevels { get; init; } = [1, 16, 64];

    public int[] StripeCounts { get; init; } = [8, 16];

    public int ReadOperations { get; init; } = 5_000;

    public int MixedOperations { get; init; } = 2_000;

    public int CrashCycles { get; init; } = 5;

    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;

    public int QueueCapacityPerStripe { get; init; } = 1_024;

    public int CheckpointGenerationsToKeep { get; init; } = 2;

    public bool IncludeCurrentProvider { get; init; }

    public bool IncludeDapper { get; init; }

    public bool DapperOnly { get; init; }

    public bool CustomOnly { get; init; }

    public bool KeepData { get; init; }

    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "phase-two",
        $"phase-two-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public static PhaseTwoOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        return new PhaseTwoOptions
        {
            Records = ParsePositiveInt(args, "--records", quick ? 240 : 500),
            Iterations = ParsePositiveInt(args, "--iterations", quick ? 1 : 3),
            PayloadBytes = ParsePositiveInts(args, "--payload-bytes", quick ? [512] : [512, 4 * 1024, 64 * 1024]),
            ConcurrencyLevels = ParsePositiveInts(args, "--concurrency", quick ? [1, 16] : [1, 16, 64]),
            StripeCounts = ParseStripeCounts(args, quick ? [8] : [8, 16]),
            ReadOperations = ParsePositiveInt(args, "--read-operations", quick ? 960 : 5_000),
            MixedOperations = ParsePositiveInt(args, "--mixed-operations", quick ? 960 : 2_000),
            CrashCycles = ParseNonNegativeInt(args, "--crash-cycles", quick ? 1 : 5),
            MaxSegmentBytes = ParsePositiveLong(args, "--segment-bytes", quick ? 1024 * 1024 : 64L * 1024 * 1024),
            QueueCapacityPerStripe = ParseBoundedPositiveInt(args, "--queue-capacity", 1_024, 1_048_576),
            CheckpointGenerationsToKeep = ParseBoundedPositiveInt(args, "--checkpoint-generations", 2, 16),
            IncludeCurrentProvider = HasFlag(args, "--include-current"),
            IncludeDapper = HasFlag(args, "--include-dapper"),
            DapperOnly = HasFlag(args, "--dapper-only"),
            CustomOnly = HasFlag(args, "--custom-only"),
            KeepData = HasFlag(args, "--keep-data"),
            OutputPath = ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "phase-two",
                $"phase-two-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
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
            throw new ArgumentOutOfRangeException("--stripes", "Stripe counts must be powers of two from 1 through 64.");
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
            throw new ArgumentOutOfRangeException(name);
        }

        return values;
    }

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static int ParseBoundedPositiveInt(string[] args, string name, int fallback, int maximum)
    {
        var parsed = ParsePositiveInt(args, name, fallback);
        return parsed <= maximum ? parsed : throw new ArgumentOutOfRangeException(name);
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

internal sealed record PhaseTwoMetric(
    string Engine,
    int StripeCount,
    int PayloadBytes,
    int Concurrency,
    string Operation,
    int Samples,
    double WallMilliseconds,
    double OperationsPerSecond,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double AllocatedBytesPerOperation,
    long ResultChecksum,
    long StorageBytes,
    int StorageFiles);

internal sealed record PhaseTwoCrashMetric(
    int Cycle,
    int AcknowledgedRecords,
    int RecoveredAcknowledgedRecords,
    int TailRecordsReplayed,
    double ReopenMilliseconds,
    bool Passed,
    string? Failure);

internal sealed record PhaseTwoReport
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset CompletedUtc { get; set; }

    public required string Runtime { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required string Processor { get; init; }

    public required string PhysicalDisk { get; init; }

    public required PhaseTwoOptions Options { get; init; }

    public List<PhaseTwoMetric> Metrics { get; init; } = [];

    public List<PhaseTwoCrashMetric> CrashMetrics { get; init; } = [];

    public List<string> Findings { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    public List<string> Failures { get; init; } = [];

    public bool Passed => Failures.Count == 0 && CrashMetrics.All(static metric => metric.Passed);
}

internal readonly record struct PhaseTwoStorageStats(long Bytes, int Files);

internal readonly record struct PhaseTwoCheckpointStats(long Bytes, double Milliseconds);

internal interface IPhaseTwoStore : IAsyncDisposable
{
    string EngineName { get; }

    int StripeCount { get; }

    int Count { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(GateRecord record, CancellationToken cancellationToken = default);

    ValueTask<GateQueryResult> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<PhaseTwoCheckpointStats> CheckpointAsync(CancellationToken cancellationToken = default);

    ValueTask ReopenAsync(CancellationToken cancellationToken = default);

    ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default);

    PhaseTwoStorageStats GetStorageStats();
}
