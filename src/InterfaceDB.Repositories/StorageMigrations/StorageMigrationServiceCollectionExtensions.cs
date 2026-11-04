using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

public static class StorageMigrationServiceCollectionExtensions
{
    /// <summary>Register after the infrastructure-only source/destination registrations, under a distinct logical key.</summary>
    public static IServiceCollection AddStorageMigration<T>(this IServiceCollection services, string repositoryKey,
        Func<IServiceProvider, IStorageMigration<T>> factory, bool makeDefault = true) where T : class, IStoredContract<T>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryKey);
        ArgumentNullException.ThrowIfNull(factory);
        if (services.Any(d => d.IsKeyedService && Equals(d.ServiceKey, repositoryKey) && d.ServiceType == typeof(IRepository<T>)))
            throw new ArgumentException("Migration repository key must differ from the source and destination keys.");
        foreach (var descriptor in services.Where(d => d.IsKeyedService && d.ServiceType == typeof(IEnvelopeStorageIdentityFactory<T>)).ToArray())
        {
            if (services.Any(d => d.IsKeyedService && Equals(d.ServiceKey, descriptor.ServiceKey)
                && d.ServiceType == typeof(StorageMigrationWriteReservations))) continue;
            var key = descriptor.ServiceKey;
            services.AddKeyedSingleton<StorageMigrationWriteReservations>(key);
            services.TryAddKeyedSingleton<IStorageMigrationWriteReservations>(key,
                (sp, _) => sp.GetRequiredKeyedService<StorageMigrationWriteReservations>(key));
            services[services.IndexOf(descriptor)] = new ServiceDescriptor(descriptor.ServiceType, key, (sp, _) =>
            {
                var original = descriptor.KeyedImplementationInstance
                    ?? descriptor.KeyedImplementationFactory?.Invoke(sp, key)
                    ?? ActivatorUtilities.CreateInstance(sp, descriptor.KeyedImplementationType!);
                return new MigrationStorageIdentityFactory<T>((IEnvelopeStorageIdentityFactory<T>)original,
                    sp.GetRequiredKeyedService<StorageMigrationWriteReservations>(key));
            }, descriptor.Lifetime);
        }
        services.AddKeyedSingleton<IStorageMigration<T>>(repositoryKey, (sp, _) => factory(sp));
        services.AddKeyedSingleton(repositoryKey, (sp, key) => new StorageMigrationRepository<T>(sp.GetRequiredKeyedService<IStorageMigration<T>>(key)));
        services.AddKeyedSingleton<IRepository<T>>(repositoryKey, (sp, key) => sp.GetRequiredKeyedService<StorageMigrationRepository<T>>(key));
        services.AddKeyedSingleton<IEnvelopeMetadataRepository<T>>(repositoryKey, (sp, key) => sp.GetRequiredKeyedService<StorageMigrationRepository<T>>(key));
        services.AddKeyedSingleton<IRevisionEditingRepository<T>>(repositoryKey, (sp, key) => sp.GetRequiredKeyedService<StorageMigrationRepository<T>>(key));
        services.AddKeyedSingleton<IRepositoryNotifications<T>>(repositoryKey, (sp, key) => sp.GetRequiredKeyedService<StorageMigrationRepository<T>>(key));
        services.AddKeyedSingleton<IStorageMigrationProgress<T>>(repositoryKey, (sp, key) => sp.GetRequiredKeyedService<StorageMigrationRepository<T>>(key));
        foreach (var alias in services.Where(d => !d.IsKeyedService && d.ImplementationInstance is RepositoryCompatibilityAlias)
                .Select(d => (RepositoryCompatibilityAlias)d.ImplementationInstance!).Where(a => a.ContractType == typeof(T)).ToArray())
            {
                // Only redirect the exact provider-owned descriptor, never a user's replacement.
                var prior = services.Where(d => !d.IsKeyedService).Select(d => d.ImplementationInstance)
                    .OfType<AliasGate>().LastOrDefault(g => ReferenceEquals(g.Original, alias.Descriptor));
                var current = prior?.Current ?? alias.Descriptor;
                var index = services.IndexOf(current);
                if (index < 0) continue;
                var serviceType = alias.Descriptor.ServiceType;
                var replacement = new ServiceDescriptor(serviceType, sp =>
                {
                    var access = sp.GetRequiredKeyedService<IStorageMigration<T>>(repositoryKey) as IStorageMigrationServiceAccess
                        ?? throw new NotSupportedException("Migration must expose gated service capabilities.");
                    return access.OwnsServiceRegistration(alias.DatabaseKey) ? access.GetReadyService(serviceType)
                        : current.ImplementationFactory?.Invoke(sp) ?? current.ImplementationInstance
                            ?? throw new NotSupportedException("Unsupported provider alias registration.");
                }, current.Lifetime);
                services[index] = replacement;
                services.AddSingleton(new AliasGate(alias.Descriptor, replacement));
                services.TryAdd(new ServiceDescriptor(serviceType, repositoryKey, (sp, _) =>
                    ((IStorageMigrationServiceAccess)sp.GetRequiredKeyedService<IStorageMigration<T>>(repositoryKey))
                        .GetReadyService(serviceType), alias.Descriptor.Lifetime));
            }
        if (makeDefault)
        {
            services.Replace(ServiceDescriptor.Singleton<IRepository<T>>(sp => sp.GetRequiredKeyedService<IRepository<T>>(repositoryKey)));
            services.Replace(ServiceDescriptor.Singleton<IEnvelopeMetadataRepository<T>>(sp => sp.GetRequiredKeyedService<IEnvelopeMetadataRepository<T>>(repositoryKey)));
            services.Replace(ServiceDescriptor.Singleton<IRevisionEditingRepository<T>>(sp => sp.GetRequiredKeyedService<IRevisionEditingRepository<T>>(repositoryKey)));
            services.Replace(ServiceDescriptor.Singleton<IRepositoryNotifications<T>>(sp => sp.GetRequiredKeyedService<IRepositoryNotifications<T>>(repositoryKey)));
            services.Replace(ServiceDescriptor.Singleton<IStorageMigrationProgress<T>>(sp => sp.GetRequiredKeyedService<IStorageMigrationProgress<T>>(repositoryKey)));
        }
        return services;
    }

    private sealed record AliasGate(ServiceDescriptor Original, ServiceDescriptor Current);
}
