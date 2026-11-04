using Idb.Libraries.Abstractions.Dtos;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Commercial.Repositories;

public static class LocalRemoteServiceCollectionExtensions
{
    /// <summary>
    /// Registers a composed IRepository using a dedicated durable local cache and a remote endpoint.
    /// Options are copied at registration so each repository keeps its own policy.
    /// </summary>
    public static IServiceCollection AddLocalRemoteRepository<TContract>(this IServiceCollection services,
        string repositoryKey, string localRepositoryKey, string remoteRepositoryKey,
        IResilientEntityMapper<TContract> mapper, ResilientRepositoryOptions<TContract> options, bool makeDefault = true)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReadPreference is null) throw new ArgumentException("Choose LocalFirst or RemoteFirst.", nameof(options));
        return services.AddResilientRepository(repositoryKey, localRepositoryKey, remoteRepositoryKey,
            mapper, options, makeDefault);
    }
}
