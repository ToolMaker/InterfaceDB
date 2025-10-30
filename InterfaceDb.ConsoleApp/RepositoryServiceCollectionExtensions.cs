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
        IDbContractConfigDto<TContract, string> contractConfig,
        MigrationStrategyType strategyType = MigrationStrategyType.None)
        where TContract : class, IRepoDto<TContract, string>
    {
        services.AddSingleton(contractConfig);
        services.AddSingleton<IDbContractConfigDto<TContract, string>>(contractConfig);
        services.AddSingleton<IEnvelopeKeyAndLocationFactory<TContract, string>, DefaultFileNameFactory<TContract>>();

        var serializerOptions = MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.TypelessContractlessStandardResolver.Instance);
        services.AddSingleton<IObjectSerializer<TContract, string>>(new BinaryObjectSerializer<TContract, string>(serializerOptions));
        services.AddSingleton<IEnvelopeRepository<TContract, string>, FileEnvelopeRepository<TContract>>();
        services.AddSingleton<IIndexManager<TContract>, PassthroughIndexManager<TContract, string>>();
        services.AddTransient<IObjectEnvelope<TContract, string>, FileObjectEnvelope<TContract>>();
        services.AddSingleton<IEnvelopeLifecycle<TContract, string>, EnvelopeLifecycle<TContract, string>>();
        services.AddSingleton<IEnvelopeDictionaryCaheManager<string,IObjectEnvelope<TContract, string>>, EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract, string>>>();
        services.AddTransient<IObjectReader<TContract, string>, FileObjectReader<TContract>>();
        services.AddTransient<IObjectWriter<TContract, string>, FileObjectWriter<TContract>>();
        services.AddTransient<IReaderAndWriter<TContract, string>, ObjectReaderAndWriter<TContract, string>>();

        if (strategyType != MigrationStrategyType.None)
        {
            services.AddSingleton<IMigrationStrategy<TContract, string>>(sp =>
            {
                var serializer = sp.GetRequiredService<IObjectSerializer<TContract, string>>();
                var factory = sp.GetRequiredService<IEnvelopeKeyAndLocationFactory<TContract, string>>();
                var reader = sp.GetRequiredService<IObjectReader<TContract, string>>();
                var writer = sp.GetRequiredService<IObjectWriter<TContract, string>>();
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
            return new InterfaceRepositryDb<TContract, string>(manager);
        });

        return services;
    }
}
