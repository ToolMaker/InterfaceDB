using Idb.Libraries.Repositories;
using System.Text;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task Delta_DurableCursorAvoidsSnapshotAfterCacheRestart()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        var counting = new FeedCountingStore(store);
        await using var host = await Host(counting);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "one", 1);
        var cursor = new FileRepositoryCursorStore(Path.Combine(_root, "cursor.json"));
        ResilientRepository<IRemotePerson> Open(ServiceProvider cache) => new(Local(cache), remote, new PersonMapper(), new ResilientRepositoryOptions<IRemotePerson>()
        {
            DatasetId = "people", CacheScope = "parent-a", ReadPreference = RepositoryReadPreference.LocalFirst,
            EnableBackgroundSynchronization = false, CursorStore = cursor,
            IsUnavailableException = TcpRepositoryAvailability.IsUnavailable
        });
        await using (var cache = Cache("persisted"))
        await using (var composed = Open(cache))
        {
            Assert.IsTrue((await composed.SynchronizeAsync()).IsSuccessful);
            Assert.AreEqual(1, (await composed.QuerySingleAsync(_ => true)).Score);
        }
        var before = counting.Enumerations;
        await using (var cache = Cache("persisted"))
        await using (var composed = Open(cache))
        {
            Assert.IsTrue((await composed.SynchronizeAsync()).IsSuccessful);
            Assert.AreEqual(before, counting.Enumerations);
            Assert.AreEqual(1, (await composed.QuerySingleAsync(_ => true)).Score);
        }
    }

    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Recovery_OfflineQueueSurvivesClientRestartAndAutomaticallyReconnects(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        var host = await Host(store);
        int port = host.LocalEndPoint.Port;
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        try
        {
            await using (var cache = Cache("recovery"))
            await using (var composed = Compose(cache, remote, mode, background: true))
            {
                await composed.InitializeAsync();
                await Until(async () => composed.Connection.State == RepositoryConnectionState.Online
                    && (await Local(cache).QueryFirstAsync(_ => true)) is not null);
                await host.DisposeAsync();
                await Until(() => Task.FromResult(composed.Connection.IsDowntime));
                var edit = await composed.QuerySingleAsync(_ => true);
                edit.Score = 2;
                await composed.SaveAsync(edit);
                await composed.SaveAsync(new RemotePerson { Name = "offline", Score = 7 });
                Assert.AreEqual(RepositoryReadPreference.LocalFirst, composed.EffectiveReadPreference);
            }
            await using var reopenedCache = Cache("recovery");
            await using var reopened = Compose(reopenedCache, remote, mode, background: true);
            await reopened.InitializeAsync();
            Assert.AreEqual(2, (await reopened.QuerySingleAsync(x => x.Name == "person")).Score);
            host = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
            await Until(async () => reopened.Connection.State == RepositoryConnectionState.Online
                && (await remote.QueryFirstAsync(x => x.Name == "offline")) is not null);
            Assert.AreEqual(mode, reopened.EffectiveReadPreference);
            Assert.AreEqual(2, (await remote.QuerySingleAsync(x => x.Name == "person")).Score);
            var other = await remote.QuerySingleAsync(x => x.Name == "person");
            other.Score = 3;
            await remote.SaveAsync(other);
            await Until(async () => (await Local(reopenedCache).QuerySingleAsync(x => x.Name == "person")).Score == 3);
        }
        finally { await host.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Delta_CacheFailureCannotAdvanceDurableCursor()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "one", 1);
        await using var cache = Cache("failed-import");
        var proxy = System.Reflection.DispatchProxy.Create<IEnvelopeMetadataRepository<IRemotePerson>, FailingCache>();
        var control = (FailingCache)(object)proxy;
        control.Inner = (IEnvelopeMetadataRepository<IRemotePerson>)Local(cache);
        var cursorPath = Path.Combine(_root, "applied-cursor.json");
        await using var composed = new ResilientRepository<IRemotePerson>(proxy, remote, new PersonMapper(),
            new ResilientRepositoryOptions<IRemotePerson>
            {
                DatasetId = "people", CacheScope = "parent-a", ReadPreference = RepositoryReadPreference.LocalFirst,
                EnableBackgroundSynchronization = false, CursorStore = new FileRepositoryCursorStore(cursorPath),
                IsUnavailableException = TcpRepositoryAvailability.IsUnavailable
            });
        Assert.IsTrue((await composed.SynchronizeAsync()).IsSuccessful);
        var receipt = await File.ReadAllBytesAsync(cursorPath);
        var edit = await remote.QuerySingleAsync(_ => true); edit.Score = 2; await remote.SaveAsync(edit);
        control.FailWrites = true;
        Assert.IsFalse((await composed.SynchronizeAsync()).IsSuccessful);
        CollectionAssert.AreEqual(receipt, await File.ReadAllBytesAsync(cursorPath));
        control.FailWrites = false;
        Assert.IsTrue((await composed.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual(2, (await composed.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Recovery_InitialRegistrationIsNotAnOutageAndReadsRemoteOnEmptyCache(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        using var notifications = new RepositoryChangePublisher();
        var endpoint = ControlledEndpoint(remote, notifications);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((ControlledPushProxy)(object)endpoint).ReadyGate = ready.Task;
        await using var cache = Cache("initial");
        await using var composed = Compose(cache, endpoint, mode, background: true);
        await composed.InitializeAsync();
        await Until(() => Task.FromResult(composed.Connection.State == RepositoryConnectionState.Reconnecting));
        try
        {
            Assert.IsFalse(composed.Connection.IsDowntime);
            Assert.AreEqual(1, (await composed.QuerySingleAsync(_ => true)).Score);
        }
        finally { ready.TrySetResult(); }
    }
    private sealed class FeedCountingStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        public int Enumerations;
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command == EncodedStoreCommand.Enumerate) Interlocked.Increment(ref Enumerations);
            return inner.ExecuteAsync(request, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
