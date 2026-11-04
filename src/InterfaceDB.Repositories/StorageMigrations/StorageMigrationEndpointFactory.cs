using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Abstractions.Services.Encryption;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Composition helpers. The coordinator itself knows no concrete provider types.</summary>
public static class StorageMigrationEndpointFactory
{
    public static IStorageMigrationSource<T> FileSource<T>(IServiceProvider services, string databaseKey,
        IFilePublication publication) where T : class, IStoredContract<T>
    {
        var config = services.GetRequiredKeyedService<IDbContractConfigDto<T>>(databaseKey);
        var root = Path.GetFullPath(config.DataRoot ?? throw new ArgumentException("Source root is required."));
        var policy = services.GetRequiredKeyedService<StoragePolicy>(databaseKey);
        if (config.RepositoryType is not (ContractRepositoryType.None or ContractRepositoryType.FileSystem))
            throw new ArgumentException("FileSource requires a File repository registration.");
        return new DeferredSource<T>($"{config.ContractName}:{databaseKey}", config.ActiveVersion!, root, databaseKey,
            () => services.GetRequiredKeyedService<IRepository<T>>(databaseKey),
            type => Resolve<T>(services, databaseKey, type), () =>
        {
            // Validate BEFORE constructing any provider that could create the missing source.
            StorageMigrationPaths.Validate(root);
            var guard = new FileStorageMigrationSourceGuard<T>(config, policy, new SidecarFileWriteLock<T>(policy), publication,
                leaseValidation: services.GetService<IFileLeaseValidation>());
            return new RepositoryStorageMigrationSource<T>(Create<T>(services, databaseKey, root),
                guard,
                new FileStorageMigrationRevisionClock(root, publication));
        }, current =>
        {
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Retired source is unavailable; cleanup will not recreate it.");
            StorageMigrationPaths.Validate(root);
            var envelopes = services.GetRequiredKeyedService<IEnvelopeRepository<T>>(databaseKey);
            return new FileStorageMigrationSourceCleanup<T>(root, current,
                envelopes as ICommittedEnvelopeInventory<T> ?? throw new NotSupportedException("File cleanup requires committed inventory."),
                envelopes as ICommittedEnvelopeSnapshotSource<T> ?? throw new NotSupportedException("File cleanup requires committed snapshots."), publication);
        });
    }

    /// <summary>physicalStoreDirectory is the actual provider directory containing its revision clock.</summary>
    public static IStorageMigrationDestination<T> LocalDestination<T>(IServiceProvider services, string databaseKey,
        string physicalStoreDirectory, IFilePublication publication, Func<CancellationToken, ValueTask> verify)
        where T : class, IStoredContract<T>
        => new RepositoryStorageMigrationDestination<T>(Create<T>(services, databaseKey, Path.GetFullPath(physicalStoreDirectory)),
            new FileStorageMigrationRevisionClock(physicalStoreDirectory, publication), verify,
            new FileStorageMigrationDestinationBinding(physicalStoreDirectory,
                services.GetRequiredKeyedService<IEncryptor>(databaseKey), publication));

    /// <summary>The caller owns this provider and must keep its exclusive storage ownership until
    /// the history handle is disposed. The normal provider initialization obtains that ownership.</summary>
    public static IStorageMigrationSource<T> LocalSource<T>(IServiceProvider services, string databaseKey,
        string physicalStoreDirectory, IFilePublication publication) where T : class, IStoredContract<T>
    {
        var endpoint = Create<T>(services, databaseKey, Path.GetFullPath(physicalStoreDirectory));
        return new RepositoryStorageMigrationSource<T>(endpoint, new LocalOwnershipGuard<T>(endpoint),
            new FileStorageMigrationRevisionClock(physicalStoreDirectory, publication));
    }

    private static object? Resolve<T>(IServiceProvider services, string key, Type type) where T : class, IStoredContract<T>
    {
        var value = services.GetKeyedService(type, key);
        if (value is not null) return value;
        if (type == typeof(IObjectReader<T>)) return services.GetRequiredKeyedService<IReaderAndWriter<T>>(key).Reader;
        if (type == typeof(IObjectWriter<T>)) return services.GetRequiredKeyedService<IReaderAndWriter<T>>(key).Writer;
        var alias = services.GetServices<RepositoryCompatibilityAlias>()
            .FirstOrDefault(a => a.ContractType == typeof(T) && a.DatabaseKey == key && a.Descriptor.ServiceType == type);
        return alias?.Descriptor.ImplementationFactory?.Invoke(services) ?? alias?.Descriptor.ImplementationInstance;
    }

    private static IStorageMigrationEndpoint<T> Create<T>(IServiceProvider services, string key, string directory)
        where T : class, IStoredContract<T>
    {
        var config = services.GetRequiredKeyedService<IDbContractConfigDto<T>>(key);
        var envelopes = services.GetRequiredKeyedService<IEnvelopeRepository<T>>(key);
        var repository = services.GetRequiredKeyedService<IRepository<T>>(key);
        // Links can make apparently distinct paths refer to the same store. Require physical, disjoint roots.
        StorageMigrationPaths.Validate(directory);
        return new RepositoryStorageMigrationEndpoint<T>($"{config.ContractName}:{key}",
            config.ActiveVersion ?? throw new ArgumentException("Contract version is required."), directory,
            repository, envelopes, services.GetRequiredKeyedService<IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<T>>>(key),
            envelopes as ICommittedEnvelopeSnapshotSource<T> ?? throw new NotSupportedException("Provider must support committed snapshots."),
            key, envelopes as ICommittedEnvelopeInventory<T>, type => Resolve<T>(services, key, type));
    }

    // Completed startup need not construct or touch the retired File source.
    private sealed class DeferredSource<T>(string identity, string version, string directory, string serviceRegistrationKey,
        Func<IRepository<T>> repository, Func<Type, object?> resolve,
        Func<IStorageMigrationSource<T>> create,
        Func<IStorageMigrationSource<T>, IStorageMigrationSourceCleanup<T>> cleanupFactory) : IStorageMigrationSource<T> where T : class, IStoredContract<T>
    {
        private IStorageMigrationSource<T>? _inner;
        private IStorageMigrationSource<T> Inner => _inner ??= create();
        public string Identity => identity;
        public string ContractVersion => version;
        public string? StorageDirectory => directory;
        public string? ServiceRegistrationKey => serviceRegistrationKey;
        public IRepository<T> Repository => repository();
        public object? GetService(Type type) => resolve(type)
            ?? (type == typeof(IStorageMigrationSourceCleanup<T>) ? cleanupFactory(this) : null);
        public ValueTask<IAsyncDisposable> AcquireSourceLeaseAsync(CancellationToken ct) => Inner.AcquireSourceLeaseAsync(ct);
        public ValueTask InitializeSnapshotAsync(CancellationToken ct) => Inner.InitializeSnapshotAsync(ct);
        public IAsyncEnumerable<StorageMigrationRecord<T>> ReadAllAsync(int maximumBytes, CancellationToken ct) => Inner.ReadAllAsync(maximumBytes, ct);
        public ValueTask<StorageMigrationRecord<T>?> ReadAsync(string key, int maximumBytes, CancellationToken ct) => Inner.ReadAsync(key, maximumBytes, ct);
        public ValueTask<long> ReadRevisionFloorAsync(CancellationToken ct) => Inner.ReadRevisionFloorAsync(ct);
    }

    private sealed class LocalOwnershipGuard<T>(IStorageMigrationEndpoint<T> endpoint) : IStorageMigrationSourceGuard where T : class, IStoredContract<T>
    {
        public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken ct)
        { await endpoint.InitializeSnapshotAsync(ct).ConfigureAwait(false); return new NoopLease(); }
    }
    private sealed class NoopLease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
