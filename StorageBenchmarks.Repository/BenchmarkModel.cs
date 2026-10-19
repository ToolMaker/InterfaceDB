using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

[StorageScope(VersionTag = "1.0.0")]
public interface IBenchRecord : IStoredContract<IBenchRecord>
{
    [SearchIndex] string Id { get; }
    string Name { get; }
    [SearchIndex] int Ordinal { get; }
    [SearchIndex] int Group { get; set; }
    long Revision { get; set; }
    byte[] Payload { get; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class BenchRecord : IBenchRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Ordinal { get; set; }
    public int Group { get; set; }
    public long Revision { get; set; }
    public byte[] Payload { get; set; } = [];
    [IgnoreMember] public IObjectEnvelope<IBenchRecord>? Envelope { get; private set; }

    public static BenchRecord Create(int index, int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        new Random(index).NextBytes(payload);
        return new() { Id = $"{unchecked((uint)index * 2654435761u):X8}-{index:D8}", Name = $"Complete object {index:D8}", Ordinal = index, Group = index % 17, Payload = payload };
    }

    public static uint Checksum(IBenchRecord record)
    {
        var hash = 2166136261u;
        AddString(record.Id);
        AddString(record.Name);
        AddInt64(record.Ordinal);
        AddInt64(record.Group);
        AddInt64(record.Revision);
        AddInt64(record.Payload.Length);
        foreach (var value in record.Payload) { AddByte(value); }
        return hash;

        void AddByte(byte value) { hash = (hash ^ value) * 16777619u; }
        void AddInt64(long value) { for (var shift = 0; shift < 64; shift += 8) { AddByte((byte)(value >> shift)); } }
        void AddString(string value) { AddInt64(value.Length); foreach (var character in value) { AddByte((byte)character); AddByte((byte)(character >> 8)); } }
    }
}

internal sealed class BenchConfig(string root, ContractRepositoryType engine, StorageObjectCacheMode cache, RunOptions options, Idb.Diagnostics.IDatabaseTelemetry? telemetry = null)
    : IDbContractConfigDto<IBenchRecord>
{
    public string EnvelopeDICacheKey => "repository-benchmark";
    public string ContractName => "RepositoryBenchmark";
    public string FileExtension => ".mpk";
    public string? ImplementationsPath => null;
    public string? ContractPath => null;
    public string? MigrationsPath => null;
    public string? DataRoot => root;
    public string? ActiveVersion => "1.0.0";
    public ContractRepositoryType RepositoryType => engine;
    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
    public StoragePolicy? StoragePolicy { get; } = StoragePolicy.Default with
    {
        Telemetry = telemetry,
        DurabilityMode = StorageDurabilityMode.Durable,
        ObjectCacheMode = cache,
        HydrateOnInitialize = false,
        RaiseNotifications = false,
        WatchStorageChanges = false,
        MaxConcurrency = 4,
        MaxReadConcurrency = options.ReadConcurrency,
        BulkWriteBatchSize = 128,
        UseSingleWriteBuffer = true,
        UseWriterSemaphore = false,
        UseSidecarWriteLocks = true
    };
}

internal sealed class BenchIdentity(BenchConfig config) : IEnvelopeStorageIdentityFactory<IBenchRecord>
{
    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
    public void InitEnvelope(IObjectEnvelope<IBenchRecord> envelope)
    {
        envelope.EnvelopeCacheKey = envelope.ObjectInstance?.Id ?? throw new InvalidOperationException("Missing object.");
        envelope.Location ??= Path.Combine(config.DataRoot!, envelope.VersionTag, envelope.EnvelopeCacheKey + config.FileExtension);
    }
}

internal sealed class BenchSession(ServiceProvider provider, IRepository<IBenchRecord> repository) : IAsyncDisposable
{
    private int _disposed;
    public IRepository<IBenchRecord> Repository { get; } = repository;
    public static async Task<BenchSession> OpenAsync(BenchConfig config)
    {
        var services = new ServiceCollection();
        var identity = new BenchIdentity(config);
        switch (config.RepositoryType)
        {
            case ContractRepositoryType.FileSystem:
                services.AddRepository<IBenchRecord>(config, storageIdentityFactory: identity);
                break;
            case ContractRepositoryType.SegmentCore:
                services.AddSegmentCoreRepository<IBenchRecord>(config, new SegmentCoreRepositoryOptions
                {
                    HardwareProfile = StorageHardwareProfile.Custom,
                    EnableBackgroundMaintenance = false,
                    StoreOptions = new SegmentCoreOptions
                    {
                        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
                        LogOptions = new StripedObjectLogProbeOptions { StripeCount = 4, DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush },
                        ReadOptions = new SegmentCoreReadOptions { MaximumParallelReads = config.StoragePolicy!.MaxReadConcurrency, StreamingBatchRecords = 256 }
                    }
                }, storageIdentityFactory: identity);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(config));
        }
        var provider = services.BuildServiceProvider();
        try
        {
            var repository = provider.GetRequiredService<IRepository<IBenchRecord>>();
            await repository.InitializeAsync();
            return new(provider, repository);
        }
        catch { await provider.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { await provider.DisposeAsync(); }
    }
}

internal sealed record RunOptions(bool Smoke, int Records, int Iterations, int Warmup, int BatchSize, int PayloadBytes, int ReadConcurrency,
    ContractRepositoryType[] Engines, StorageObjectCacheMode[] CacheModes, string Label, string? Output, string? DataRoot, bool KeepData)
{
    public static RunOptions Parse(string[] args)
    {
        string? Value(string name) { var index = Array.IndexOf(args, name); return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing value for {name}."); }
        int Number(string name, int fallback, int minimum, int maximum) { var result = int.Parse(Value(name) ?? fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)); return result >= minimum && result <= maximum ? result : throw new ArgumentOutOfRangeException(name); }
        var smoke = args.Contains("--smoke");
        var records = Number("--records", smoke ? 32 : 2000, 4, 1_000_000);
        return new(smoke, records, smoke ? 1 : Number("--iterations", 7, 1, 10_000), smoke ? 0 : Number("--warmup", 2, 0, 1000),
            Math.Min(records, Number("--batch", 128, 1, 4096)), Number("--payload-bytes", 512, 0, 1_048_576), Number("--read-concurrency", 4, 1, 64),
            (Value("--engines") ?? "FileSystem,SegmentCore").Split(',').Select(value => Enum.Parse<ContractRepositoryType>(value, ignoreCase: true)).ToArray(),
            (Value("--cache-modes") ?? "MetadataOnly,WeakObject,StrongObject").Split(',').Select(value => Enum.Parse<StorageObjectCacheMode>(value, ignoreCase: true)).ToArray(),
            Value("--label") ?? "current", Value("--output"), Value("--data-root"), args.Contains("--keep-data"));
    }
}
