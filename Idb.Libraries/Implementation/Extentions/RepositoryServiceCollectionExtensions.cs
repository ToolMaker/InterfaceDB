namespace Idb.Libraries.Implementation.Extentions
{
    using Idb.Libraries.Abstraction.Services;
    using Idb.Libraries.Abstraction.Services.Cache;
    using Idb.Libraries.Abstraction.Services.Migrations;
    using Idb.Libraries.Abstraction.Services.Serialization;
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Enums;
    using Idb.Libraries.Abstractions.Services;
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

        public static IServiceCollection AddRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType = MigrationStrategyType.None)
            where TContract : class, IStoredContract<TContract>
        {
            return contractConfig.RepositoryType switch
            {
                ContractRepositoryType.None or ContractRepositoryType.FileSystem => services.AddFileRepository(contractConfig, strategyType),
                _ => throw new InvalidOperationException($"Unsupported repository type '{contractConfig.RepositoryType}'.")
            };
        }

        public static IServiceCollection AddFileRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType = MigrationStrategyType.None)
            where TContract : class, IStoredContract<TContract>
        {
            ValidateRepositoryType(contractConfig.RepositoryType, ContractRepositoryType.FileSystem);
            return AddRepositoryCore<TContract>(
                services,
                contractConfig,
                strategyType);
        }

        private static IServiceCollection AddRepositoryCore<TContract>(
            IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType)
            where TContract : class, IStoredContract<TContract>
        {
            services.AddSingleton(contractConfig);
            services.AddSingleton<IDbContractConfigDto<TContract>>(contractConfig);
            services.TryAddSingleton<IEnvelopeKeyAndLocationFactory<TContract>, FlatFileNameFactory<TContract>>();

            services.TryAddSingleton<IEncryptor, EncryptionNone>();
            var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
            services.AddSingleton(new BinaryObjectSerializer<TContract>(serializerOptions));
            services.AddSingleton<IObjectSerializer<TContract>>(sp =>
                new EncryptingObjectSerializer<TContract>(
                    sp.GetRequiredService<BinaryObjectSerializer<TContract>>(),
                    sp.GetRequiredService<IEncryptor>()));
            services.AddSingleton<IEnvelopeRepository<TContract>, FileEnvelopeRepository<TContract>>();
            services.AddSingleton<IIndexManager<TContract>, PassthroughIndexManager<TContract>>();
            services.AddTransient<IObjectEnvelope<TContract>, FileObjectEnvelope<TContract>>();
            services.AddSingleton<IEnvelopeLifecycle<TContract>, EnvelopeLifecycle<TContract>>();
            services.AddSingleton<IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>>, EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>>>();
            services.AddSingleton<IFileWriteLock, SidecarFileWriteLock<TContract>>();
            services.AddSingleton<IFileWriteCoordinator, CoalescingFileWriteCoordinator<TContract>>();
            services.AddSingleton<IEnvelopeStorageChangeSource<TContract>, FileEnvelopeStorageChangeSource<TContract>>();
            services.AddSingleton<IEnvelopeStorageChangeHandler<TContract>, FileEnvelopeStorageChangeHandler<TContract>>();
            services.AddTransient<IObjectReader<TContract>, FileObjectReader<TContract>>();
            services.AddTransient<IObjectWriter<TContract>, FileObjectWriter<TContract>>();
            services.AddTransient<IReaderAndWriter<TContract>, ObjectReaderAndWriter<TContract>>();

            if (strategyType != MigrationStrategyType.None)
            {
                services.AddSingleton<IMigrationStrategy<TContract>>(sp =>
                {
                    var serializer = sp.GetRequiredService<IObjectSerializer<TContract>>();
                    var encryptor = sp.GetRequiredService<IEncryptor>();
                    var factory = sp.GetRequiredService<IEnvelopeKeyAndLocationFactory<TContract>>();
                    var fileWriteCoordinator = sp.GetRequiredService<IFileWriteCoordinator>();
                    var reader = sp.GetRequiredService<IObjectReader<TContract>>();
                    var writer = sp.GetRequiredService<IObjectWriter<TContract>>();
                    var migrations = sp.GetServices<IMigration>().Where(m => typeof(TContract).IsAssignableFrom(m.GetType())).ToList();
                    return strategyType switch
                    {
                        MigrationStrategyType.Lazy => new FileSystemLazyMigrationStrategy<TContract>(contractConfig, serializer, factory, migrations, encryptor, fileWriteCoordinator, reader, writer),
                        MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant => new FileSystemEagerMigrationStrategy<TContract>(contractConfig, serializer, migrations, strategyType, factory, encryptor, fileWriteCoordinator, reader, writer),
                        _ => throw new InvalidOperationException($"Unsupported migration strategy '{strategyType}'.")
                    };
                });
            }

            services.AddSingleton<IRepository<TContract>>(sp =>
            {
                var manager = sp.GetRequiredService<IIndexManager<TContract>>();
                var config = sp.GetRequiredService<IDbContractConfigDto<TContract>>();
                return new InterfaceRepositryDb<TContract>(manager, config);
            });

            return services;
        }

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
    }
}
