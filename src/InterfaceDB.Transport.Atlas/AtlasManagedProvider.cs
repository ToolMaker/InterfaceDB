using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.AtlasCore;
using Idb.Transport.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Commercial.Transport.Atlas;

/// <summary>Optional Atlas adapter; the management coordinator has no dependency on Atlas.</summary>
public sealed class AtlasManagedProvider<T> : IManagedLocalRepositoryProvider<T> where T : class, IStoredContract<T>
{
    public string Id => "atlas";
    public string GetVersionDirectory(ManagedProviderContext<T> context) => Path.Combine(context.Root, ".atlas-core", context.Model.Manifest.ContractVersion);
    public void Register(IServiceCollection services, ManagedProviderContext<T> context) => services.AddAtlasCoreRepository(
        context.Configure(ContractRepositoryType.AtlasCore), encryptor: context.Model.Encryptor, runtimeTypeAllowlist: context.Model.RuntimeTypes);
    public async ValueTask MigrateAsync(ManagedProviderContext<T> previous, ManagedProviderContext<T> selected,
        IServiceProvider target, CancellationToken cancellationToken)
    {
        var result = await target.GetRequiredKeyedService<IAtlasCoreRepositoryMaintenance<T>>(ManagedProviderContext<T>.ServiceKey)
            .MigrateFromAsync(GetVersionDirectory(previous), previous.Model.Manifest.ContractVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Failures.Count != 0) throw new InvalidDataException("The cache migration did not complete.");
    }
}
