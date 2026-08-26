using System.Globalization;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;

internal sealed record SegmentCoreScaleGateOptions
{
    public int Records { get; init; } = 100_000;
    public int PayloadBytes { get; init; } = 256;
    public int BatchSize { get; init; } = 1_000;
    public int Concurrency { get; init; } = 16;
    public int Stripes { get; init; } = 8;
    public int SampleReads { get; init; } = 10_000;
    public int CheckpointEveryBatches { get; init; }
    public bool RequireDataLargerThanManagedMemory { get; init; }
    public SegmentCorePrimaryIndexMode PrimaryIndexMode { get; init; } =
        SegmentCorePrimaryIndexMode.Automatic;
    public int PagingThreshold { get; init; } = 100_000;
    public ObjectLogDurabilityStrategy DurabilityStrategy { get; init; } =
        ObjectLogDurabilityStrategy.WriteThrough;
    public bool KeepData { get; init; }
    public string RootPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "segment-core-scale",
        $"segment-core-scale-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public static SegmentCoreScaleGateOptions Parse(string[] args)
    {
        var records = ParseInt(args, "--records", 100_000, 1, 10_000_000);
        var durability = (Read(args, "--durability") ?? "write-through").ToLowerInvariant() switch
        {
            "write-through" or "writethrough" => ObjectLogDurabilityStrategy.WriteThrough,
            "explicit" or "explicit-flush" => ObjectLogDurabilityStrategy.ExplicitFlush,
            _ => throw new ArgumentOutOfRangeException("--durability")
        };
        var stripes = ParseInt(args, "--stripes", 8, 1, 64);
        if (!System.Numerics.BitOperations.IsPow2((uint)stripes))
        {
            throw new ArgumentOutOfRangeException("--stripes", "Stripe count must be a power of two.");
        }

        var primaryIndexMode = (Read(args, "--primary-index") ?? "automatic").ToLowerInvariant() switch
        {
            "automatic" or "auto" => SegmentCorePrimaryIndexMode.Automatic,
            "resident" or "memory" => SegmentCorePrimaryIndexMode.Resident,
            "paged" or "disk" => SegmentCorePrimaryIndexMode.Paged,
            _ => throw new ArgumentOutOfRangeException(
                "--primary-index",
                "Primary index mode must be 'automatic', 'resident', or 'paged'.")
        };

        return new SegmentCoreScaleGateOptions
        {
            Records = records,
            PayloadBytes = ParseInt(args, "--payload-bytes", 256, sizeof(int), 1024 * 1024),
            BatchSize = ParseInt(args, "--batch-size", 1_000, 1, 100_000),
            Concurrency = ParseInt(args, "--concurrency", 16, 1, 256),
            Stripes = stripes,
            SampleReads = ParseInt(args, "--sample-reads", 10_000, 1, 1_000_000),
            CheckpointEveryBatches = ParseInt(args, "--checkpoint-every-batches", 0, 0, 1_000_000),
            RequireDataLargerThanManagedMemory = HasFlag(args, "--require-larger-than-memory"),
            PrimaryIndexMode = primaryIndexMode,
            PagingThreshold = ParseInt(args, "--paging-threshold", 100_000, 1, 100_000_000),
            DurabilityStrategy = durability,
            KeepData = HasFlag(args, "--keep-data"),
            RootPath = Read(args, "--root") ?? string.Empty,
            OutputPath = Read(args, "--output") ?? Path.Combine(
                "artifacts",
                "segment-core-scale",
                $"segment-core-scale-{records}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
        };
    }

    private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var text = Read(args, name);
        var value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
        return value >= minimum && value <= maximum
            ? value
            : throw new ArgumentOutOfRangeException(name);
    }

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

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record SegmentCoreScaleMetric(
    string Name,
    int Operations,
    TimeSpan Elapsed,
    double OperationsPerSecond,
    long AllocatedBytes);

internal sealed record SegmentCoreScaleGateReport
{
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required SegmentCoreScaleGateOptions Options { get; init; }
    public required string StoreRoot { get; init; }
    public long AvailableManagedMemoryBytes { get; init; }
    public long LivePayloadBytes { get; set; }
    public double DataToManagedMemoryRatio { get; set; }
    public bool DataLargerThanManagedMemory { get; set; }
    public bool ReopenedWithPagedPrimaryIndex { get; set; }
    public long WalRecoveryPrefixBytesSkipped { get; set; }
    public int PeriodicCheckpoints { get; set; }
    public List<SegmentCoreScaleMetric> Metrics { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    public bool Passed => Failures.Count == 0;
}
