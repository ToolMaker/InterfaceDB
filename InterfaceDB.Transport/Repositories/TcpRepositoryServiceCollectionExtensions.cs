using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idb.Transport.Repositories;

public static class TcpRepositoryServiceCollectionExtensions
{
    /// <summary>Register one repository per signed-in account/service scope; dispose and replace it on account changes.</summary>
    public static IServiceCollection AddTcpRepository<TContract>(this IServiceCollection services,
        TcpRepositoryOptions<TContract> options, string? databaseKey = null)
        where TContract : class, IStoredContract<TContract>
        => AddTcpRepository(services, options, databaseKey, serializerKey: null);

    /// <summary>Reuse exactly one serializer registered under serializerKey; null uses the repository's own key.</summary>
    public static IServiceCollection AddTcpRepository<TContract>(this IServiceCollection services,
        TcpRepositoryOptions<TContract> options, string? databaseKey, string? serializerKey)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var key = databaseKey ?? options.Contract.EnvelopeDICacheKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (services.Any(descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, key)
            && descriptor.ServiceType == typeof(IRepository<TContract>)))
            throw new InvalidOperationException("A repository is already registered under this database key.");
        var selectedKey = serializerKey ?? key;
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedKey);
        var registered = services.Any(descriptor => descriptor.IsKeyedService && Equals(descriptor.ServiceKey, selectedKey)
            && descriptor.ServiceType == typeof(IObjectSerializer<TContract>));
        if (options.Serializer is not null && (registered || serializerKey is not null))
            throw new ArgumentException("Select the serializer through options or a DI registration, not both.", nameof(options));
        if (serializerKey is null && !registered)
            services.TryAddKeyedSingleton<IObjectSerializer<TContract>>(selectedKey, (_, _) => options.ResolveSerializer());
        services.AddKeyedSingleton<TcpRepository<TContract>>(key, (provider, _) =>
        {
            var serializers = provider.GetKeyedServices<IObjectSerializer<TContract>>(selectedKey).ToArray();
            if (serializers.Length != 1)
                throw new InvalidOperationException("Exactly one serializer must be registered under the selected serializer key.");
            return new(options with { Serializer = serializers[0] });
        });
        services.AddKeyedSingleton<IRepository<TContract>>(key, (provider, serviceKey) =>
            provider.GetRequiredKeyedService<TcpRepository<TContract>>(serviceKey));
        services.AddKeyedSingleton<IEnvelopeMetadataRepository<TContract>>(key, (provider, serviceKey) =>
            provider.GetRequiredKeyedService<TcpRepository<TContract>>(serviceKey));
        services.TryAddSingleton<IRepository<TContract>>(provider =>
            provider.GetRequiredKeyedService<IRepository<TContract>>(key));
        return services;
    }
}
