using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idb.Storage.SegmentCore;

public static class SegmentCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a complete InterfaceDB repository backed by SegmentCore. The upper repository and
    /// query/index layers remain unchanged; only envelope persistence is replaced.
    /// </summary>
    public static IServiceCollection AddSegmentCoreRepository<TContract>(
        this IServiceCollection services,
        IDbContractConfigDto<TContract> contractConfig,
        SegmentCoreRepositoryOptions? segmentCoreOptions = null,
        StoragePolicy? storagePolicy = null,
        IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory = null,
        IEncryptor? encryptor = null,
        StoredRuntimeTypeAllowlist<TContract>? runtimeTypeAllowlist = null)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contractConfig);
        if (contractConfig.RepositoryType is not (
            ContractRepositoryType.None or ContractRepositoryType.SegmentCore))
        {
            throw new InvalidOperationException(
                $"Repository config declares '{contractConfig.RepositoryType}', but SegmentCore registration was requested.");
        }

        var effectiveConfig = contractConfig.RepositoryType == ContractRepositoryType.SegmentCore
            ? contractConfig
            : new SegmentCoreContractConfig<TContract>(contractConfig);
        var databaseKey = ResolveDatabaseKey(effectiveConfig);
        var policy = storagePolicy ?? effectiveConfig.StoragePolicy ?? StoragePolicy.Default;
        var options = SegmentCoreRepositoryOptions.Resolve(policy, segmentCoreOptions);

        services.TryAddSingleton<IEncryptor, EncryptionNone>();
        services.TryAddKeyedSingleton<IDbContractConfigDto<TContract>>(databaseKey, effectiveConfig);
        services.TryAddKeyedSingleton(databaseKey, policy);
        services.TryAddKeyedSingleton(databaseKey, options);
        if (storageIdentityFactory is not null)
        {
            services.Add(ServiceDescriptor.KeyedSingleton(
                typeof(IEnvelopeStorageIdentityFactory<TContract>),
                databaseKey,
                storageIdentityFactory));
        }
        else
        {
            services.TryAddKeyedSingleton<IEnvelopeStorageIdentityFactory<TContract>>(
                databaseKey,
                (_, _) => new FlatFileNameFactory<TContract>(effectiveConfig));
        }

        if (encryptor is not null)
        {
            services.Add(ServiceDescriptor.KeyedSingleton(typeof(IEncryptor), databaseKey, encryptor));
        }
        else
        {
            services.TryAddKeyedSingleton<IEncryptor>(
                databaseKey,
                (sp, _) => sp.GetRequiredService<IEncryptor>());
        }

        services.TryAddKeyedSingleton(
            databaseKey,
            (_, _) => new BinaryObjectSerializer<TContract>(
                MessagePackSerializerOptions.Standard.WithResolver(
                    MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance),
                policy.NormalizedMaximumStoredObjectBytes,
                allowLegacyTypelessPayloads: false,
                runtimeTypeAllowlist));
        services.TryAddKeyedSingleton<IObjectSerializer<TContract>>(databaseKey, (sp, key) =>
        {
            var serializer = sp.GetRequiredKeyedService<BinaryObjectSerializer<TContract>>(key);
            var resolvedEncryptor = sp.GetRequiredKeyedService<IEncryptor>(key);
            return resolvedEncryptor is EncryptionNone
                ? serializer
                : new EncryptingObjectSerializer<TContract>(serializer, resolvedEncryptor);
        });
        services.TryAddKeyedSingleton<
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(
            databaseKey,
            (_, _) => new EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>>());
        services.TryAddKeyedTransient<IObjectEnvelope<TContract>>(
            databaseKey,
            (_, _) => new FileObjectEnvelope<TContract>(effectiveConfig)
            {
                EnvelopeCacheKey = string.Empty,
                VersionTag = effectiveConfig.ActiveVersion ?? "1.0.0"
            });
        services.TryAddKeyedSingleton<IEnvelopeLifecycle<TContract>>(databaseKey, (sp, key) =>
            new EnvelopeLifecycle<TContract>(
                () => sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(key),
                sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key),
                sp.GetRequiredKeyedService<IEncryptor>(key)));
        services.TryAddKeyedSingleton<IEnvelopeRepository<TContract>>(databaseKey, (sp, key) =>
            new SegmentCoreEnvelopeRepository<TContract>(
                sp.GetRequiredKeyedService<IDbContractConfigDto<TContract>>(key),
                sp.GetRequiredKeyedService<StoragePolicy>(key),
                sp.GetRequiredKeyedService<SegmentCoreRepositoryOptions>(key),
                sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key),
                sp.GetRequiredKeyedService<
                    IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(key),
                () => sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(key),
                sp.GetRequiredKeyedService<IEncryptor>(key),
                sp.GetServices<IMigration>(),
                sp.GetKeyedService<IWriteConflictStrategy<TContract>>(key) ?? sp.GetService<IWriteConflictStrategy<TContract>>()));
        services.TryAddKeyedSingleton<SegmentCoreRepositoryFacade<TContract>>(databaseKey, (sp, key) =>
            new SegmentCoreRepositoryFacade<TContract>(
                sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key)));
        services.TryAddKeyedSingleton<ISegmentCoreRepositoryMaintenance<TContract>>(databaseKey, (sp, key) =>
            sp.GetRequiredKeyedService<SegmentCoreRepositoryFacade<TContract>>(key));
        services.TryAddKeyedSingleton<ICompactionMaintenance<TContract>>(databaseKey, (sp, key) =>
            sp.GetRequiredKeyedService<SegmentCoreRepositoryFacade<TContract>>(key));
        services.TryAddKeyedSingleton<IReaderAndWriter<TContract>>(databaseKey, (sp, key) =>
            sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key).ReaderAndWriter);
        services.TryAddKeyedSingleton<IIndexManager<TContract>>(databaseKey, (sp, key) =>
            SearchIndexMetadata.HasIndexes(typeof(TContract))
                ? new SearchIndexManager<TContract>(
                    sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key),
                    sp.GetRequiredKeyedService<StoragePolicy>(key))
                : new PassthroughIndexManager<TContract>(
                    sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key)));
        services.TryAddKeyedSingleton<IRepository<TContract>>(databaseKey, (sp, key) =>
            new InterfaceRepositoryDb<TContract>(
                sp.GetRequiredKeyedService<IIndexManager<TContract>>(key),
                sp.GetRequiredKeyedService<IDbContractConfigDto<TContract>>(key),
                sp.GetRequiredKeyedService<StoragePolicy>(key),
                sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key),
                StorageDurabilityMode.Durable));
        services.TryAddKeyedSingleton<IEnvelopeMetadataRepository<TContract>>(databaseKey, (sp, key) =>
            (IEnvelopeMetadataRepository<TContract>)sp.GetRequiredKeyedService<IRepository<TContract>>(key));
        services.TryAddKeyedSingleton<IRepositoryNotifications<TContract>>(databaseKey, (sp, key) =>
            (IRepositoryNotifications<TContract>)sp.GetRequiredKeyedService<IRepository<TContract>>(key));

        services.TryAddSingleton<IDbContractConfigDto<TContract>>(sp =>
            sp.GetRequiredKeyedService<IDbContractConfigDto<TContract>>(databaseKey));
        services.TryAddSingleton<StoragePolicy>(sp =>
            sp.GetRequiredKeyedService<StoragePolicy>(databaseKey));
        services.TryAddSingleton<SegmentCoreRepositoryOptions>(sp =>
            sp.GetRequiredKeyedService<SegmentCoreRepositoryOptions>(databaseKey));
        services.TryAddSingleton<IEnvelopeStorageIdentityFactory<TContract>>(sp =>
            sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(databaseKey));
        services.TryAddSingleton<IObjectSerializer<TContract>>(sp =>
            sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(databaseKey));
        services.TryAddTransient<IObjectEnvelope<TContract>>(sp =>
            sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(databaseKey));
        services.TryAddSingleton<IEnvelopeLifecycle<TContract>>(sp =>
            sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(databaseKey));
        services.TryAddSingleton<IEnvelopeRepository<TContract>>(sp =>
            sp.GetRequiredKeyedService<SegmentCoreRepositoryFacade<TContract>>(databaseKey));
        services.TryAddSingleton<ISegmentCoreRepositoryMaintenance<TContract>>(sp =>
            sp.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<TContract>>(databaseKey));
        services.TryAddSingleton<ICompactionMaintenance<TContract>>(sp =>
            sp.GetRequiredKeyedService<ICompactionMaintenance<TContract>>(databaseKey));
        services.TryAddSingleton<IReaderAndWriter<TContract>>(sp =>
            sp.GetRequiredKeyedService<IReaderAndWriter<TContract>>(databaseKey));
        services.TryAddSingleton<IIndexManager<TContract>>(sp =>
            sp.GetRequiredKeyedService<IIndexManager<TContract>>(databaseKey));
        services.TryAddSingleton<IRepository<TContract>>(sp =>
            sp.GetRequiredKeyedService<IRepository<TContract>>(databaseKey));
        services.TryAddSingleton<IRepositoryNotifications<TContract>>(sp =>
            (IRepositoryNotifications<TContract>)sp.GetRequiredService<IRepository<TContract>>());
        services.TryAddSingleton<IEnvelopeMetadataRepository<TContract>>(sp =>
            sp.GetRequiredKeyedService<IEnvelopeMetadataRepository<TContract>>(databaseKey));
        return services;
    }

    public static ISegmentCoreRepositoryMaintenance<TContract> GetSegmentCoreMaintenance<TContract>(
        this IServiceProvider serviceProvider,
        string databaseKey)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<TContract>>(
            NormalizeDatabaseKey(databaseKey));
    }

    private static string ResolveDatabaseKey<TContract>(IDbContractConfigDto<TContract> config)
        where TContract : class, IStoredContract<TContract> =>
        NormalizeDatabaseKey(string.IsNullOrWhiteSpace(config.EnvelopeDICacheKey)
            ? config.ContractName
            : config.EnvelopeDICacheKey);

    private static string NormalizeDatabaseKey(string? databaseKey)
    {
        if (string.IsNullOrWhiteSpace(databaseKey))
        {
            throw new InvalidOperationException(
                "InterfaceDB requires EnvelopeDICacheKey or ContractName to define a database key.");
        }

        return databaseKey.Trim();
    }

    private sealed class SegmentCoreContractConfig<TContract>(IDbContractConfigDto<TContract> inner)
        : IDbContractConfigDto<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        public string EnvelopeDICacheKey => inner.EnvelopeDICacheKey;
        public string ContractName => inner.ContractName;
        public string FileExtension => inner.FileExtension;
        public string? ImplementationsPath => inner.ImplementationsPath;
        public string? ContractPath => inner.ContractPath;
        public string? MigrationsPath => inner.MigrationsPath;
        public string? DataRoot => inner.DataRoot;
        public string? ActiveVersion => inner.ActiveVersion;
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => inner.ObjectCardinality;
        public StoragePolicy? StoragePolicy => inner.StoragePolicy;
    }
}
