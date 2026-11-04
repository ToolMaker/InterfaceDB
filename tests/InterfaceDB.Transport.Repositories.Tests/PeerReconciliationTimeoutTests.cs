using System.Net;
using Idb.Commercial.Repositories.Peers;
using Idb.Transport.Peers;

namespace InterfaceDB.Transport.Tests;

public sealed partial class PeerReplicatedRepositoryTests
{
    [TestMethod]
    public async Task Peer_SlowPeerTimesOutWithoutCancellingTheWholeReconciliation()
    {
        var limits = Limits with { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await TcpPeerRelayHost.StartAsync(new()
        {
            ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
            TokenValidator = _validator, AuthorizeIdentity = (_, alias, _) => alias == "entries", Limits = limits,
            Datasets = [new("entries", Manifest(false), ledger)]
        });
        var timedOut = 0;
        var slowFactory = new TcpPeerConnectionFactory(Options(host.LocalEndPoint.Port, false), Issuer, "reader", limits);
        await using var slow = await slowFactory.ConnectAsync(async (request, token) =>
        {
            if (request.Command == PeerCommand.Inventory)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Increment(ref timedOut); }
            }
            return new PeerMessage { Records = [] };
        });
        var statuses = 0;
        await using var healthy = await slowFactory.ConnectAsync((request, _) =>
        {
            if (request.Command == PeerCommand.Status) Interlocked.Increment(ref statuses);
            return ValueTask.FromResult(new PeerMessage { Records = [] });
        });
        for (var i = 0; i < 3; i++)
            await healthy.RequestAsync(new() { Command = PeerCommand.Reconcile }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(0, Volatile.Read(ref timedOut));
        Assert.IsGreaterThanOrEqualTo(3, Volatile.Read(ref statuses));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => healthy.RequestAsync(new() { Command = PeerCommand.Reconcile }, cancelled.Token).AsTask());
        await healthy.RequestAsync(new() { Command = PeerCommand.Reconcile }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
