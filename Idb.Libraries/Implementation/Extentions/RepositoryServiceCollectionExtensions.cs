namespace Idb.Libraries.Implementation.Extentions
{
    using Idb.Libraries.Abstraction.Services;
    using Idb.Libraries.Abstraction.Services.Cache;
    using Idb.Libraries.Abstraction.Services.Migrations;
    using Idb.Libraries.Abstraction.Services.Serialization;
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Enums;
    using Idb.Libraries.Abstractions.Services;
    using Idb.Libraries.Abstractions.Services.Indexes;
    using Idb.Libraries.Abstractions.Services.Migrations;
    using Idb.Libraries.Abstractions.Services.Repository;
    using Idb.Libraries.Abstractions.Services.Serialization;
    using Idb.Libraries.Implementation.Cache;
    using Idb.Libraries.Implementation.Indexes;
    using Idb.Libraries.Implementation.Migrations;
    using Idb.Libraries.Implementation.Repositories;
    using Idb.Libraries.Implementation.Serialization;
    using MessagePack;
    using Microsoft.Extensions.DependencyInjection;
    // using IMigrationContract = Idb.Libraries.Abstraction.Services.Migrations.IMigration;

    public static class RepositoryServiceCollectionExtensions
    {
        public static IServiceCollection AddFileRepository<TContract>(
            this IServiceCollection services,
            IDbContractConfigDto<TContract> contractConfig,
            MigrationStrategyType strategyType = MigrationStrategyType.None)
            where TContract : class, IRepoDto<TContract>
        {
            services.AddSingleton(contractConfig);
            services.AddSingleton(contractConfig);
            services.AddSingleton<IEnvelopeKeyAndLocationFactory<TContract>, DefaultFileNameFactory<TContract>>();

            var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
            services.AddSingleton<IObjectSerializer<TContract>>(new BinaryObjectSerializer<TContract>(serializerOptions));
            services.AddSingleton<IEnvelopeRepository<TContract>, FileEnvelopeRepository<TContract>>();
            services.AddSingleton<IIndexManager<TContract>, PassthroughIndexManager<TContract>>();
            services.AddTransient<IObjectEnvelope<TContract>, FileObjectEnvelope<TContract>>();
            services.AddSingleton<IEnvelopeLifecycle<TContract>, EnvelopeLifecycle<TContract>>();
            services.AddSingleton<IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>>, EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>>>();
            services.AddTransient<IObjectReader<TContract>, FileObjectReader<TContract>>();
            services.AddTransient<IObjectWriter<TContract>, FileObjectWriter<TContract>>();
            services.AddTransient<IReaderAndWriter<TContract>, ObjectReaderAndWriter<TContract>>();

            if (strategyType != MigrationStrategyType.None)
            {
                services.AddSingleton<IMigrationStrategy<TContract>>(sp =>
                {
                    var serializer = sp.GetRequiredService<IObjectSerializer<TContract>>();
                    var factory = sp.GetRequiredService<IEnvelopeKeyAndLocationFactory<TContract>>();
                    var reader = sp.GetRequiredService<IObjectReader<TContract>>();
                    var writer = sp.GetRequiredService<IObjectWriter<TContract>>();
                    var migrations = sp.GetServices<IMigration>().Where(m => typeof(TContract).IsAssignableFrom(m.GetType())).ToList();
                    return strategyType switch
                    {
                        MigrationStrategyType.Lazy => new FileSystemLazyMigrationStrategy<TContract>(contractConfig, serializer, factory, migrations, reader, writer),
                        MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant => new FileSystemEagerMigrationStrategy<TContract>(contractConfig, serializer, migrations, strategyType, factory, reader, writer),
                        _ => throw new InvalidOperationException($"Unsupported migration strategy '{strategyType}'.")
                    };
                });
            }

            services.AddSingleton<IRepository<TContract>>(sp =>
            {
                var manager = sp.GetRequiredService<IIndexManager<TContract>>();
                return new InterfaceRepositryDb<TContract>(manager);
            });

            return services;
        }
    }

}