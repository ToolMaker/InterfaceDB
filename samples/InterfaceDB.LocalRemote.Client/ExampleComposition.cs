using Idb.Libraries.Repositories;
using Idb.Commercial.Repositories;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.TcpRepository.Sample;

/// <summary>One provider per account. Dispose it on logout/account change; reopen the same dedicated cache for that account.</summary>
public static class ExampleComposition
{
    /// <summary>Preferred entry point. The server selects the client provider, read mode and contract baseline.</summary>
    public static LocalRemoteRepository<IExampleProfile> CreateFromServer(
        Idb.Transport.TcpStoreClientOptions endpoint, string setupName,
        IManagedSetupVerifier setupTrust, IEnumerable<ManagedRepositoryModel<IExampleProfile>> deployedModels,
        IEnumerable<IManagedLocalRepositoryProvider<IExampleProfile>> installedProviders,
        string accountCacheDirectory, string accountCacheScope,
        IEnumerable<Idb.Libraries.Abstractions.Services.Migrations.IMigration>? migrations = null,
        Func<CancellationToken, ValueTask>? validateAccessAsync = null)
    {
        var models = deployedModels.ToArray();
        var approvalId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]
                { TcpManagedSetupAuthority.Audience(endpoint), setupName, models[0].Manifest.ContractIdentity })));
        var approvalPath = Path.Combine(accountCacheDirectory, "setup-" + approvalId + ".json");
        var authority = new TcpManagedSetupAuthority(endpoint, setupName, models[0].Manifest.ContractIdentity,
            setupTrust, new FileManagedSetupReceiptStore(approvalPath));
        return LocalRemoteRepositoryFactory.CreateFromServer(authority, models, installedProviders,
            accountCacheDirectory, accountCacheScope, new Mapper(), migrations, validateAccessAsync: validateAccessAsync);
    }

    /// <summary>Compatibility wiring for applications that still select their own SegmentCore cache policy.</summary>
    public static LocalRemoteRepository<IExampleProfile> CreateManaged(
        IEnumerable<TcpRepositoryOptions<IExampleProfile>> deployedVersions,
        string accountCacheDirectory, string accountCacheScope, RepositoryReadPreference preference,
        IEnumerable<Idb.Libraries.Abstractions.Services.Migrations.IMigration>? migrations = null,
        Func<CancellationToken, ValueTask>? validateAccessAsync = null) =>
        SegmentCoreManagedRepository.Create(deployedVersions, accountCacheDirectory, accountCacheScope,
            new Mapper(), preference, migrations, validateAccessAsync);

    public static ServiceProvider CreateProvider(TcpRepositoryOptions<IExampleProfile> remoteOptions,
        string accountCacheDirectory, string accountCacheScope, RepositoryReadPreference preference,
        Func<CancellationToken, ValueTask>? validateAccessAsync = null)
    {
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IExampleProfile>(new LocalConfig(accountCacheDirectory),
            runtimeTypeAllowlist: new(typeof(ExampleProfile)));
        services.AddTcpRepository(remoteOptions, "profile-remote");
        services.AddLocalRemoteRepository("profile", "profile-local", "profile-remote", new Mapper(),
            new ResilientRepositoryOptions<IExampleProfile>
            {
                DatasetId = "example-profile", ReadPreference = preference, CacheScope = accountCacheScope,
                ValidateAccessAsync = validateAccessAsync,
                CursorStore = new FileRepositoryCursorStore(Path.Combine(accountCacheDirectory, "applied-cursor.json")),
                IsUnavailableException = TcpRepositoryAvailability.IsUnavailable,
                IsEndpointWideFailureException = TcpRepositoryAvailability.IsUnavailable
            });
        return services.BuildServiceProvider();
    }

    private sealed class Mapper : IResilientEntityMapper<IExampleProfile>
    {
        public string GetStorageKey(IExampleProfile entity) => "ExampleProfile";
        public IExampleProfile CreateDetachedCopy(IExampleProfile source)
            => new ExampleProfile { DisplayName = source.DisplayName, Counter = source.Counter };
        public void CopyValues(IExampleProfile source, IExampleProfile target)
        { target.DisplayName = source.DisplayName; target.Counter = source.Counter; }
    }

    private sealed class LocalConfig(string root) : IDbContractConfigDto<IExampleProfile>
    {
        public string EnvelopeDICacheKey => "profile-local";
        public string ContractName => "ExampleProfile";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public StoragePolicy? StoragePolicy => new() { RepositoryNotifications = new() };
    }
}
