using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Version_ComposedCacheMigratesPendingWritesAndKeepsApplicationSubscription(RepositoryReadPreference mode)
    {
        var path = Path.Combine(_root, "master");
        var master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]);
        var host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with { Stores = [master.Registration] });
        int port = host.LocalEndPoint.Port;
        var v1 = Options(host, Token("parent-a")) with { RuntimeTypes = new(typeof(RemotePerson), typeof(PersonMigration)) };
        var v2 = v1 with { Contract = new VersionPersonConfig("2.0.0"), Connection = v1.Connection with { Manifest = Manifest2() } };
        LocalRemoteRepository<IRemotePerson> Open() => SegmentCoreManagedRepository.Create(
            [v1, v2], Path.Combine(_root, "managed-cache"), "parent-a", new PersonMapper(), mode,
            [new PersonMigration()], retryDelay: TimeSpan.FromMilliseconds(50));
        try
        {
            await using (var client = new TcpRepository<IRemotePerson>(v1)) await Seed(client, "person", 1);
            await using (var managed = Open())
            {
                await managed.InitializeAsync();
                await Until(async () => managed.Connection.State == RepositoryConnectionState.Online
                    && (await managed.QueryFirstAsync(_ => true)) is not null);
                Assert.AreEqual("1.0.0", managed.RunningVersion);
                await host.DisposeAsync();
                await Until(() => Task.FromResult(managed.Connection.IsDowntime));
                var pending = await managed.QuerySingleAsync(_ => true);
                pending.Score = 2;
                await managed.SaveAsync(pending);
            }
            await using var reopened = Open();
            await reopened.InitializeAsync();
            Assert.AreEqual("1.0.0", reopened.RunningVersion);
            Assert.AreEqual(2, (await reopened.QuerySingleAsync(_ => true)).Score);
            var notice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = reopened.SubscribeChanges(() =>
            { if (reopened.RunningVersion == "2.0.0") notice.TrySetResult(); return ValueTask.CompletedTask; });
            await subscription.Ready;
            await master.DisposeAsync();
            master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1(), StoreV2()]);
            host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with
            { Stores = [master.Registration], ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
            try { await notice.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { Assert.Fail("Version activation failed: " + reopened.Connection.State + " " + reopened.Connection.LastFailure); }
            await Until(() => Task.FromResult(reopened.RunningVersion == "2.0.0"
                && reopened.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(12, (await reopened.QuerySingleAsync(_ => true)).Score);
            await using var remote = new TcpRepository<IRemotePerson>(v2);
            Assert.AreEqual(12, (await remote.QuerySingleAsync(_ => true)).Score);
            Assert.AreEqual(mode, reopened.EffectiveReadPreference);
            var loop = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var feedback = reopened.SubscribeChanges(async () =>
            {
                try { await reopened.SaveAsync(await reopened.QuerySingleAsync(_ => true)); }
                catch (Exception exception) { loop.TrySetResult(exception); }
            });
            await feedback.Ready;
            var changed = await remote.QuerySingleAsync(_ => true); changed.Score = 20;
            await remote.SaveAsync(changed);
            Assert.IsInstanceOfType<RepositoryFeedbackLoopException>(await loop.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally { await host.DisposeAsync(); await master.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Version_LegacyAdoptionCopiesWithoutChangingSource()
    {
        var source = Path.Combine(_root, "legacy");
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(source))
        await using (var host = await Host(store))
        await using (var remote = Client(host, "parent-a")) await Seed(remote, "person", 1);
        await using (var adopted = await VersionedTcpStore.OpenAsync(Path.Combine(_root, "versioned"), "people",
            [StoreV1(), StoreV2()], legacyStoreDirectory: source, legacyManifest: Manifest()))
            Assert.AreEqual("2.0.0", adopted.RunningVersion);
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(source))
        await using (var host = await Host(store))
        await using (var remote = Client(host, "parent-a"))
            Assert.AreEqual(1, (await remote.QuerySingleAsync(_ => true)).Score);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var invalid = await VersionedTcpStore.OpenAsync(source, "people", [StoreV1()]);
        });
    }

    [TestMethod]
    public async Task Version_RetryAfterInterruptedCacheMigrationPreservesNewOfflineEdits()
    {
        var path = Path.Combine(_root, "master");
        var cachePath = Path.Combine(_root, "managed-cache");
        var master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1()]);
        var host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with { Stores = [master.Registration] });
        int port = host.LocalEndPoint.Port;
        var v1 = Options(host, Token("parent-a")) with { RuntimeTypes = new(typeof(RemotePerson), typeof(PersonMigration)) };
        var v2 = v1 with { Contract = new VersionPersonConfig("2.0.0"), Connection = v1.Connection with { Manifest = Manifest2() } };
        var mapper = new MigrationFailureMapper();
        LocalRemoteRepository<IRemotePerson> Open() => SegmentCoreManagedRepository.Create([v1, v2], cachePath,
            "parent-a", mapper, RepositoryReadPreference.LocalFirst, [new PersonMigration()], retryDelay: TimeSpan.FromMilliseconds(50));
        try
        {
            await using (var remote = new TcpRepository<IRemotePerson>(v1)) await Seed(remote, "person", 1);
            await using (var managed = Open())
            {
                await managed.InitializeAsync();
                await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
                await host.DisposeAsync();
                await Until(() => Task.FromResult(managed.Connection.IsDowntime));
                var item = await managed.QuerySingleAsync(_ => true); item.Score = 2; await managed.SaveAsync(item);
                mapper.Fail = true;
                await master.DisposeAsync();
                master = await VersionedTcpStore.OpenAsync(path, "people", [StoreV1(), StoreV2()]);
                host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with
                { Stores = [master.Registration], ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
                // The v1 data subscription can report a manifest mismatch before version discovery reaches the rebind.
                await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Faulted
                    && managed.Connection.LastFailure is InvalidDataException { Message: "Injected rebind failure" }));
            }
            await host.DisposeAsync();
            mapper.Fail = false;
            await using var retry = Open();
            await retry.InitializeAsync();
            Assert.AreEqual("1.0.0", retry.RunningVersion);
            var latest = await retry.QuerySingleAsync(_ => true); latest.Score = 3; await retry.SaveAsync(latest);
            host = await TcpStoreHost.StartAsync(HostOptions(master.Registration.Store) with
            { Stores = [master.Registration], ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
            await Until(() => Task.FromResult(retry.RunningVersion == "2.0.0" && retry.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(13, (await retry.QuerySingleAsync(_ => true)).Score);
            await using var finalRemote = new TcpRepository<IRemotePerson>(v2);
            Assert.AreEqual(13, (await finalRemote.QuerySingleAsync(_ => true)).Score);
            Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(cachePath, ".segment-core"), "2.0.0.unactivated-*").Length);
        }
        finally { await host.DisposeAsync(); await master.DisposeAsync(); }
    }

    private sealed class MigrationFailureMapper : IResilientEntityMapper<IRemotePerson>
    {
        public volatile bool Fail;
        public string GetStorageKey(IRemotePerson value) => value.Name;
        public IRemotePerson CreateDetachedCopy(IRemotePerson value)
        {
            if (Fail && value.Envelope?.VersionTag == "2.0.0") throw new InvalidDataException("Injected rebind failure");
            return new RemotePerson { Name = value.Name, Score = value.Score };
        }
        public void CopyValues(IRemotePerson source, IRemotePerson target) { target.Name = source.Name; target.Score = source.Score; }
    }

    [MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
    public sealed class PersonMigration : IRemotePerson, IMigration
    {
        public string Name { get; set; } = "";
        public int Score { get; set; }
        [IgnoreMember] public IObjectEnvelope<IRemotePerson>? Envelope { get; private set; }
        [IgnoreMember] public string SourceVersion => "1.0.0";
        [IgnoreMember] public string TargetVersion => "2.0.0";
        public bool Up() { Score += 10; return true; }
        public bool Down() => throw new NotSupportedException();
    }
}
