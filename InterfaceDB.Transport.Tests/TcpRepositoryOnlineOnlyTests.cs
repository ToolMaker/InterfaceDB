using System.Net;
using System.Net.Sockets;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task OnlineOnly_DisconnectDoesNotCreateLocalDatabaseOrQueueWrites_ExplicitSubscriptionCanReconnect()
    {
        var unusedLocalPath = Path.Combine(_root, "client-must-not-create");
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        await using var host = await Host(store);
        var port = host.LocalEndPoint.Port;
        var options = Options(host, Token("parent-a")) with { Contract = new PersonConfig(unusedLocalPath) };
        await using var client = new TcpRepository<IRemotePerson>(options);
        await client.SaveAsync(new RemotePerson { Name = "committed", Score = 1 });
        var hints = 0;
        using var changes = client.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        await changes.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(Volatile.Read(ref hints) > 0));
        Assert.IsFalse(Directory.Exists(unusedLocalPath));

        await host.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(() => changes.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<SocketException>(async () => await client.QueryFirstAsync(_ => true));
        await Assert.ThrowsAsync<SocketException>(async () => await client.SaveAsync(new RemotePerson { Name = "offline" }));
        Assert.IsFalse(Directory.Exists(unusedLocalPath));

        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(IPAddress.Loopback, port) });
        Assert.AreEqual(0, restarted.ActiveSubscriptions);
        Assert.AreEqual("committed", (await client.QuerySingleAsync(_ => true)).Name, "Disconnected writes must not be queued or replayed.");
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registered = client.SubscribeChanges(() => { delivered.TrySetResult(); return ValueTask.CompletedTask; });
        await registered.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await registered.DisposeAsync();
        await Until(() => Task.FromResult(restarted.ActiveSubscriptions == 0));
        Assert.IsFalse(Directory.Exists(unusedLocalPath));
    }
}
