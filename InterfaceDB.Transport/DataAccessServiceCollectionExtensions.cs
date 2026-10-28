using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Transport;

public static class DataAccessServiceCollectionExtensions
{
    /// <summary>Registers interface ownership criteria using the same keyed serializer as storage.</summary>
    public static IServiceCollection AddContractDataAccessPolicy<TContract>(this IServiceCollection services,
        IDbContractConfigDto<TContract> contract, string? serviceKey = null, string? serializerKey = null,
        IReadOnlyDictionary<string, string>? selectedValues = null,
        EncodedStoreCapabilities allowedCapabilities = EncodedStoreCapabilities.All,
        int maximumPayloadBytes = 4 * 1024 * 1024) where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(contract);
        string key = serviceKey ?? contract.EnvelopeDICacheKey;
        string serializationKey = serializerKey ?? key;
        ArgumentException.ThrowIfNullOrWhiteSpace(key); ArgumentException.ThrowIfNullOrWhiteSpace(serializationKey);
        var pinned = selectedValues is null ? null : new Dictionary<string, string>(selectedValues, StringComparer.Ordinal);
        services.TryAddKeyedSingleton<IAccessPolicyEnvelopeReader<TContract>>(key, (provider, _) =>
        {
            var serializers = provider.GetKeyedServices<IObjectSerializer<TContract>>(serializationKey).ToArray();
            if (serializers.Length != 1) throw new InvalidOperationException("Register exactly one serializer under the selected key.");
            return new AccessPolicyEnvelopeReader<TContract>(serializers[0], contract, maximumPayloadBytes);
        });
        if (services.Any(d => d.IsKeyedService && Equals(d.ServiceKey, key) && d.ServiceType == typeof(IDataAccessPolicy)))
            throw new InvalidOperationException("An access policy is already registered under this key.");
        services.AddKeyedSingleton<IDataAccessPolicy>(key, (provider, _) => new ContractDataAccessPolicy<TContract>(
            provider.GetRequiredKeyedService<IAccessPolicyEnvelopeReader<TContract>>(key), pinned, allowedCapabilities));
        return services.AddPartitionKeyIndex();
    }

    /// <summary>Registers a factory, not a shared mutable index. Pass it when opening each host-owned store.</summary>
    public static IServiceCollection AddPartitionKeyIndex(this IServiceCollection services,
        PartitionIndexOptions? options = null, string? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new(); options.Validate();
        if (serviceKey is null)
            services.TryAddSingleton<IPartitionKeyIndexFactory>(new BoundedPartitionKeyIndexFactory(options));
        else
            services.AddKeyedSingleton<IPartitionKeyIndexFactory>(serviceKey, new BoundedPartitionKeyIndexFactory(options));
        return services;
    }
}
