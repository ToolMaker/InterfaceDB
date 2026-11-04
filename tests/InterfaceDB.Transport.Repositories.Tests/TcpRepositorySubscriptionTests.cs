using System.Net;
using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Push_CompositionHasNoIdleStorageReadsOrReplicationEcho(RepositoryReadPreference mode)
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using var writer = Client(host, "parent-a");
        await using var cache = Cache("idle");
        await using var repository = Compose(cache, remote, mode, background: true);
        await repository.InitializeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 1));
        await Seed(writer, "person", 1);
        await Until(async () => (await Local(cache).QueryFirstAsync(_ => true))?.Score == 1);
        await Settle(store);
        var idle = store.Operations;
        await Task.Delay(400); // Eight configured retry intervals: a healthy subscription stays idle.
        Assert.AreEqual(idle, store.Operations, "An idle subscriber queried storage.");
        var record = await repository.QuerySingleAsync(_ => true);
        record.Score = 2;
        await repository.SaveAsync(record);
        await Until(async () => (await writer.QuerySingleAsync(_ => true)).Score == 2);
        await Settle(store);
        idle = store.Operations;
        var mutations = store.Mutations;
        await Task.Delay(400);
        Assert.AreEqual(idle, store.Operations, "Notification feedback must settle.");
        Assert.AreEqual(mutations, store.Mutations, "Replication must not echo writes.");
        await writer.DeleteAsync(await writer.QuerySingleAsync(_ => true));
        await Until(async () => await Local(cache).QueryFirstAsync(_ => true) is null);
        await repository.DisposeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
    }

    [TestMethod]
    public async Task Push_ReconnectCatchesChangesMissedWhileDisconnectedWithoutLocalWrites()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var port = host.LocalEndPoint.Port;
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("missed");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst, background: true);
        await repository.InitializeAsync();
        await Seed(remote, "person", 1);
        await Until(async () => (await Local(cache).QueryFirstAsync(_ => true))?.Score == 1);
        await host.DisposeAsync();
        await using (var otherHost = await Host(store))
        await using (var writer = Client(otherHost, "parent-a"))
        {
            var value = await writer.QuerySingleAsync(_ => true);
            value.Score = 9;
            await writer.SaveAsync(value);
        }
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(IPAddress.Loopback, port) });
        await Until(async () => (await Local(cache).QueryFirstAsync(_ => true))?.Score == 9);
        // A fresh operation may catch up while the previous subscription is still unwinding.
        // Require re-registration to complete, then prove subsequent changes arrive by push.
        await Until(() => Task.FromResult(restarted.ActiveSubscriptions == 1));
        await using var connectedWriter = Client(restarted, "parent-a");
        var next = await connectedWriter.QuerySingleAsync(_ => true);
        next.Score = 10;
        await connectedWriter.SaveAsync(next);
        await Until(async () => (await Local(cache).QueryFirstAsync(_ => true))?.Score == 10);
        Assert.AreEqual(1, restarted.ActiveSubscriptions);
    }
}
