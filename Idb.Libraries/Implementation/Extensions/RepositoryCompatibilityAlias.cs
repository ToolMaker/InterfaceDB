using Microsoft.Extensions.DependencyInjection;

namespace Idb.Libraries.Implementation.Extensions;

/// <summary>Ownership metadata for compatibility aliases. Composers may redirect aliases
/// contributed by a provider without removing application services or another contract's aliases.</summary>
public sealed record RepositoryCompatibilityAlias(Type ContractType, string DatabaseKey, ServiceDescriptor Descriptor)
{
    public static void Record(IServiceCollection services, Type contractType, string databaseKey,
        HashSet<ServiceDescriptor> before)
    {
        foreach (var descriptor in services.Where(d => !d.IsKeyedService && !before.Contains(d)).ToArray())
            services.AddSingleton(new RepositoryCompatibilityAlias(contractType, databaseKey, descriptor));
    }
}
