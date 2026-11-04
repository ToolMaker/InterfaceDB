using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Transport.Repositories;

/// <summary>Trusted contract/serializer code shipped with the application, not configuration downloaded from a server.</summary>
public sealed record ManagedRepositoryModel<T>(CompatibilityManifest Manifest, IDbContractConfigDto<T> Contract,
    StoredRuntimeTypeAllowlist<T> RuntimeTypes, IEncryptor Encryptor, IObjectSerializer<T> Serializer,
    StoragePolicy StoragePolicy) where T : class, IStoredContract<T>
{
    public static ManagedRepositoryModel<T> FromTcp(TcpRepositoryOptions<T> options)
    {
        options.Validate();
        return new(options.Connection.Manifest, options.Contract, options.RuntimeTypes, options.Encryptor,
            options.ResolveSerializer(), options.StoragePolicy);
    }
}

public sealed record ManagedProviderContext<T>(ManagedRepositoryModel<T> Model, string Root) where T : class, IStoredContract<T>
{
    public const string ServiceKey = "managed-cache";
    public IDbContractConfigDto<T> Configure(ContractRepositoryType type, string? dataRoot = null)
        => new CacheConfig(Model, dataRoot ?? Root, type);
    private sealed class CacheConfig(ManagedRepositoryModel<T> model, string root, ContractRepositoryType type) : IDbContractConfigDto<T>
    {
        public string EnvelopeDICacheKey => ServiceKey;
        public string ContractName => model.Contract.ContractName;
        public string FileExtension => model.Contract.FileExtension;
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => model.Manifest.ContractVersion;
        public ContractRepositoryType RepositoryType => type;
        public StorageObjectCardinality ObjectCardinality => model.Contract.ObjectCardinality;
        public StoragePolicy? StoragePolicy => model.StoragePolicy with
        {
            DurabilityMode = StorageDurabilityMode.Durable,
            RepositoryNotifications = model.Contract.StoragePolicy?.RepositoryNotifications ?? new()
        };
    }
}

/// <summary>Provider-specific registration and migration. The coordinator never casts to an engine implementation.</summary>
public interface IManagedLocalRepositoryProvider<T> where T : class, IStoredContract<T>
{
    string Id { get; }
    string GetVersionDirectory(ManagedProviderContext<T> context);
    void Register(IServiceCollection services, ManagedProviderContext<T> context);
    ValueTask MigrateAsync(ManagedProviderContext<T> previous, ManagedProviderContext<T> selected,
        IServiceProvider target, CancellationToken cancellationToken);
}

public interface IManagedRemoteRepositoryProvider<T> where T : class, IStoredContract<T>
{
    ValueTask<RepositoryLease<T>> OpenAsync(ManagedRepositoryModel<T> model, CancellationToken cancellationToken);
}

public interface IManagedRepositorySettings
{
    string LocalProviderId { get; }
    RepositoryReadPreference ReadPreference { get; }
}
public sealed record ManagedRepositorySettings(string LocalProviderId, RepositoryReadPreference ReadPreference) : IManagedRepositorySettings;

/// <summary>TCP adapter. Other transports implement IManagedRemoteRepositoryProvider without changing the coordinator.</summary>
public sealed class TcpManagedRemoteRepositoryProvider<T>(Func<ManagedRepositoryModel<T>, TcpRepositoryOptions<T>> options)
    : IManagedRemoteRepositoryProvider<T> where T : class, IStoredContract<T>
{
    public ValueTask<RepositoryLease<T>> OpenAsync(ManagedRepositoryModel<T> model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new TcpRepository<T>(options(model) with { Serializer = model.Serializer });
        return ValueTask.FromResult(new RepositoryLease<T>(repository, repository.DisposeAsync));
    }
}

public sealed class SegmentCoreManagedProvider<T> : IManagedLocalRepositoryProvider<T> where T : class, IStoredContract<T>
{
    public string Id => "segment-core";
    public string GetVersionDirectory(ManagedProviderContext<T> context) => Path.Combine(context.Root, ".segment-core", context.Model.Manifest.ContractVersion);
    public void Register(IServiceCollection services, ManagedProviderContext<T> context) => services.AddSegmentCoreRepository(
        context.Configure(ContractRepositoryType.SegmentCore), encryptor: context.Model.Encryptor, runtimeTypeAllowlist: context.Model.RuntimeTypes);
    public async ValueTask MigrateAsync(ManagedProviderContext<T> previous, ManagedProviderContext<T> selected,
        IServiceProvider target, CancellationToken cancellationToken)
    {
        var result = await target.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<T>>(ManagedProviderContext<T>.ServiceKey)
            .MigrateFromAsync(GetVersionDirectory(previous), previous.Model.Manifest.ContractVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Failures.Count != 0) throw new InvalidDataException("The cache migration did not complete.");
    }
}

public sealed class FileManagedProvider<T> : IManagedLocalRepositoryProvider<T> where T : class, IStoredContract<T>
{
    public string Id => "file";
    // A separate migration root for every generation prevents eager file migration from deleting the active source.
    public string GetVersionDirectory(ManagedProviderContext<T> context) => Path.Combine(context.Root, ".managed-file", context.Model.Manifest.ContractVersion);
    public void Register(IServiceCollection services, ManagedProviderContext<T> context)
    {
        services.AddKeyedSingleton<IEncryptor>(ManagedProviderContext<T>.ServiceKey, context.Model.Encryptor);
        services.AddFileRepository(context.Configure(ContractRepositoryType.FileSystem, GetVersionDirectory(context)),
            MigrationStrategyType.EagerStrict, durabilityMode: StorageDurabilityMode.Durable, runtimeTypeAllowlist: context.Model.RuntimeTypes);
    }
    public async ValueTask MigrateAsync(ManagedProviderContext<T> previous, ManagedProviderContext<T> selected,
        IServiceProvider target, CancellationToken cancellationToken)
    {
        var source = Path.Combine(GetVersionDirectory(previous), previous.Model.Manifest.ContractVersion);
        var seed = Path.Combine(GetVersionDirectory(selected), previous.Model.Manifest.ContractVersion);
        var extension = previous.Model.Contract.FileExtension.NormalizeExtension();
        if (!string.Equals(extension, selected.Model.Contract.FileExtension.NormalizeExtension(), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Changing a File cache extension requires an explicit conversion.");
        if (!Directory.Exists(source)) throw new InvalidDataException("The active File cache generation is missing.");
        {
            Directory.CreateDirectory(seed);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(seed, Path.GetFileName(file)), overwrite: false);
            }
        }
        await target.GetRequiredKeyedService<IRepository<T>>(ManagedProviderContext<T>.ServiceKey).InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
