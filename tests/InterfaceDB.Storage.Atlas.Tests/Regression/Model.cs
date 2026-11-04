// Regression copied from experiments/Atlas.Qualification/Model.cs; only provider configuration is adapted for the promoted hybrid.
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Storage.AtlasCore;
using Microsoft.Extensions.DependencyInjection;

namespace AtlasQualification;

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IRecord : IStoredContract<IRecord>
{
    [SearchIndex(SearchIndexMode.Exact)] string Id { get; set; }
    [SearchIndex] int Value { get; set; }
    byte[] Payload { get; set; }
}

public sealed class Record : IRecord
{
    public string Id { get; set; } = "";
    public int Value { get; set; }
    public byte[] Payload { get; set; } = [];
    public IObjectEnvelope<IRecord>? Envelope { get; private set; }
}

public sealed class Identity : IEnvelopeStorageIdentityFactory<IRecord>
{
    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
    public void InitEnvelope(IObjectEnvelope<IRecord> envelope)
    {
        envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id;
        envelope.Location ??= envelope.EnvelopeCacheKey;
    }
}

public sealed record Config(string DataRoot, StoragePolicy StoragePolicy) : IDbContractConfigDto<IRecord>
{
    public string EnvelopeDICacheKey => "atlas-qualification";
    public string ContractName => "atlas-qualification";
    public string FileExtension => ".mpk";
    public string? ImplementationsPath => null;
    public string? ContractPath => null;
    public string? MigrationsPath => null;
    public string? ActiveVersion => "1.0.0";
    public ContractRepositoryType RepositoryType => ContractRepositoryType.AtlasCore;
}

public static class Setup
{
    public static AtlasCoreOptions Options(int variant, int concurrency = 8) =>
        AtlasCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.WindowsNvme).StoreOptions with
        {
        };

    public static ServiceProvider Create(string root, int variant, string mode = "disabled", int concurrency = 8)
    {
        var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
        {
            DurabilityMode = StorageDurabilityMode.Durable,
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            RaiseNotifications = false, WatchStorageChanges = false, HydrateOnInitialize = false,
            RepositoryNotifications = mode == "disabled" ? null : new() { QueueCapacity = 16_384 }
        };
        var services = new ServiceCollection();
        services.AddAtlasCoreRepository<IRecord>(new Config(root, policy),
            AtlasCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
            {
                StoreOptions = Options(variant, concurrency), EnableBackgroundMaintenance = false,
                CheckpointOnDispose = false
            }, storageIdentityFactory: new Identity());
        return services.BuildServiceProvider();
    }

    public static Record New(int index, int prefixes, bool skew = false) => new()
    {
        Id = $"{(skew ? (index % 10 < 9 ? 0 : (index / 10) % prefixes) : index % prefixes):X3}/item-{index:D8}",
        Payload = Enumerable.Repeat((byte)(index % 251), 512).ToArray()
    };
}
