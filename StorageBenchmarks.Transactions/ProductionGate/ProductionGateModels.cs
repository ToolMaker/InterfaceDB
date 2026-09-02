using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using System.Globalization;

internal enum GateEngine
{
    InterfaceDb,
    Sqlite,
    SqlServer
}

internal enum SqliteClientKind
{
    MicrosoftDataSqlite,
    Dapper
}

internal enum GateQueryKind
{
    Exact,
    Range,
    Prefix,
    Suffix,
    Contains,
    BroadConjunction
}

internal sealed record GateRecord(
    string Id,
    string Name,
    int Age,
    string Role,
    string Content,
    long Revision)
{
    // SQLite exposes INTEGER columns as Int64. This matching constructor lets
    // Dapper materialize the complete object without using an untyped/dynamic row.
    public GateRecord(string id, string name, long age, string role, string content, long revision)
        : this(id, name, checked((int)age), role, content, revision)
    {
    }

    public static GateRecord Create(int ordinal, string idPrefix = "row")
    {
        var region = (ordinal % 4) switch
        {
            0 => "north",
            1 => "south",
            2 => "east",
            _ => "west"
        };
        var suffix = ordinal % 521 == 0 ? "special-tail" : region;
        var content = ordinal % 257 == 0
            ? $"document {ordinal:D8} has the rare-needle marker"
            : $"ordinary document {ordinal:D8} for {region}";

        return new GateRecord(
            $"{idPrefix}-{ordinal:D8}-id",
            $"customer-{ordinal:D8}-{suffix}",
            18 + ordinal % 63,
            ordinal % 2 == 0 ? "student" : "employee",
            content,
            0);
    }

    public long MaterializedChecksum => GateRecordMaterialization.Checksum(
        Id,
        Name,
        Age,
        Role,
        Content,
        Revision);
}

internal readonly record struct GateQueryResult(int Count, long Checksum);

internal static class GateRecordMaterialization
{
    public const string Contract = "GateRecord/FNV1a32-v2/all-six-fields";

    // A stable, inexpensive full-field sink for timed materialization paths. Keeping
    // the value within UInt32 makes aggregate checksums safe for the benchmark's
    // supported record counts while still detecting same-length content mistakes.
    public static long Checksum(
        string id,
        string name,
        int age,
        string role,
        string content,
        long revision)
    {
        var hash = 2166136261u;
        AddString(ref hash, id);
        AddString(ref hash, name);
        AddInt32(ref hash, age);
        AddString(ref hash, role);
        AddString(ref hash, content);
        AddInt64(ref hash, revision);
        return hash;
    }

    private static void AddString(ref uint hash, string value)
    {
        AddInt32(ref hash, value.Length);
        foreach (var character in value)
        {
            AddByte(ref hash, (byte)character);
            AddByte(ref hash, (byte)(character >> 8));
        }
    }

    private static void AddInt32(ref uint hash, int value)
    {
        var bits = unchecked((uint)value);
        AddByte(ref hash, (byte)bits);
        AddByte(ref hash, (byte)(bits >> 8));
        AddByte(ref hash, (byte)(bits >> 16));
        AddByte(ref hash, (byte)(bits >> 24));
    }

    private static void AddInt64(ref uint hash, long value)
    {
        var bits = unchecked((ulong)value);
        for (var shift = 0; shift < 64; shift += 8)
        {
            AddByte(ref hash, (byte)(bits >> shift));
        }
    }

    private static void AddByte(ref uint hash, byte value)
    {
        hash ^= value;
        hash *= 16777619u;
    }
}

internal readonly record struct GateStorageStats(long Bytes, int Files);

internal sealed record GateMetric(
    string Engine,
    int Records,
    string CacheMode,
    string DurabilityMode,
    string Operation,
    int Iterations,
    double TotalMilliseconds,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double AllocatedBytesPerOperation,
    int ResultCount,
    long StorageBytes,
    int StorageFiles);

internal sealed record GateStressMetric(
    string Engine,
    int StartingRecords,
    string CacheMode,
    string DurabilityMode,
    int Concurrency,
    double DurationSeconds,
    long Operations,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long Reads,
    long Inserts,
    long Updates,
    long Deletes,
    int FinalRecords,
    int Errors,
    bool IntegrityPassed);

internal sealed record GateRecoveryMetric(
    int Cycle,
    int RecoveredRecords,
    int RemainingProposalFiles,
    double RecoveryMilliseconds,
    bool Passed);

internal sealed class ProductionGateReport
{
    public string MaterializationContract { get; init; } = GateRecordMaterialization.Contract;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string Runtime { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Processor { get; init; } = string.Empty;
    public ProductionGateOptions Options { get; init; } = new();
    public List<GateMetric> Metrics { get; } = [];
    public List<GateStressMetric> StressMetrics { get; } = [];
    public List<GateRecoveryMetric> RecoveryMetrics { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Failures { get; } = [];
    public bool Passed => Failures.Count == 0
        && StressMetrics.All(metric => metric.Errors == 0 && metric.IntegrityPassed)
        && RecoveryMetrics.All(metric => metric.Passed);
}

internal sealed class ProductionGateOptions
{
    public int[] RecordCounts { get; init; } = [10_000];
    public int QueryIterations { get; init; } = 50;
    public TimeSpan StressDuration { get; init; } = TimeSpan.FromMinutes(5);
    public int[] ConcurrencyLevels { get; init; } = [1, 4, 16];
    public StorageObjectCacheMode[] CacheModes { get; init; } =
        [StorageObjectCacheMode.StrongObject, StorageObjectCacheMode.WeakObject, StorageObjectCacheMode.MetadataOnly];
    public StorageDurabilityMode[] DurabilityModes { get; init; } =
        [StorageDurabilityMode.Performance, StorageDurabilityMode.Durable];
    public bool IncludeSqlServer { get; init; }
    public bool IncludeWriteAblations { get; init; }
    public bool RunStress { get; init; } = true;
    public bool RunCrashRecovery { get; init; } = true;
    public int CrashRecoveryCycles { get; init; } = 5;
    public string OutputPath { get; init; } = Path.Combine(
        "TestResults",
        $"production-gate-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    public string? BaselinePath { get; init; }
    public double MaximumRegressionPercent { get; init; } = 20;
    public bool KeepData { get; init; }
    public bool Quick { get; init; }

    public static ProductionGateOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        return new ProductionGateOptions
        {
            Quick = quick,
            RecordCounts = ParseInts(args, "--records", quick ? [1_000] : [10_000]),
            QueryIterations = ParseInt(args, "--iterations", quick ? 5 : 50),
            StressDuration = ParseDuration(args, "--duration", quick ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(5)),
            ConcurrencyLevels = ParseInts(args, "--concurrency", quick ? [2] : [1, 4, 16]),
            CacheModes = ParseCacheModes(args, quick),
            DurabilityModes = ParseDurabilityModes(args, quick),
            IncludeSqlServer = HasFlag(args, "--include-sqlserver"),
            IncludeWriteAblations = HasFlag(args, "--include-write-ablations"),
            RunStress = !HasFlag(args, "--no-stress"),
            RunCrashRecovery = !HasFlag(args, "--no-crash-recovery"),
            CrashRecoveryCycles = ParseInt(args, "--crash-cycles", quick ? 1 : 5),
            OutputPath = ParseString(args, "--output") ?? Path.Combine(
                "TestResults",
                $"production-gate-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
            BaselinePath = ParseString(args, "--baseline"),
            MaximumRegressionPercent = ParseDouble(args, "--max-regression-percent", 20),
            KeepData = HasFlag(args, "--keep-data")
        };
    }

    private static StorageObjectCacheMode[] ParseCacheModes(string[] args, bool quick)
    {
        var value = ParseString(args, "--cache");
        if (value is null)
        {
            return quick ? [StorageObjectCacheMode.StrongObject] :
                [StorageObjectCacheMode.StrongObject, StorageObjectCacheMode.WeakObject, StorageObjectCacheMode.MetadataOnly];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant() switch
            {
                "strong" or "strongobject" => StorageObjectCacheMode.StrongObject,
                "weak" or "weakobject" => StorageObjectCacheMode.WeakObject,
                "metadata" or "metadataonly" => StorageObjectCacheMode.MetadataOnly,
                _ => throw new ArgumentException($"Unknown cache mode '{item}'.")
            })
            .Distinct()
            .ToArray();
    }

    private static StorageDurabilityMode[] ParseDurabilityModes(string[] args, bool quick)
    {
        var value = ParseString(args, "--durability");
        if (value is null)
        {
            return quick ? [StorageDurabilityMode.Durable] :
                [StorageDurabilityMode.Performance, StorageDurabilityMode.Durable];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant() switch
            {
                "performance" => StorageDurabilityMode.Performance,
                "durable" => StorageDurabilityMode.Durable,
                _ => throw new ArgumentException($"Unknown durability mode '{item}'.")
            })
            .Distinct()
            .ToArray();
    }

    private static int[] ParseInts(string[] args, string name, int[] fallback)
    {
        var value = ParseString(args, name);
        var values = value is null
            ? fallback
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.Parse(item, CultureInfo.InvariantCulture))
                .ToArray();
        if (values.Length == 0 || values.Any(item => item <= 0))
        {
            throw new ArgumentOutOfRangeException(name, "Values must be positive integers.");
        }

        return values.Distinct().ToArray();
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        return parsed > 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static double ParseDouble(string[] args, string name, double fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : double.Parse(value, CultureInfo.InvariantCulture);
        return parsed >= 0 ? parsed : throw new ArgumentOutOfRangeException(name);
    }

    private static TimeSpan ParseDuration(string[] args, string name, TimeSpan fallback)
    {
        var value = ParseString(args, name);
        var parsed = value is null ? fallback : TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        return parsed > TimeSpan.Zero ? parsed : throw new ArgumentOutOfRangeException(name);
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

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal interface IProductionGateStore : IAsyncDisposable
{
    string EngineName { get; }
    string CacheModeName { get; }
    string DurabilityModeName { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask SeedAsync(IReadOnlyList<GateRecord> records, CancellationToken cancellationToken = default);
    ValueTask<GateQueryResult> QueryAsync(GateQueryKind kind, string exactId, CancellationToken cancellationToken = default);
    ValueTask InsertAsync(GateRecord record, CancellationToken cancellationToken = default);
    ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask ReopenAsync(CancellationToken cancellationToken = default);
    GateStorageStats GetStorageStats();
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
internal interface IProductionGateRecord : IStoredContract<IProductionGateRecord>
{
    [SearchIndex(SearchIndexMode.Exact)]
    string Id { get; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix)]
    string Name { get; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
    int Age { get; set; }

    [SearchIndex(SearchIndexMode.Exact)]
    string Role { get; }

    [SearchIndex(SearchIndexMode.Contains)]
    string Content { get; set; }

    long Revision { get; set; }
}

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
internal sealed class ProductionGateRecord : IProductionGateRecord
{
    public ProductionGateRecord(string id, string name, int age, string role, string content, long revision)
    {
        Id = id;
        Name = name;
        Age = age;
        Role = role;
        Content = content;
        Revision = revision;
    }

    public string Id { get; }
    public string Name { get; }
    public int Age { get; set; }
    public string Role { get; }
    public string Content { get; set; }
    public long Revision { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<IProductionGateRecord>? Envelope { get; private set; }

    public GateRecord ToGateRecord() => new(Id, Name, Age, Role, Content, Revision);
}

internal sealed class ProductionGateContractOptions : IDbContractConfigDto<IProductionGateRecord>
{
    public ProductionGateContractOptions(
        string dataRoot,
        StorageObjectCacheMode cacheMode,
        StorageDurabilityMode durabilityMode,
        bool useSidecarWriteLocks = true)
    {
        DataRoot = dataRoot;
        StoragePolicy = StoragePolicy.Default with
        {
            DurabilityMode = durabilityMode,
            ObjectCacheMode = cacheMode,
            HydrateOnInitialize = false,
            MaxConcurrency = Math.Max(1, Math.Min(Environment.ProcessorCount, 16)),
            BulkWriteBatchSize = 4_096,
            FileWriteBufferSize = 16 * 1024,
            RaiseNotifications = false,
            UseSidecarWriteLocks = useSidecarWriteLocks,
            UseSingleWriteBuffer = true,
            UseWriterSemaphore = false,
            WatchStorageChanges = false,
            WriteLockTimeout = TimeSpan.FromSeconds(15),
            WriteLockRetryDelay = TimeSpan.FromMilliseconds(10),
            StaleWriteLockTimeout = TimeSpan.FromSeconds(2)
        };
    }

    public string EnvelopeDICacheKey { get; } = "ProductionGate";
    public string ContractName { get; } = "ProductionGate";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.FileSystem;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Multiple;
    public StoragePolicy? StoragePolicy { get; }
}
