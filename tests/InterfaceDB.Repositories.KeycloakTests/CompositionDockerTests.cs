using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore;
using InterfaceDB.TcpRepository.Sample;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Authentication.KeycloakTests;

public sealed partial class KeycloakTests
{
    private sealed partial class KeycloakFixture
    {
        partial void ConfigureAdditionalProbe(ref Func<int, string, Task>? probe) => probe = ProbeComposedTypedRepository;
        private async Task ProbeComposedTypedRepository(int port, string token)
        {
            foreach (var mode in new[] { RepositoryReadPreference.LocalFirst, RepositoryReadPreference.RemoteFirst })
            {
                await using var remote = new TcpRepository<IExampleProfile>(new()
                {
                    Contract = new ExampleProfileConfig(), RuntimeTypes = new(typeof(ExampleProfile)),
                    Connection = new()
                    {
                        Host = "127.0.0.1", Port = port, TargetHost = "localhost", StoreAlias = "example-profiles",
                        Manifest = ExampleProfileConfig.Manifest, AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
                        TokenProvider = new FixedAccessTokenProvider(new AccessToken(token)),
                        ServerCertificateValidation = _certificates.ValidateServer
                    }
                });
                var cachePath = Path.Combine(RootDirectory, "composed-" + mode);
                ServiceProvider OpenCache()
                {
                    var services = new ServiceCollection();
                    services.AddSegmentCoreRepository<IExampleProfile>(new ProfileCacheConfig(cachePath),
                        runtimeTypeAllowlist: new(typeof(ExampleProfile)));
                    return services.BuildServiceProvider();
                }
                ResilientRepository<IExampleProfile> Compose(ServiceProvider provider) => new(
                    provider.GetRequiredKeyedService<IRepository<IExampleProfile>>("cache"), remote, new ProfileMapper(),
                    new ResilientRepositoryOptions<IExampleProfile>
                    {
                        CacheScope = "keycloak-parent-a", ReadPreference = mode, EnableBackgroundSynchronization = true,
                        IsUnavailableException = TcpRepositoryAvailability.IsUnavailable,
                        IsEndpointWideFailureException = TcpRepositoryAvailability.IsUnavailable
                    });
                int counter;
                await using (var provider = OpenCache())
                await using (var repository = Compose(provider))
                {
                    var loaded = await repository.QuerySingleAsync(_ => true);
                    counter = loaded.Counter + 10;
                    loaded.Counter = counter;
                    await repository.SaveAsync(loaded);
                    // Both command directions must reach the remote and cache through automatic pub/sub.
                    // Do not use composed reads or explicit synchronization to make this assertion pass.
                    var local = provider.GetRequiredKeyedService<IRepository<IExampleProfile>>("cache");
                    using (var deliveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        while ((await remote.QuerySingleAsync(_ => true)).Counter != counter
                            || (await local.QuerySingleAsync(_ => true)).Counter != counter)
                            await Task.Delay(25, deliveryTimeout.Token);
                    }
                    var direct = await remote.QuerySingleAsync(_ => true);
                    direct.Counter = ++counter;
                    await remote.SaveAsync(direct);
                    // Inspect only the local provider: this must be refreshed by server push.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while ((await local.QuerySingleAsync(_ => true)).Counter != counter)
                        await Task.Delay(25, timeout.Token);
                }
                await using (var provider = OpenCache())
                await using (var reopened = Compose(provider))
                    Assert.AreEqual(counter, (await reopened.QuerySingleAsync(_ => true)).Counter);
            }
            Console.WriteLine("Composition: local-first queued writes, remote-first confirmed writes and server-pushed local cache refresh passed against real Keycloak JWTs and the isolated Linux database; local caches reopened successfully.");
        }

        private sealed class ProfileMapper : IResilientEntityMapper<IExampleProfile>
        {
            public string GetStorageKey(IExampleProfile entity) => "ExampleProfile";
            public IExampleProfile CreateDetachedCopy(IExampleProfile source)
                => new ExampleProfile { DisplayName = source.DisplayName, Counter = source.Counter };
            public void CopyValues(IExampleProfile source, IExampleProfile target)
            { target.DisplayName = source.DisplayName; target.Counter = source.Counter; }
        }

        private sealed class ProfileCacheConfig(string root) : IDbContractConfigDto<IExampleProfile>
        {
            public string EnvelopeDICacheKey => "cache";
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
}
