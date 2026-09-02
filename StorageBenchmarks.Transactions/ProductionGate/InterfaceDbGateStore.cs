using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Indexes;
using Microsoft.Extensions.DependencyInjection;

internal sealed class InterfaceDbGateStore : IProductionGateStore
{
    private readonly string _dataRoot;
    private readonly StorageObjectCacheMode _cacheMode;
    private readonly StorageDurabilityMode _durabilityMode;
    private readonly bool _useSearchIndexes;
    private readonly bool _useSidecarWriteLocks;
    private ServiceProvider? _provider;
    private IRepository<IProductionGateRecord>? _repository;

    public InterfaceDbGateStore(
        string dataRoot,
        StorageObjectCacheMode cacheMode,
        StorageDurabilityMode durabilityMode,
        bool useSearchIndexes = true,
        bool useSidecarWriteLocks = true)
    {
        _dataRoot = dataRoot;
        _cacheMode = cacheMode;
        _durabilityMode = durabilityMode;
        _useSearchIndexes = useSearchIndexes;
        _useSidecarWriteLocks = useSidecarWriteLocks;
    }

    public string EngineName => (_useSearchIndexes, _useSidecarWriteLocks) switch
    {
        (false, _) => "InterfaceDB no search indexes",
        (_, false) => "InterfaceDB no sidecar locks",
        _ => "InterfaceDB"
    };
    public string CacheModeName => _cacheMode.ToString();
    public string DurabilityModeName => _durabilityMode.ToString();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is not null)
        {
            throw new InvalidOperationException("InterfaceDB gate store is already initialized.");
        }

        Directory.CreateDirectory(_dataRoot);
        var options = new ProductionGateContractOptions(
            _dataRoot,
            _cacheMode,
            _durabilityMode,
            _useSidecarWriteLocks);
        var services = new ServiceCollection();
        services.AddRepository<IProductionGateRecord>(
            options,
            storageIdentityFactory: new ProductionGateIdentityFactory(options));
        if (!_useSearchIndexes)
        {
            services.AddKeyedSingleton<IIndexManager<IProductionGateRecord>>(
                options.EnvelopeDICacheKey,
                (serviceProvider, key) => new PassthroughIndexManager<IProductionGateRecord>(
                    serviceProvider.GetRequiredKeyedService<IEnvelopeRepository<IProductionGateRecord>>(key)));
        }

        _provider = services.BuildServiceProvider();
        _repository = _provider.GetRequiredService<IRepository<IProductionGateRecord>>();
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SeedAsync(
        IReadOnlyList<GateRecord> records,
        CancellationToken cancellationToken = default)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Repository.SaveAsync(ToStorageRecord(record), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GateQueryResult> QueryAsync(
        GateQueryKind kind,
        string exactId,
        CancellationToken cancellationToken = default)
    {
        var materialized = new List<IProductionGateRecord>();
        var results = kind switch
        {
            GateQueryKind.Exact => Repository.SearchManyAsync(record => record.Id == exactId, cancellationToken),
            GateQueryKind.Range => Repository.SearchManyAsync(record => record.Age >= 75, cancellationToken),
            GateQueryKind.Prefix => Repository.SearchManyAsync(record => record.Name.StartsWith("customer-00000"), cancellationToken),
            GateQueryKind.Suffix => Repository.SearchManyAsync(record => record.Name.EndsWith("special-tail"), cancellationToken),
            GateQueryKind.Contains => Repository.SearchManyAsync(record => record.Content.Contains("rare-needle"), cancellationToken),
            GateQueryKind.BroadConjunction => Repository.SearchManyAsync(
                record => record.Role == "student" && record.Age >= 40,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        await foreach (var record in results.ConfigureAwait(false))
        {
            materialized.Add(record);
        }

        long checksum = 0;
        foreach (var record in materialized)
        {
            checksum += MaterializedChecksum(record);
        }

        GC.KeepAlive(materialized);
        return new GateQueryResult(materialized.Count, checksum);
    }

    public async ValueTask InsertAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        await Repository.SaveAsync(ToStorageRecord(record), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var existing = await Repository.SearchFirstAsync(
            candidate => candidate.Id == record.Id,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        existing.Age = record.Age;
        existing.Content = record.Content;
        existing.Revision = record.Revision;
        await Repository.SaveAsync(existing, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var existing = await Repository.SearchFirstAsync(
            candidate => candidate.Id == id,
            cancellationToken).ConfigureAwait(false);
        return existing is not null
            && await Repository.DeleteAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, GateRecord>(StringComparer.Ordinal);
        await foreach (var record in Repository.QueryManyAsync(static _ => true, cancellationToken).ConfigureAwait(false))
        {
            var materialized = ((ProductionGateRecord)record).ToGateRecord();
            if (!result.TryAdd(materialized.Id, materialized))
            {
                throw new InvalidOperationException($"InterfaceDB contains duplicate logical id '{materialized.Id}'.");
            }
        }

        return result;
    }

    public async ValueTask ReopenAsync(CancellationToken cancellationToken = default)
    {
        await DisposeProviderAsync().ConfigureAwait(false);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public GateStorageStats GetStorageStats()
    {
        if (!Directory.Exists(_dataRoot))
        {
            return default;
        }

        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            bytes += info.Length;
            files++;
        }

        return new GateStorageStats(bytes, files);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeProviderAsync().ConfigureAwait(false);
    }

    private IRepository<IProductionGateRecord> Repository =>
        _repository ?? throw new InvalidOperationException("InterfaceDB gate store is not initialized.");

    private async ValueTask DisposeProviderAsync()
    {
        _repository = null;
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            _provider = null;
        }
    }

    private static ProductionGateRecord ToStorageRecord(GateRecord record) =>
        new(record.Id, record.Name, record.Age, record.Role, record.Content, record.Revision);

    private static long MaterializedChecksum(IProductionGateRecord record) =>
        GateRecordMaterialization.Checksum(
            record.Id,
            record.Name,
            record.Age,
            record.Role,
            record.Content,
            record.Revision);
}

internal sealed class ProductionGateIdentityFactory : IEnvelopeStorageIdentityFactory<IProductionGateRecord>
{
    private readonly ProductionGateContractOptions _options;

    public ProductionGateIdentityFactory(ProductionGateContractOptions options)
    {
        _options = options;
    }

    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

    public void InitEnvelope(IObjectEnvelope<IProductionGateRecord> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var record = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope must contain a production-gate record.");
        envelope.EnvelopeCacheKey = string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)
            ? record.Id
            : envelope.EnvelopeCacheKey;
        envelope.Location ??= Path.Combine(
            _options.DataRoot!,
            envelope.VersionTag,
            envelope.EnvelopeCacheKey + _options.FileExtension);
    }
}
