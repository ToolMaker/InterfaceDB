using System.Globalization;

internal sealed record PhaseZeroOptions
{
    public int Records { get; init; } = 300;

    public int ContentBytes { get; init; } = 512;

    public int Iterations { get; init; } = 7;

    public int CrashCycles { get; init; } = 12;

    public long MaxSegmentBytes { get; init; } = 64L * 1024;

    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "phase-zero",
        $"phase-zero-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public bool KeepData { get; init; }

    public static PhaseZeroOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        return new PhaseZeroOptions
        {
            Records = ParsePositiveInt(args, "--records", quick ? 80 : 300),
            ContentBytes = ParsePositiveInt(args, "--payload-bytes", quick ? 262 : 512),
            Iterations = ParsePositiveInt(args, "--iterations", quick ? 3 : 7),
            CrashCycles = ParseNonNegativeInt(args, "--crash-cycles", quick ? 3 : 12),
            MaxSegmentBytes = ParsePositiveLong(args, "--segment-bytes", quick ? 16 * 1024 : 64 * 1024),
            OutputPath = ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "phase-zero",
                $"phase-zero-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
            KeepData = HasFlag(args, "--keep-data")
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

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static int ParseNonNegativeInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;
    }

    private static long ParsePositiveLong(string[] args, string name, long fallback)
    {
        var value = ParseString(args, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record PhaseZeroMetric(
    string Engine,
    string Durability,
    string Scope,
    int Samples,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MeanMicroseconds,
    double OperationsPerSecond,
    double AllocatedBytesPerOperation,
    long StorageBytes,
    int StorageFiles,
    int VerifiedRecords,
    long VerifiedChecksum);

internal sealed record PhaseZeroCrashMetric(
    int Cycle,
    int AcknowledgedRecords,
    int RecoveredRecords,
    long RepairedTailBytes,
    int IgnoredOrphanSegments,
    double RecoveryMilliseconds,
    bool Passed,
    string? Failure);

internal sealed record PhaseZeroReport
{
    public required DateTimeOffset StartedUtc { get; init; }

    public required DateTimeOffset CompletedUtc { get; set; }

    public required string Runtime { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required string Processor { get; init; }

    public required string WorkingDrive { get; init; }

    public required string DirectoryFlushStrategy { get; init; }

    public required PhaseZeroOptions Options { get; init; }

    public List<PhaseZeroMetric> Metrics { get; init; } = [];

    public List<PhaseZeroCrashMetric> CrashMetrics { get; init; } = [];

    public List<string> Failures { get; init; } = [];

    public List<string> Notes { get; init; } = [];

    public double StrictP50ImprovementPercent { get; set; }

    public double StrictP95ImprovementPercent { get; set; }

    public double WriteThroughP50ImprovementPercent { get; set; }

    public double WriteThroughP95ImprovementPercent { get; set; }

    public bool WriteThroughAlternativePassed { get; set; }

    public bool WindowsOrLinuxAdmissionPassed { get; set; }

    public bool Passed => Failures.Count == 0;
}

internal readonly record struct PhaseZeroStorageStats(long Bytes, int Files);

internal interface IPhaseZeroStore : IAsyncDisposable
{
    string EngineName { get; }

    string DurabilityName { get; }

    string Scope { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask AppendAsync(GateRecord record, CancellationToken cancellationToken = default);

    ValueTask<(int Count, long Checksum)> VerifyAsync(CancellationToken cancellationToken = default);

    PhaseZeroStorageStats GetStorageStats();
}
