namespace Idb.Libraries.Implementation.Extensions
{
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Enums;
    using Idb.Libraries.Abstractions.Options;
    using Idb.Libraries.Abstractions.Services;
    using Idb.Libraries.Abstractions.Services.Cache;
    using Idb.Libraries.Abstractions.Services.Encryption;
    using Idb.Libraries.Abstractions.Services.Encryption.Options;
    using Idb.Libraries.Abstractions.Services.Indexes;
    using Idb.Libraries.Abstractions.Services.Migrations;
    using Idb.Libraries.Abstractions.Services.Repository;
    using Idb.Libraries.Abstractions.Services.Serialization;
    using Idb.Libraries.Implementation.Cache;
    using Idb.Libraries.Implementation.Encryption;
    using Idb.Libraries.Implementation.Indexes;
    using Idb.Libraries.Implementation.Migrations;
    using Idb.Libraries.Implementation.Repositories;
    using Idb.Libraries.Implementation.Serialization;
    using MessagePack;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;

    public static class RepositoryServiceCollectionExtensions
    {
        public static IServiceCollection AddInterfaceDbEncryption(
            this IServiceCollection services,
            IEncryptor encryptor)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(encryptor);
            services.Replace(ServiceDescriptor.Singleton(encryptor));
            return services;
        }

        public static IServiceCollection AddInterfaceDbEncryption(
            this IServiceCollection services,
            string databaseName,
            IEncryptor encryptor)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(encryptor);
            services.TryAdd(ServiceDescriptor.KeyedSingleton(
                typeof(IEncryptor),
                NormalizeDatabaseKey(databaseName),
                encryptor));
            return services;
        }

        public static IServiceCollection AddInterfaceDbAesGcmEncryption(
            this IServiceCollection services,
            IKeyProvider keyProvider,
            AesGcmEncryptorOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(keyProvider);
            services.Replace(ServiceDescriptor.Singleton<IKeyProvider>(keyProvider));
            services.Replace(ServiceDescriptor.Singleton(options ?? new AesGcmEncryptorOptions()));
            services.Replace(ServiceDescriptor.Singleton<IEncryptor, AesGcmEncryptor>());
            return services;
        }

        public static IServiceCollection AddInterfaceDbAesGcmEncryption(
            this IServiceCollection services,
            string databaseName,
            IKeyProvider keyProvider,
            AesGcmEncryptorOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(keyProvider);

            var key = NormalizeDatabaseKey(databaseName);
            services.TryAdd(ServiceDescriptor.KeyedSingleton(
                typeof(IKeyProvider),
                key,
                keyProvider));
            services.TryAdd(ServiceDescriptor.KeyedSingleton(
                typeof(AesGcmEncryptorOptions),
                key,
                options ?? new AesGcmEncryptorOptions()));
            services.TryAddKeyedSingleton<IEncryptor>(key, (sp, serviceKey) =>
                new AesGcmEncryptor(
                    sp.GetRequiredKeyedService<IKeyProvider>(serviceKey),
                    sp.GetRequiredKeyedService<AesGcmEncryptorOptions>(serviceKey)));
            return services;
        }

        public static IServiceCollection AddInterfaceDbWindowsCredentialEncryption(
            this IServiceCollection services,
            string databaseName,
            AesGcmEncryptorOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var keyStore = new WindowsCredentialEncryptionKeyStore(databaseName);
            services.Replace(ServiceDescriptor.Singleton<IEncryptionKeyStore>(keyStore));
            return services.AddInterfaceDbAesGcmEncryption(keyStore, options);
        }

        public static IServiceCollection AddInterfaceDbWindowsCredentialEncryption(
            this IServiceCollection services,
            string registrationKey,
            string credentialDatabaseName,
            AesGcmEncryptorOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var keyStore = new WindowsCredentialEncryptionKeyStore(credentialDatabaseName);
            services.TryAdd(ServiceDescriptor.KeyedSingleton(
                typeof(IEncryptionKeyStore),
                NormalizeDatabaseKey(registrationKey),
                keyStore));
            return services.AddInterfaceDbAesGcmEncryption(registrationKey, keyStore, options);
        }

        public static IServiceCollection AddRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType = MigrationStrategyType.None,
            StoragePolicy? storagePolicy = null,
            IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory = null,
            StorageDurabilityMode? durabilityMode = null)
            where TContract : class, IStoredContract<TContract>
        {
            ArgumentNullException.ThrowIfNull(contractConfig);
            return contractConfig.RepositoryType switch
            {
                ContractRepositoryType.None or ContractRepositoryType.FileSystem =>
                    services.AddFileRepository(
                        contractConfig,
                        strategyType,
                        storagePolicy,
                        storageIdentityFactory,
                        durabilityMode),
                _ => throw new InvalidOperationException(
                    $"Unsupported repository type '{contractConfig.RepositoryType}'.")
            };
        }

        public static IServiceCollection AddRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            string databaseKey,
            MigrationStrategyType strategyType = MigrationStrategyType.None,
            StoragePolicy? storagePolicy = null,
            IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory = null,
            StorageDurabilityMode? durabilityMode = null)
            where TContract : class, IStoredContract<TContract>
        {
            ArgumentNullException.ThrowIfNull(contractConfig);
            return services.AddRepository(
                WithDatabaseKey(contractConfig, databaseKey),
                strategyType,
                storagePolicy,
                storageIdentityFactory,
                durabilityMode);
        }

        public static IServiceCollection AddFileRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType = MigrationStrategyType.None,
            StoragePolicy? storagePolicy = null,
            IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory = null,
            StorageDurabilityMode? durabilityMode = null)
            where TContract : class, IStoredContract<TContract>
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(contractConfig);
            ValidateRepositoryType(contractConfig.RepositoryType, ContractRepositoryType.FileSystem);
            return AddFileRepositoryCore(
                services,
                contractConfig,
                strategyType,
                storagePolicy,
                storageIdentityFactory,
                durabilityMode);
        }

        public static IServiceCollection AddFileRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            string databaseKey,
            MigrationStrategyType strategyType = MigrationStrategyType.None,
            StoragePolicy? storagePolicy = null,
            IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory = null,
            StorageDurabilityMode? durabilityMode = null)
            where TContract : class, IStoredContract<TContract>
        {
            ArgumentNullException.ThrowIfNull(contractConfig);
            return services.AddFileRepository(
                WithDatabaseKey(contractConfig, databaseKey),
                strategyType,
                storagePolicy,
                storageIdentityFactory,
                durabilityMode);
        }

        public static IRepository<TContract> GetInterfaceDbRepository<TContract>(
            this IServiceProvider serviceProvider,
            string databaseKey)
            where TContract : class, IStoredContract<TContract>
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            return serviceProvider.GetRequiredKeyedService<IRepository<TContract>>(
                NormalizeDatabaseKey(databaseKey));
        }

        private static IServiceCollection AddFileRepositoryCore<TContract>(
            IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType,
            StoragePolicy? storagePolicy,
            IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory,
            StorageDurabilityMode? durabilityMode)
            where TContract : class, IStoredContract<TContract>
        {
            ValidateRepositoryType(contractConfig.RepositoryType, ContractRepositoryType.FileSystem);

            var databaseKey = ResolveDatabaseKey(contractConfig);
            var internalKey = new InternalDatabaseServiceKey(databaseKey, typeof(TContract));
            var configuredStoragePolicy = storagePolicy ?? contractConfig.StoragePolicy ?? StoragePolicy.Default;
            var effectiveStoragePolicy = durabilityMode is null
                ? configuredStoragePolicy
                : configuredStoragePolicy with { DurabilityMode = durabilityMode.Value };
            var hasExistingDatabaseRegistration = services.Any(descriptor =>
                descriptor.ServiceType == typeof(RegistrationMarker<TContract>)
                && descriptor.ImplementationInstance is RegistrationMarker<TContract>);
            var hasUnkeyedCustomFactory = storageIdentityFactory is null
                && !hasExistingDatabaseRegistration
                && services.Any(descriptor =>
                    !descriptor.IsKeyedService
                    && descriptor.ServiceType == typeof(IEnvelopeStorageIdentityFactory<TContract>));

            if (!TryAddRegistrationMarker(services, databaseKey, contractConfig))
            {
                return services;
            }

            services.TryAddSingleton<IEncryptor, EncryptionNone>();
            services.TryAddKeyedSingleton<IDbContractConfigDto<TContract>>(databaseKey, contractConfig);
            services.TryAddKeyedSingleton(databaseKey, effectiveStoragePolicy);
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
                    (sp, _) => hasUnkeyedCustomFactory
                        ? sp.GetRequiredService<IEnvelopeStorageIdentityFactory<TContract>>()
                        : new FlatFileNameFactory<TContract>(contractConfig));
            }
            services.TryAddKeyedSingleton<IEncryptor>(
                databaseKey,
                (sp, _) => sp.GetRequiredService<IEncryptor>());

            services.TryAddKeyedSingleton(
                databaseKey,
                (_, _) => new BinaryObjectSerializer<TContract>(
                    MessagePackSerializerOptions.Standard.WithResolver(
                        MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance)));
            services.TryAddKeyedSingleton<IObjectSerializer<TContract>>(databaseKey, (sp, key) =>
            {
                var serializer = sp.GetRequiredKeyedService<BinaryObjectSerializer<TContract>>(key);
                var encryptor = sp.GetRequiredKeyedService<IEncryptor>(key);
                return encryptor is EncryptionNone
                    ? serializer
                    : new EncryptingObjectSerializer<TContract>(serializer, encryptor);
            });
            services.TryAddKeyedSingleton<
                IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(
                databaseKey,
                (_, _) => new EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>>());
            services.TryAddKeyedTransient<IObjectEnvelope<TContract>>(
                databaseKey,
                (_, _) => new FileObjectEnvelope<TContract>(contractConfig)
                {
                    EnvelopeCacheKey = string.Empty,
                    VersionTag = contractConfig.ActiveVersion ?? "1.0.0"
                });
            services.TryAddKeyedSingleton<IEnvelopeLifecycle<TContract>>(databaseKey, (sp, key) =>
                new EnvelopeLifecycle<TContract>(
                    () => sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key),
                    sp.GetRequiredKeyedService<IEncryptor>(key)));

            // File-provider write locking. Future providers should register their own IStorageWriteLock
            // implementation from their provider-specific registration path.
            services.TryAddKeyedSingleton<IStorageWriteLock>(
                internalKey,
                (sp, _) => new SidecarFileWriteLock<TContract>(
                    sp.GetRequiredKeyedService<StoragePolicy>(databaseKey)));
            services.TryAddKeyedSingleton<IFileWriteCoordinator>(internalKey, (sp, _) =>
                new CoalescingFileWriteCoordinator<TContract>(
                    sp.GetRequiredKeyedService<IStorageWriteLock>(internalKey),
                    contractConfig,
                    sp.GetRequiredKeyedService<StoragePolicy>(databaseKey)));
            services.TryAddKeyedSingleton<IEncryptionRotationOperation<TContract>>(
                databaseKey,
                (sp, key) => new FileSystemEncryptionRotationOperation<TContract>(
                    contractConfig,
                    sp.GetRequiredKeyedService<IEncryptor>(key),
                    sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey)));
            services.TryAddKeyedSingleton<IEnvelopeStorageChangeSource<TContract>>(
                databaseKey,
                (sp, _) => new FileEnvelopeStorageChangeSource<TContract>(
                    contractConfig,
                    sp.GetRequiredKeyedService<StoragePolicy>(databaseKey)));
            services.TryAddKeyedSingleton<IEnvelopeStorageChangeHandler<TContract>>(
                databaseKey,
                (sp, key) => new FileEnvelopeStorageChangeHandler<TContract>(
                    contractConfig,
                    sp.GetRequiredKeyedService<StoragePolicy>(key),
                    sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                    sp.GetRequiredKeyedService<
                        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(key)));
            services.TryAddKeyedTransient<IObjectReader<TContract>>(databaseKey, (sp, key) =>
                new FileObjectReader<TContract>(
                    sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                    sp.GetRequiredKeyedService<
                        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(key),
                    contractConfig,
                    sp.GetRequiredKeyedService<StoragePolicy>(key),
                    sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey),
                    sp.GetRequiredKeyedService<IEnvelopeStorageChangeSource<TContract>>(key)));
            services.TryAddKeyedTransient<IObjectWriter<TContract>>(databaseKey, (sp, key) =>
                new FileObjectWriter<TContract>(
                    sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                    contractConfig,
                    sp.GetRequiredKeyedService<StoragePolicy>(key),
                    sp.GetRequiredKeyedService<
                        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(key),
                    sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey),
                    sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key)));
            services.TryAddKeyedTransient<IReaderAndWriter<TContract>>(databaseKey, (sp, key) =>
                new ObjectReaderAndWriter<TContract>(
                    sp.GetRequiredKeyedService<IObjectReader<TContract>>(key),
                    sp.GetRequiredKeyedService<IObjectWriter<TContract>>(key)));

            if (strategyType != MigrationStrategyType.None)
            {
                services.TryAddKeyedSingleton<IMigrationStrategy<TContract>>(databaseKey, (sp, key) =>
                {
                    var migrations = sp.GetServices<IMigration>()
                        .Where(migration => typeof(TContract).IsAssignableFrom(migration.GetType()))
                        .ToList();
                    return strategyType switch
                    {
                        MigrationStrategyType.Lazy => new FileSystemLazyMigrationStrategy<TContract>(
                            contractConfig,
                            sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                            sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key),
                            migrations,
                            sp.GetRequiredKeyedService<IEncryptor>(key),
                            sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey),
                            sp.GetRequiredKeyedService<StoragePolicy>(key),
                            sp.GetRequiredKeyedService<IObjectReader<TContract>>(key),
                            sp.GetRequiredKeyedService<IObjectWriter<TContract>>(key)),
                        MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant =>
                            new FileSystemEagerMigrationStrategy<TContract>(
                                contractConfig,
                                sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(key),
                                migrations,
                                strategyType,
                                sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(key),
                                sp.GetRequiredKeyedService<IEncryptor>(key),
                                sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey),
                                sp.GetRequiredKeyedService<StoragePolicy>(key),
                                sp.GetRequiredKeyedService<IObjectReader<TContract>>(key),
                                sp.GetRequiredKeyedService<IObjectWriter<TContract>>(key)),
                        _ => throw new InvalidOperationException(
                            $"Unsupported migration strategy '{strategyType}'.")
                    };
                });
            }

            services.TryAddKeyedSingleton<IEnvelopeRepository<TContract>>(databaseKey, (sp, key) =>
                new FileEnvelopeRepository<TContract>(
                    contractConfig,
                    sp.GetRequiredKeyedService<IReaderAndWriter<TContract>>(key),
                    sp.GetRequiredKeyedService<
                        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(key),
                    sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey),
                    sp.GetRequiredKeyedService<StoragePolicy>(key),
                    () => sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(key),
                    sp.GetKeyedService<IMigrationStrategy<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeStorageChangeSource<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeStorageChangeHandler<TContract>>(key)));
            services.TryAddKeyedSingleton<FileEnvelopeRepositoryFacade<TContract>>(databaseKey, (sp, key) =>
                new FileEnvelopeRepositoryFacade<TContract>(
                    sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key)));
            services.TryAddKeyedSingleton<IIndexManager<TContract>>(databaseKey, (sp, key) =>
                SearchIndexDiscovery.Discover(typeof(TContract)).Count == 0
                    ? new PassthroughIndexManager<TContract>(
                        sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key))
                    : new SearchIndexManager<TContract>(
                        sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key),
                        sp.GetRequiredKeyedService<StoragePolicy>(key)));
            services.TryAddKeyedSingleton<IRepository<TContract>>(databaseKey, (sp, key) =>
                new InterfaceRepositoryDb<TContract>(
                    sp.GetRequiredKeyedService<IIndexManager<TContract>>(key),
                    sp.GetRequiredKeyedService<IDbContractConfigDto<TContract>>(key),
                    sp.GetRequiredKeyedService<StoragePolicy>(key),
                    sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(key),
                    sp.GetRequiredKeyedService<IEnvelopeRepository<TContract>>(key),
                    sp.GetRequiredKeyedService<StoragePolicy>(key).DurabilityMode));
            services.TryAddKeyedSingleton<IEnvelopeMetadataRepository<TContract>>(databaseKey, (sp, key) =>
                (IEnvelopeMetadataRepository<TContract>)sp.GetRequiredKeyedService<IRepository<TContract>>(key));

            AddUnkeyedCompatibilityAliases<TContract>(
                services,
                databaseKey,
                internalKey,
                hasUnkeyedCustomFactory,
                strategyType != MigrationStrategyType.None);
            return services;
        }

        private static void AddUnkeyedCompatibilityAliases<TContract>(
            IServiceCollection services,
            string databaseKey,
            object internalKey,
            bool hasUnkeyedCustomFactory,
            bool hasMigrationStrategy)
            where TContract : class, IStoredContract<TContract>
        {
            services.TryAddSingleton<IDbContractConfigDto<TContract>>(sp =>
                sp.GetRequiredKeyedService<IDbContractConfigDto<TContract>>(databaseKey));
            if (!hasUnkeyedCustomFactory)
            {
                services.TryAddSingleton<IEnvelopeStorageIdentityFactory<TContract>>(sp =>
                    sp.GetRequiredKeyedService<IEnvelopeStorageIdentityFactory<TContract>>(databaseKey));
            }

            services.TryAddSingleton<StoragePolicy>(sp =>
                sp.GetRequiredKeyedService<StoragePolicy>(databaseKey));
            services.TryAddSingleton<BinaryObjectSerializer<TContract>>(sp =>
                sp.GetRequiredKeyedService<BinaryObjectSerializer<TContract>>(databaseKey));
            services.TryAddSingleton<IObjectSerializer<TContract>>(sp =>
                sp.GetRequiredKeyedService<IObjectSerializer<TContract>>(databaseKey));
            services.TryAddSingleton<
                IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(sp =>
                sp.GetRequiredKeyedService<
                    IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>>(databaseKey));
            services.TryAddTransient<IObjectEnvelope<TContract>>(sp =>
                sp.GetRequiredKeyedService<IObjectEnvelope<TContract>>(databaseKey));
            services.TryAddSingleton<IEnvelopeLifecycle<TContract>>(sp =>
                sp.GetRequiredKeyedService<IEnvelopeLifecycle<TContract>>(databaseKey));
            services.TryAddSingleton<IStorageWriteLock>(sp =>
                sp.GetRequiredKeyedService<IStorageWriteLock>(internalKey));
            services.TryAddSingleton<IFileWriteCoordinator>(sp =>
                sp.GetRequiredKeyedService<IFileWriteCoordinator>(internalKey));
            services.TryAddSingleton<IEncryptionRotationOperation<TContract>>(sp =>
                sp.GetRequiredKeyedService<IEncryptionRotationOperation<TContract>>(databaseKey));
            services.TryAddSingleton<IEnvelopeStorageChangeSource<TContract>>(sp =>
                sp.GetRequiredKeyedService<IEnvelopeStorageChangeSource<TContract>>(databaseKey));
            services.TryAddSingleton<IEnvelopeStorageChangeHandler<TContract>>(sp =>
                sp.GetRequiredKeyedService<IEnvelopeStorageChangeHandler<TContract>>(databaseKey));
            services.TryAddTransient<IObjectReader<TContract>>(sp =>
                sp.GetRequiredKeyedService<IObjectReader<TContract>>(databaseKey));
            services.TryAddTransient<IObjectWriter<TContract>>(sp =>
                sp.GetRequiredKeyedService<IObjectWriter<TContract>>(databaseKey));
            services.TryAddTransient<IReaderAndWriter<TContract>>(sp =>
                sp.GetRequiredKeyedService<IReaderAndWriter<TContract>>(databaseKey));
            if (hasMigrationStrategy)
            {
                services.TryAddSingleton<IMigrationStrategy<TContract>>(sp =>
                    sp.GetRequiredKeyedService<IMigrationStrategy<TContract>>(databaseKey));
            }
            services.TryAddSingleton<IEnvelopeRepository<TContract>>(sp =>
                sp.GetRequiredKeyedService<FileEnvelopeRepositoryFacade<TContract>>(databaseKey));
            services.TryAddSingleton<IIndexManager<TContract>>(sp =>
                sp.GetRequiredKeyedService<IIndexManager<TContract>>(databaseKey));
            services.TryAddSingleton<IRepository<TContract>>(sp =>
                sp.GetRequiredKeyedService<IRepository<TContract>>(databaseKey));
            services.TryAddSingleton<IEnvelopeMetadataRepository<TContract>>(sp =>
                sp.GetRequiredKeyedService<IEnvelopeMetadataRepository<TContract>>(databaseKey));
        }

        private static bool TryAddRegistrationMarker<TContract>(
            IServiceCollection services,
            string databaseKey,
            IDbContractConfigDto<TContract> contractConfig)
            where TContract : class, IStoredContract<TContract>
        {
            var existing = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(RegistrationMarker<TContract>)
                    && descriptor.ImplementationInstance is RegistrationMarker<TContract>)
                .Select(descriptor => (RegistrationMarker<TContract>)descriptor.ImplementationInstance!)
                .FirstOrDefault(marker =>
                    string.Equals(marker.DatabaseKey, databaseKey, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                services.AddSingleton(new RegistrationMarker<TContract>(databaseKey, contractConfig));
                return true;
            }

            if (ReferenceEquals(existing.Configuration, contractConfig))
            {
                return false;
            }

            throw new InvalidOperationException(
                $"InterfaceDB database '{databaseKey}' for contract '{typeof(TContract).FullName}' " +
                "is already registered with a different configuration instance.");
        }

        private static string ResolveDatabaseKey<TContract>(
            IDbContractConfigDto<TContract> contractConfig)
            where TContract : class, IStoredContract<TContract>
        {
            var candidate = string.IsNullOrWhiteSpace(contractConfig.EnvelopeDICacheKey)
                ? contractConfig.ContractName
                : contractConfig.EnvelopeDICacheKey;
            return NormalizeDatabaseKey(candidate);
        }

        private static string NormalizeDatabaseKey(string? databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new InvalidOperationException(
                    "InterfaceDB requires EnvelopeDICacheKey or ContractName to define a database registration key.");
            }

            return databaseName.Trim();
        }

        private static IDbContractConfigDto<TContract> WithDatabaseKey<TContract>(
            IDbContractConfigDto<TContract> contractConfig,
            string databaseKey)
            where TContract : class, IStoredContract<TContract>
            => new DatabaseKeyContractConfig<TContract>(
                contractConfig,
                NormalizeDatabaseKey(databaseKey));

        private static void ValidateRepositoryType(
            ContractRepositoryType configuredType,
            ContractRepositoryType expectedType)
        {
            if (configuredType == ContractRepositoryType.None || configuredType == expectedType)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Repository config declares '{configuredType}', but this registration expects '{expectedType}'.");
        }

        private sealed record RegistrationMarker<TContract>(
            string DatabaseKey,
            IDbContractConfigDto<TContract> Configuration)
            where TContract : class, IStoredContract<TContract>;

        private sealed record InternalDatabaseServiceKey(string DatabaseKey, Type ContractType);

        private sealed class DatabaseKeyContractConfig<TContract> : IDbContractConfigDto<TContract>
            where TContract : class, IStoredContract<TContract>
        {
            private readonly IDbContractConfigDto<TContract> _inner;

            public DatabaseKeyContractConfig(
                IDbContractConfigDto<TContract> inner,
                string databaseKey)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                EnvelopeDICacheKey = databaseKey;
            }

            public string EnvelopeDICacheKey { get; }

            public string ContractName => _inner.ContractName;

            public string FileExtension => _inner.FileExtension;

            public string? ImplementationsPath => _inner.ImplementationsPath;

            public string? ContractPath => _inner.ContractPath;

            public string? MigrationsPath => _inner.MigrationsPath;

            public string? DataRoot => _inner.DataRoot;

            public string? ActiveVersion => _inner.ActiveVersion;

            public ContractRepositoryType RepositoryType => _inner.RepositoryType;

            public StorageObjectCardinality ObjectCardinality => _inner.ObjectCardinality;

            public StoragePolicy? StoragePolicy => _inner.StoragePolicy;
        }

    }
}
