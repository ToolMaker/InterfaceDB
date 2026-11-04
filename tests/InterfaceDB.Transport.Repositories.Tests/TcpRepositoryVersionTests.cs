using Idb.Libraries.Repositories;
using System.Buffers.Binary;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task Version_NewClientWaitsForServerAndReopensAfterUpgrade_OldClientFaults()
    {
        var path = Path.Combine(_root, "master");
        var master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]);
        var host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with { Stores = [master.Registration] });
        int port = host.LocalEndPoint.Port;
        var template = Options(host, Token("parent-a"));
        var prepared = new List<string>();
        RepositoryVersionPlan<IRemotePerson> Plan(CompatibilityManifest manifest) => new(manifest,
            (context, token) => { lock (prepared) prepared.Add(context.SelectedManifest.ContractVersion); return ValueTask.CompletedTask; },
            token =>
            {
                var repository = new TcpRepository<IRemotePerson>(template with
                { Contract = new VersionPersonConfig(manifest.ContractVersion), Connection = template.Connection with { Manifest = manifest } });
                return ValueTask.FromResult(new RepositoryLease<IRemotePerson>(repository, repository.DisposeAsync));
            });
        await using var managed = new LocalRemoteRepository<IRemotePerson>([Plan(Manifest()), Plan(Manifest2())],
            new FileRepositoryCursorStore(Path.Combine(_root, "new-version.json")), "parent-a", template.Connection, TimeSpan.FromMilliseconds(50));
        await using var old = new LocalRemoteRepository<IRemotePerson>([Plan(Manifest())],
            new FileRepositoryCursorStore(Path.Combine(_root, "old-version.json")), "parent-a", template.Connection, TimeSpan.FromMilliseconds(50));
        try
        {
            await managed.InitializeAsync();
            await old.InitializeAsync();
            Assert.AreEqual("1.0.0", managed.RunningVersion);
            lock (prepared) Assert.IsFalse(prepared.Contains("2.0.0"));
            var oldEdit = await managed.SaveAsync(new RemotePerson { Name = "person", Score = 2 });
            await host.DisposeAsync();
            await master.DisposeAsync();
            master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1(), StoreV2()]);
            host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with
            { Stores = [master.Registration], ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
            await Until(() => Task.FromResult(managed.RunningVersion == "2.0.0"));
            Assert.AreEqual(12, (await managed.QuerySingleAsync(_ => true)).Score);
            await Assert.ThrowsExactlyAsync<RepositoryVersionChangedException>(async () => await managed.SaveAsync(oldEdit));
            await Until(() => Task.FromResult(old.Connection.State == RepositoryConnectionState.Faulted));
            var error = await Assert.ThrowsExactlyAsync<RepositoryUpgradeRequiredException>(async () => await old.QueryFirstAsync(_ => true));
            Assert.AreEqual("2.0.0", error.RequiredVersion);
            lock (prepared) Assert.AreEqual(1, prepared.Count(x => x == "2.0.0"));
        }
        finally { await host.DisposeAsync(); await master.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Version_StandaloneChoosesLatestWithoutRemoteAuthority()
    {
        var prepared = new List<string>();
        RepositoryVersionPlan<IRemotePerson> Plan(CompatibilityManifest manifest) => new(manifest,
            (context, token) => { prepared.Add(context.SelectedManifest.ContractVersion); return ValueTask.CompletedTask; },
            token =>
            {
                var services = new ServiceCollection();
                services.AddSegmentCoreRepository<IRemotePerson>(new VersionPersonConfig(manifest.ContractVersion, Path.Combine(_root, "standalone")),
                    runtimeTypeAllowlist: new(typeof(RemotePerson)));
                var provider = services.BuildServiceProvider();
                return ValueTask.FromResult(new RepositoryLease<IRemotePerson>(
                    provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("people"), provider.DisposeAsync));
            });
        await using var repository = new LocalRemoteRepository<IRemotePerson>([Plan(Manifest()), Plan(Manifest2())],
            new FileRepositoryCursorStore(Path.Combine(_root, "local-version.json")), "local-master");
        await repository.InitializeAsync();
        Assert.AreEqual("2.0.0", repository.RunningVersion);
        CollectionAssert.AreEqual(new[] { "2.0.0" }, prepared);
        await repository.SaveAsync(new RemotePerson { Name = "local", Score = 3 });
        Assert.AreEqual(3, (await repository.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Version_FirstOfflineStartWaitsForAuthorityAndReconnects()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var host = await Host(store);
        var options = Options(host, Token("parent-a"));
        var port = host.LocalEndPoint.Port;
        await host.DisposeAsync();
        int prepared = 0;
        var plan = new RepositoryVersionPlan<IRemotePerson>(Manifest(),
            (context, token) => { prepared++; return ValueTask.CompletedTask; },
            token =>
            {
                var remote = new TcpRepository<IRemotePerson>(options);
                return ValueTask.FromResult(new RepositoryLease<IRemotePerson>(remote, remote.DisposeAsync));
            });
        await using var repository = new LocalRemoteRepository<IRemotePerson>([plan],
            new FileRepositoryCursorStore(Path.Combine(_root, "version.json")), "parent-a", options.Connection, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExactlyAsync<System.Net.Sockets.SocketException>(async () => await repository.InitializeAsync());
        Assert.IsNull(repository.RunningVersion);
        Assert.AreEqual(0, prepared);
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
        await Until(() => Task.FromResult(repository.RunningVersion == "1.0.0" && repository.Connection.State == RepositoryConnectionState.Online));
        Assert.AreEqual(1, prepared);
    }
}
