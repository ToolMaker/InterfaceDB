using Idb.Diagnostics;
using Idb.Commercial.Repositories;
using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Transport.Repositories;

/// <summary>Composes registered repository providers using settings selected by the version authority.</summary>
public static class LocalRemoteRepositoryFactory
{
    /// <summary>Server-selected provider, read mode and baseline. Models and providers are trusted DI services shipped with the client.</summary>
    public static LocalRemoteRepository<T> CreateFromServer<T>(TcpManagedSetupAuthority authority,
        IEnumerable<ManagedRepositoryModel<T>> deployedVersions, IEnumerable<IManagedLocalRepositoryProvider<T>> localProviders,
        string accountDirectory, string accountScope, IResilientEntityMapper<T> mapper,
        IEnumerable<IMigration>? migrations = null, TimeSpan? retryDelay = null,
        Func<CancellationToken, ValueTask>? validateAccessAsync = null, IDatabaseTelemetry? telemetry = null) where T : class, IStoredContract<T>
    {
        var models = deployedVersions.Select(model => model with { Manifest = ManagedSetupProtocol.ClientManifest(model.Manifest) }).ToArray();
        var root = Path.Combine(Path.GetFullPath(accountDirectory), ManagedSetupProtocol.Hash(authority.Scope + "|" + accountScope));
        return Create(models, localProviders, new TcpManagedRemoteRepositoryProvider<T>(model => new()
        {
            Connection = authority.DataConnection(model.Manifest), Contract = model.Contract, RuntimeTypes = model.RuntimeTypes,
            Encryptor = model.Encryptor, Serializer = model.Serializer, SerializerEnvelopeFormat = model.Manifest.EnvelopeFormat,
            StoragePolicy = model.StoragePolicy
        }), authority, authority, root, accountScope, mapper, migrations, validateAccessAsync, retryDelay, telemetry: telemetry);
    }

    public static LocalRemoteRepository<T> Create<T>(IEnumerable<ManagedRepositoryModel<T>> deployedVersions,
        IEnumerable<IManagedLocalRepositoryProvider<T>> localProviders, IManagedRemoteRepositoryProvider<T> remoteProvider,
        IRepositoryVersionAuthority authority, IManagedRepositorySettings settings,
        string accountDirectory, string accountScope, IResilientEntityMapper<T> mapper,
        IEnumerable<IMigration>? migrations = null, Func<CancellationToken, ValueTask>? validateAccessAsync = null,
        TimeSpan? retryDelay = null, Func<Exception, bool>? isUnavailable = null, IDatabaseTelemetry? telemetry = null)
        where T : class, IStoredContract<T>
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(settings);
        var models = deployedVersions.ToArray();
        if (models.Length == 0) throw new ArgumentException("Supply the deployed contract versions.", nameof(deployedVersions));
        foreach (var model in models)
        {
            model.Manifest.Validate();
            // These values become local directory names only after comparison with deployed code.
            if (!Version.TryParse(model.Manifest.ContractVersion, out _) || model.Manifest.ContractVersion != model.Contract.ActiveVersion)
                throw new ArgumentException("The model must declare the same valid version as its contract.");
        }
        var providers = localProviders.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        var root = Path.GetFullPath(accountDirectory);
        var steps = migrations?.ToArray() ?? [];
        var retry = retryDelay ?? TimeSpan.FromSeconds(5);
        var unavailable = isUnavailable ?? TcpRepositoryAvailability.IsUnavailable;
        IManagedLocalRepositoryProvider<T> SelectedProvider() => providers.TryGetValue(settings.LocalProviderId, out var provider)
            ? provider : throw new InvalidOperationException("The approved client setup requires a provider not installed in this application.");
        ServiceProvider Cache(ManagedRepositoryModel<T> model, IManagedLocalRepositoryProvider<T> provider)
        {
            var services = new ServiceCollection();
            foreach (var step in steps) services.AddSingleton<IMigration>(step);
            services.AddKeyedSingleton<IObjectSerializer<T>>(ManagedProviderContext<T>.ServiceKey, model.Serializer);
            provider.Register(services, new(model, root));
            return services.BuildServiceProvider();
        }
        async ValueTask Validate(CancellationToken token)
        {
            await authority.ValidateAccessAsync(false, token).ConfigureAwait(false);
            if (validateAccessAsync is not null) await validateAccessAsync(token).ConfigureAwait(false);
        }
        var plans = models.Select(model => new RepositoryVersionPlan<T>(model.Manifest,
            async (context, token) =>
            {
                await Validate(token).ConfigureAwait(false);
                var provider = SelectedProvider();
                if (context.PreviousVersion is not { } previous) return;
                var prior = models.Single(item => item.Manifest.ContractVersion == previous);
                var targetPath = Path.GetFullPath(provider.GetVersionDirectory(new(model, root)));
                if (!targetPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A managed provider must keep its version directories beneath its cache root.");
                // Never reuse a partial migration: the old generation may have accepted new offline writes.
                if (Directory.Exists(targetPath)) Directory.Move(targetPath, targetPath + ".unactivated-" + Guid.NewGuid().ToString("N"));
                await using var target = Cache(model, provider);
                await provider.MigrateAsync(new(prior, root), new(model, root), target, token).ConfigureAwait(false);
                await using var oldRemote = await remoteProvider.OpenAsync(prior, token).ConfigureAwait(false);
                await using var newRemote = await remoteProvider.OpenAsync(model, token).ConfigureAwait(false);
                if (oldRemote.Repository is not IRepositoryCacheIdentity oldIdentity || newRemote.Repository is not IRepositoryCacheIdentity newIdentity)
                    throw new NotSupportedException("Managed remote providers must identify their account and dataset for cache migration.");
                await RepositoryCacheMigration.RebindAsync(
                    (IEnvelopeMetadataRepository<T>)target.GetRequiredKeyedService<IRepository<T>>(ManagedProviderContext<T>.ServiceKey),
                    accountScope, await oldIdentity.GetCacheIdentityAsync(token).ConfigureAwait(false),
                    await newIdentity.GetCacheIdentityAsync(token).ConfigureAwait(false), mapper, token: token).ConfigureAwait(false);
            },
            async token =>
            {
                await Validate(token).ConfigureAwait(false);
                var provider = Cache(model, SelectedProvider());
                RepositoryLease<T>? remote = null;
                try
                {
                    remote = await remoteProvider.OpenAsync(model, token).ConfigureAwait(false);
                    var composed = new ResilientRepository<T>(provider.GetRequiredKeyedService<IRepository<T>>(ManagedProviderContext<T>.ServiceKey),
                        remote.Repository, mapper, new ResilientRepositoryOptions<T>
                        {
                            DatasetId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authority.Scope))),
                            Telemetry = telemetry ?? model.StoragePolicy.Telemetry,
                            CacheScope = accountScope, ReadPreference = settings.ReadPreference,
                            EnableBackgroundSynchronization = true, BackgroundRetryDelay = retry,
                            CursorStore = new FileRepositoryCursorStore(Path.Combine(root, "cursor-" + model.Manifest.ContractVersion + ".json")),
                            ValidateAccessAsync = Validate, IsUnavailableException = unavailable, IsEndpointWideFailureException = unavailable
                        });
                    return new RepositoryLease<T>(composed, async () =>
                    {
                        try { await composed.DisposeAsync().ConfigureAwait(false); }
                        finally
                        {
                            try { await remote.DisposeAsync().ConfigureAwait(false); }
                            finally { await provider.DisposeAsync().ConfigureAwait(false); }
                        }
                    });
                }
                catch
                {
                    try { if (remote is not null) await remote.DisposeAsync().ConfigureAwait(false); }
                    finally { await provider.DisposeAsync().ConfigureAwait(false); }
                    throw;
                }
            }));
        return new(authority, plans, new FileRepositoryCursorStore(Path.Combine(root, "running-version.json")), accountScope, retry, unavailable, telemetry);
    }
}
