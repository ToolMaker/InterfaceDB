using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Idb.Commercial.Repositories;

public static class ResilientServiceCollectionExtensions
{
    /// <summary>
    /// Composes two existing keyed repositories into one resilient repository. Both endpoints
    /// must implement <see cref="IEnvelopeMetadataRepository{TContract}"/>.
    /// </summary>
    /// <param name="services">The dependency-injection service collection.</param>
    /// <param name="resilientRepositoryKey">The distinct key assigned to the composed repository.</param>
    /// <param name="preferredRepositoryKey">The key of the preferred physical repository.</param>
    /// <param name="peerRepositoryKey">The key of the peer physical repository.</param>
    /// <param name="entityMapper">The logical-identity and deep-copy mapper.</param>
    /// <param name="policy">Optional repository or key-specific behavior policy.</param>
    /// <param name="makeDefault">
    /// Whether the composition also replaces the unkeyed repository registrations for this contract.
    /// </param>
    /// <param name="synchronizationStateStore">
    /// Optional invalidation/checkpoint store. The default is process-local and non-durable.
    /// </param>
    /// <typeparam name="TContract">The InterfaceDB stored contract exposed by the composition.</typeparam>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddResilientRepository<TContract>(
        this IServiceCollection services,
        string resilientRepositoryKey,
        string preferredRepositoryKey,
        string peerRepositoryKey,
        IResilientEntityMapper<TContract> entityMapper,
        IResilientRepositoryPolicy<TContract>? policy = null,
        bool makeDefault = true,
        IResilientSynchronizationStateStore? synchronizationStateStore = null)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(entityMapper);

        resilientRepositoryKey = NormalizeKey(resilientRepositoryKey, nameof(resilientRepositoryKey));
        preferredRepositoryKey = NormalizeKey(preferredRepositoryKey, nameof(preferredRepositoryKey));
        peerRepositoryKey = NormalizeKey(peerRepositoryKey, nameof(peerRepositoryKey));

        if (string.Equals(preferredRepositoryKey, peerRepositoryKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Preferred and peer repository keys must be different.");
        }

        if (string.Equals(resilientRepositoryKey, preferredRepositoryKey, StringComparison.Ordinal)
            || string.Equals(resilientRepositoryKey, peerRepositoryKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The resilient repository key must differ from both endpoint keys.");
        }

        var effectivePolicy = policy switch
        {
            null => new ResilientRepositoryOptions<TContract>().Snapshot(),
            ResilientRepositoryOptions<TContract> options => options.Snapshot(),
            _ => policy
        };
        var effectiveStateStore = synchronizationStateStore
            ?? new InMemoryResilientSynchronizationStateStore(
                effectivePolicy.MaximumPendingSynchronizationKeys);

        services.AddKeyedSingleton<ResilientRepository<TContract>>(
            resilientRepositoryKey,
            (serviceProvider, _) => new ResilientRepository<TContract>(
                serviceProvider.GetRequiredKeyedService<IRepository<TContract>>(preferredRepositoryKey),
                serviceProvider.GetRequiredKeyedService<IRepository<TContract>>(peerRepositoryKey),
                entityMapper,
                effectivePolicy,
                effectiveStateStore));
        services.AddKeyedSingleton<IResilientRepository<TContract>>(
            resilientRepositoryKey,
            (serviceProvider, _) => serviceProvider
                .GetRequiredKeyedService<ResilientRepository<TContract>>(resilientRepositoryKey));
        services.AddKeyedSingleton<IRepository<TContract>>(
            resilientRepositoryKey,
            (serviceProvider, _) => serviceProvider
                .GetRequiredKeyedService<ResilientRepository<TContract>>(resilientRepositoryKey));

        if (makeDefault)
        {
            services.Replace(ServiceDescriptor.Singleton<IRepository<TContract>>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<IRepository<TContract>>(resilientRepositoryKey)));
            services.Replace(ServiceDescriptor.Singleton<IResilientRepository<TContract>>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<IResilientRepository<TContract>>(resilientRepositoryKey)));
        }

        return services;
    }

    private static string NormalizeKey(string key, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A repository key is required.", parameterName);
        }

        return key.Trim();
    }
}
