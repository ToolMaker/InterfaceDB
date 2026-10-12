using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class TransportBoundaryTests
{
    private static readonly TransportLimits Limits = new()
    {
        MaximumFrameBytes = 16 * 1024, MaximumPayloadBytes = 8192,
        MaximumBatchItems = 4, MaximumPageItems = 8
    };
    private TestCertificates _certificates = null!;

    [TestInitialize]
    public void Initialize() => _certificates = new();

    [TestCleanup]
    public void Cleanup() => _certificates.Dispose();

    [TestMethod]
    public async Task AggregateEncodeFailurePreservesSessionAndNextCorrelation()
    {
        var backend = new ResponseStore(request => new() { AffectedCount = request.Command == EncodedStoreCommand.Put ? 1 : 0 });
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host.LocalEndPoint.Port));
        var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await client.ApplyBatchAsync([new("a", new byte[8192]), new("b", new byte[8192])]));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
        Assert.AreEqual(0, backend.Calls);
        await client.PutAsync("valid", [1]);
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    [DataRow("put")]
    [DataRow("batch")]
    [DataRow("oversized")]
    [DataRow("null")]
    public async Task MalformedMutationAcknowledgementHasUnknownOutcome(string scenario)
    {
        var backend = new ResponseStore(_ => scenario switch
        {
            "oversized" => new() { AffectedCount = 1, Payload = new byte[8193] },
            "null" => null!,
            _ => new()
        });
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host.LocalEndPoint.Port));
        await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () =>
        {
            if (scenario == "batch") await client.ApplyBatchAsync([new("key", [1]), new("absent", null)]);
            else await client.PutAsync("key", [1]);
        });
        Assert.AreEqual(1, backend.Calls, "A malformed acknowledgement cannot cause an automatic replay.");
        await Assert.ThrowsExactlyAsync<IOException>(async () => await client.GetAsync("key"));
    }

    [TestMethod]
    [DataRow("get")]
    [DataRow("enumerate")]
    [DataRow("get-many")]
    public async Task MalformedReadResponseBreaksSession(string scenario)
    {
        var backend = new ResponseStore(_ => scenario switch
        {
            "get" => new() { Found = true },
            "get-many" => new() { Records = [new("unrequested", [1])] },
            _ => new() { Records = [new("b", [1]), new("a", [2])] }
        });
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host.LocalEndPoint.Port));
        var request = scenario switch
        {
            "get" => new EncodedStoreRequest { Command = EncodedStoreCommand.Get, Key = "key" },
            "get-many" => new() { Command = EncodedStoreCommand.GetMany, Keys = ["a"] },
            _ => new() { Command = EncodedStoreCommand.Enumerate, PageSize = 2 }
        };
        var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.ExecuteAsync(request));
        Assert.AreEqual(RemoteErrorCode.ProtocolError, exception.Code);
        Assert.AreEqual(1, backend.Calls);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await client.GetAsync("key"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WrongResponseCorrelationIsNeverAccepted(bool mutation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var server = WrongCorrelationPeerAsync(listener, deadline.Token);
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(((IPEndPoint)listener.LocalEndpoint).Port), deadline.Token);
            if (mutation)
                await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () => await client.PutAsync("key", [1], deadline.Token));
            else
            {
                var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.GetAsync("key", deadline.Token));
                Assert.AreEqual(RemoteErrorCode.ProtocolError, exception.Code);
            }
            await server.WaitAsync(deadline.Token);
            await Assert.ThrowsExactlyAsync<IOException>(async () => await client.GetAsync("key"));
        }
        finally { listener.Stop(); }
    }

    private async Task WrongCorrelationPeerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var tcp = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = new SslStream(tcp.GetStream(), false, _certificates.ValidateClient);
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificates.Server, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken);
        var hello = await MessagePackFrameCodec.ReadAsync(stream, Limits, cancellationToken);
        await MessagePackFrameCodec.WriteAsync(stream, new()
        {
            Kind = WireMessageKind.HelloResult, CorrelationId = 1, Manifest = hello!.Manifest,
            Limits = Limits, Capabilities = hello.Capabilities
        }, Limits, cancellationToken);
        var request = await MessagePackFrameCodec.ReadAsync(stream, Limits, cancellationToken);
        await MessagePackFrameCodec.WriteAsync(stream, new()
        {
            Kind = WireMessageKind.Response, CorrelationId = request!.CorrelationId + 1,
            Response = request.Request!.IsMutation ? new() { AffectedCount = 1 } : new()
        }, Limits, cancellationToken);
    }

    private TcpStoreHostOptions HostOptions(IEncodedEnvelopeStore store) => new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
        Stores = [new("boundary", Manifest(), store)], ClientCertificateValidation = _certificates.ValidateClient,
        AuthorizeClient = (certificate, alias) => certificate.Thumbprint == _certificates.Client.Thumbprint && alias == "boundary",
        Limits = Limits, HandshakeTimeout = TimeSpan.FromSeconds(5), MaximumOperationTimeout = TimeSpan.FromSeconds(5)
    };

    private TcpStoreClientOptions ClientOptions(int port) => new()
    {
        Host = IPAddress.Loopback.ToString(), Port = port, TargetHost = "localhost", StoreAlias = "boundary",
        Manifest = Manifest(), ClientCertificate = _certificates.Client, ServerCertificateValidation = _certificates.ValidateServer,
        Limits = Limits, ConnectTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5)
    };

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "1", StorageEngine = "test", StorageFormat = "1", EnvelopeFormat = "1",
        ContractIdentity = "test", ContractVersion = "1", ContractSchemaHash = "schema",
        ImplementationManifestHash = "types", IndexDefinitionHash = "indexes"
    };

    private sealed class ResponseStore(Func<EncodedStoreRequest, EncodedStoreResponse> response) : IEncodedEnvelopeStore
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public EncodedStoreCapabilities Capabilities => EncodedStoreCapabilities.All;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(response(request));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
