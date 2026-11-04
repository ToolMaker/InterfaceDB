using System.Net;
using System.Net.Sockets;
using Idb.Commercial.Repositories;
using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Recovery_ServerClosesBeforeTlsReplyPreservesOfflineCacheAndQueuedWrites(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "handshake-server"));
        var host = await Host(store);
        var port = host.LocalEndPoint.Port;
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("handshake-cache");
        await using var composed = Compose(cache, remote, mode);
        try
        {
            Assert.IsTrue((await composed.SynchronizeAsync()).IsSuccessful);
            await host.DisposeAsync();
            await using (var stoppingServer = new ClosingHandshakeServer(port))
            {
                var result = await composed.SynchronizeAsync();
                Assert.IsFalse(result.IsSuccessful);
                Assert.IsTrue(result.Failures.All(failure => failure.IsTransient), FailureText(result));
                Assert.IsTrue(composed.Connection.IsDowntime);
                var edit = await composed.QuerySingleAsync(_ => true);
                Assert.AreEqual(1, edit.Score);
                edit.Score = 2;
                await composed.SaveAsync(edit);
                Assert.AreEqual(2, (await Local(cache).QuerySingleAsync(_ => true)).Score);
                Assert.AreEqual(RepositoryReadPreference.LocalFirst, composed.EffectiveReadPreference);
            }

            host = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(IPAddress.Loopback, port) });
            var recovered = await composed.SynchronizeAsync();
            Assert.IsTrue(recovered.IsSuccessful, FailureText(recovered));
            Assert.AreEqual(mode, composed.EffectiveReadPreference);
            Assert.AreEqual(2, (await remote.QuerySingleAsync(_ => true)).Score);
        }
        finally { await host.DisposeAsync(); }
    }

    // Drain the complete ClientHello before FIN, avoiding a platform-dependent reset
    // caused by closing a socket with unread input. No TLS or database response is sent.
    private sealed class ClosingHandshakeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _worker;

        public ClosingHandshakeServer(int port)
        {
            _listener = new(IPAddress.Loopback, port);
            _listener.Start();
            _worker = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var header = new byte[5];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    Assert.AreEqual(22, (int)header[0], "The first TLS record must be a handshake.");
                    var payload = new byte[(header[3] << 8) | header[4]];
                    await stream.ReadExactlyAsync(payload, _stop.Token);
                    client.Client.Shutdown(SocketShutdown.Send);
                    try { while (await stream.ReadAsync(header, _stop.Token) != 0) { } }
                    catch (IOException exception) when (exception.InnerException is SocketException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _worker; }
            finally { _stop.Dispose(); }
        }
    }
}
