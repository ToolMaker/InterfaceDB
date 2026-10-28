using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class TcpTransportTests
{
    private const string Alias = "test-store";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(15);
    private TestCertificates _certificates = null!;
    private string _path = null!;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _certificates = new();
        _path = Path.Combine(Path.GetTempPath(), "InterfaceDB.Transport.TlsTests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _certificates.Dispose();
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }

    [TestMethod]
    public async Task MutualTlsOpaqueWritesAndAtomicBatchSurviveSegmentCoreReopen()
    {
        await using (var backend = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(backend)))
        await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host)))
        {
            await client.PutAsync("opaque", [0xc1, 0xff, 0x80, 0]);
            CollectionAssert.AreEqual(new byte[] { 0xc1, 0xff, 0x80, 0 }, await client.GetAsync("opaque"));
            await client.PutAsync("empty", []);
            Assert.HasCount(0, (await client.GetAsync("empty"))!);
            Assert.IsNull(await client.GetAsync("missing"));
            await client.ApplyBatchAsync([new("opaque", null), new("a", [1]), new("b", [2])]);
            Assert.IsNull(await client.GetAsync("opaque"));
            var page = await client.EnumerateAsync(2);
            CollectionAssert.AreEqual(new[] { "a", "b" }, page.Records!.Select(r => r.Key).ToArray());
            Assert.AreEqual("b", page.NextAfterKey);
            await client.ExecuteAsync(new() { Command = EncodedStoreCommand.Checkpoint });
        }
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_path);
        Assert.IsNull(await reopened.GetAsync("opaque"));
        CollectionAssert.AreEqual(new byte[] { 1 }, await reopened.GetAsync("a"));
        CollectionAssert.AreEqual(new byte[] { 2 }, await reopened.GetAsync("b"));
        Assert.HasCount(0, (await reopened.GetAsync("empty"))!);
    }

    [TestMethod]
    public async Task DefaultServerTrustRejectsPrivateCaWithoutExecutingStorage()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await AssertTlsFailureCannotFallBackAsync(async () =>
        {
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host) with { ServerCertificateValidation = null });
            await client.ExecuteAsync(Ping());
        });
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task PrivateCaValidationStillRejectsWrongServerHostname()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await AssertTlsFailureCannotFallBackAsync(async () =>
        {
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host) with { TargetHost = "wrong-host.invalid" });
            await client.ExecuteAsync(Ping());
        });
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TlsSetupOnlyTreatsCloseBeforeAnyResponseAsUnavailable(int responseKind)
    {
        byte[] response = responseKind switch
        {
            0 => [],
            1 => [22], // Truncated TLS header is not an empty transport close.
            2 => [21, 3, 3, 0, 2, 2, 40], // Fatal TLS handshake_failure alert.
            _ => [255, 255, 255, 0, 1, 255] // Malformed TLS record.
        };
        using var timeout = new CancellationTokenSource(TestDeadline);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = ReplyToClientHelloAsync(listener, response, timeout.Token);
        try
        {
            var exception = await Assert.ThrowsAsync<Exception>(async () =>
            {
                await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(new()
                {
                    Host = IPAddress.Loopback.ToString(), Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                    TargetHost = "localhost", StoreAlias = Alias, Manifest = Manifest(),
                    ClientCertificate = _certificates.Client, ServerCertificateValidation = _certificates.ValidateServer,
                    ConnectTimeout = TimeSpan.FromSeconds(5)
                }, timeout.Token);
            });
            Assert.AreEqual(responseKind == 0,
                Idb.Transport.Repositories.TcpRepositoryAvailability.IsUnavailable(exception), exception.ToString());
            if (responseKind == 0) Assert.IsInstanceOfType<EndOfStreamException>(exception);
        }
        finally { await server; }
    }

    private static async Task ReplyToClientHelloAsync(TcpListener listener, byte[] response, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        var stream = client.GetStream();
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        Assert.AreEqual(22, (int)header[0]);
        var payload = new byte[(header[3] << 8) | header[4]];
        await stream.ReadExactlyAsync(payload, token);
        if (response.Length > 0) await stream.WriteAsync(response, token);
        client.Client.Shutdown(SocketShutdown.Send);
        try { while (await stream.ReadAsync(header, token) != 0) { } }
        catch (IOException exception) when (exception.InnerException is SocketException) { }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostRejectsUntrustedClientCertificateBeforeStorage(bool platformTrust)
    {
        using var untrusted = new TestCertificates();
        var backend = new CountingStore();
        var options = HostOptions(backend);
        if (platformTrust) options = options with { ClientCertificateValidation = null };
        await using var host = await TcpStoreHost.StartAsync(options);
        await AssertTlsFailureAsync(async () =>
        {
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host) with
            {
                ClientCertificate = platformTrust ? _certificates.Client : untrusted.Client
            });
            await client.ExecuteAsync(Ping());
        });
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task HostRejectsAbsentClientCertificateBeforeStorage()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        using var deadline = new CancellationTokenSource(TestDeadline);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.LocalEndPoint.Port, deadline.Token);
        using var stream = new SslStream(tcp.GetStream(), false, _certificates.ValidateServer);
        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token);
            await MessagePackFrameCodec.WriteAsync(stream, Hello(), new(), deadline.Token);
            var result = await MessagePackFrameCodec.ReadAsync(stream, new(), deadline.Token);
            Assert.IsNull(result, "A client without a certificate must never complete negotiation.");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    [DataRow("authorization", RemoteErrorCode.Unauthorized)]
    [DataRow("alias", RemoteErrorCode.UnknownStore)]
    [DataRow("manifest", RemoteErrorCode.IncompatibleManifest)]
    [DataRow("capability", RemoteErrorCode.UnsupportedCapability)]
    public async Task NegotiationRejectsMismatchBeforeAnyStorageOperation(string mismatch, RemoteErrorCode expected)
    {
        var backend = new CountingStore
        {
            Capabilities = mismatch == "capability"
                ? EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits : EncodedStoreCapabilities.All
        };
        var hostOptions = HostOptions(backend) with { AuthorizeClient = (_, _) => mismatch != "authorization" };
        await using var host = await TcpStoreHost.StartAsync(hostOptions);
        var options = ClientOptions(host);
        if (mismatch == "alias") options = options with { StoreAlias = "unconfigured" };
        if (mismatch == "manifest") options = options with { Manifest = Manifest() with { ContractSchemaHash = "different" } };
        var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(options);
        });
        Assert.AreEqual(expected, exception.Code);
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task AliasHasOneSessionUntilDisconnectThenCanBeReacquired()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var first = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        var busy = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        });
        Assert.AreEqual(RemoteErrorCode.Busy, busy.Code);
        await first.DisposeAsync();
        await using var next = await ConnectAfterLeaseReleasedAsync(ClientOptions(host));
        await next.ExecuteAsync(Ping());
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    public async Task AbsoluteSessionLifetimeReleasesAliasEvenWhenClientRemainsActive()
    {
        var backend = new CountingStore();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            TimeProvider = clock, MaximumSessionLifetime = TimeSpan.FromMinutes(5)
        });
        await using var first = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        await first.ExecuteAsync(Ping());
        var busy = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        });
        Assert.AreEqual(RemoteErrorCode.Busy, busy.Code);

        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await first.ExecuteAsync(Ping()));
        Assert.AreEqual(RemoteErrorCode.DeadlineExceeded, expired.Code);
        await using var next = await ConnectAfterLeaseReleasedAsync(ClientOptions(host));
        await next.ExecuteAsync(Ping());
        Assert.AreEqual(2, backend.Calls);
    }

    [TestMethod]
    public async Task SessionLifetimeCancelsAnInFlightOperationBeforeReleasingItsLease()
    {
        var entered = Signal();
        var backend = new CountingStore
        {
            Handler = async (request, token) =>
            {
                if (request.Command != EncodedStoreCommand.Put) return new();
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new();
            }
        };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            MaximumSessionLifetime = TimeSpan.FromSeconds(2),
            MaximumOperationTimeout = TimeSpan.FromSeconds(5)
        });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));

        var mutation = client.PutAsync("cancel-at-session-boundary", [1]).AsTask();
        await entered.Task.WaitAsync(TestDeadline);
        await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () =>
            await mutation.WaitAsync(TestDeadline));
        await using var next = await ConnectAfterLeaseReleasedAsync(ClientOptions(host));
        await next.ExecuteAsync(Ping());
        Assert.AreEqual(2, backend.Calls);
    }

    [TestMethod]
    public async Task NegotiatedLimitsUseIntersectionAndRejectBeforeStore()
    {
        var backend = new CountingStore();
        var hostLimits = new TransportLimits
        {
            MaximumFrameBytes = 32 * 1024, MaximumPayloadBytes = 2000, MaximumKeyBytes = 40,
            MaximumBatchItems = 8, MaximumPageItems = 10
        };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with { Limits = hostLimits });
        var clientLimits = hostLimits with { MaximumPayloadBytes = 3000, MaximumKeyBytes = 20, MaximumBatchItems = 4 };
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host) with { Limits = clientLimits });
        Assert.AreEqual(2000, client.Limits.MaximumPayloadBytes);
        Assert.AreEqual(20, client.Limits.MaximumKeyBytes);
        Assert.AreEqual(4, client.Limits.MaximumBatchItems);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.PutAsync("key", new byte[2001]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await client.GetAsync(new string('x', 21)));
        Assert.AreEqual(0, backend.Calls);
        await client.PutAsync("key", new byte[2000]);
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    public async Task ConcurrentRequestsOnOneClientRemainSerializedAndCorrelated()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async number =>
        {
            var key = "key-" + number;
            await client.PutAsync(key, BitConverter.GetBytes(number));
            var result = await client.GetAsync(key);
            Assert.AreEqual(number, BitConverter.ToInt32(result!));
        })).WaitAsync(TestDeadline);
        Assert.AreEqual(48, backend.Calls);
        Assert.AreEqual(1, backend.MaximumActiveCalls);
    }

    [TestMethod]
    public async Task CancelledCallBeforeSendDoesNotExecuteOrBreakSession()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await client.PutAsync("key", [1], cancellation.Token));
        Assert.AreEqual(0, backend.Calls);
        await client.ExecuteAsync(Ping());
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    public async Task MutationDeadlineIsUnknownAndLeaseStaysHeldUntilStorageFinishes()
    {
        var entered = Signal();
        var release = Signal();
        var finished = Signal();
        var backend = new CountingStore
        {
            Handler = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
                return new() { AffectedCount = 1 };
            }
        };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with { MaximumOperationTimeout = TimeSpan.FromMilliseconds(200) });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        try
        {
            var mutation = client.PutAsync("key", [1]).AsTask();
            await entered.Task.WaitAsync(TestDeadline);
            await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () => await mutation.WaitAsync(TestDeadline));
            Assert.AreEqual(1, backend.Calls, "An uncertain write must not be replayed automatically.");
            var busy = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            {
                await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
            });
            Assert.AreEqual(RemoteErrorCode.Busy, busy.Code);
            release.TrySetResult();
            await finished.Task.WaitAsync(TestDeadline);
            await using var reconnect = await ConnectAfterLeaseReleasedAsync(ClientOptions(host));
            Assert.AreEqual(1, backend.Calls, "Reconnection itself must not replay the mutation.");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task ReadDeadlineIsReportedWithoutMutationAmbiguity()
    {
        var entered = Signal();
        var release = Signal();
        var backend = new CountingStore
        {
            Handler = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new();
            }
        };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with { MaximumOperationTimeout = TimeSpan.FromMilliseconds(200) });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        try
        {
            var read = client.GetAsync("key").AsTask();
            await entered.Task.WaitAsync(TestDeadline);
            var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await read.WaitAsync(TestDeadline));
            Assert.AreEqual(RemoteErrorCode.DeadlineExceeded, exception.Code);
            Assert.AreEqual(1, backend.Calls);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task StorageFailureIsRedactedWhileLocalDiagnosticsReceiveCause()
    {
        const string secret = "sensitive path C:/private/data and secret-token-123";
        var diagnostic = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new CountingStore { Handler = (_, _) => throw new IOException(secret) };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with { OnError = error => diagnostic.TrySetResult(error) });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.GetAsync("key"));
        Assert.AreEqual(RemoteErrorCode.StorageFailure, error.Code);
        Assert.IsFalse(error.ToString().Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual(secret, (await diagnostic.Task.WaitAsync(TestDeadline)).Message);
    }

    [TestMethod]
    public async Task HostDisposalDrainsStorageAndDisconnectedMutationHasUnknownOutcome()
    {
        var committed = Signal();
        var releaseAcknowledgement = Signal();
        var backend = new CountingStore
        {
            Handler = async (_, _) =>
            {
                committed.TrySetResult(); // Emulates a durable commit before returning its acknowledgement.
                await releaseAcknowledgement.Task;
                return new() { AffectedCount = 1 };
            }
        };
        var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host));
        try
        {
            var mutation = client.PutAsync("key", [1]).AsTask();
            await committed.Task.WaitAsync(TestDeadline);
            var stopping = host.DisposeAsync().AsTask();
            Assert.IsFalse(stopping.IsCompleted, "The host must drain backend work even after closing the network connection.");
            await Assert.ThrowsExactlyAsync<RemoteCommitOutcomeUnknownException>(async () => await mutation.WaitAsync(TestDeadline));
            Assert.AreEqual(1, backend.Calls);
            Assert.IsFalse(stopping.IsCompleted);
            releaseAcknowledgement.TrySetResult();
            await stopping.WaitAsync(TestDeadline);
            Assert.IsFalse(backend.Disposed, "The host borrows the backend and must not dispose it.");
            await host.DisposeAsync();
        }
        finally
        {
            releaseAcknowledgement.TrySetResult();
            await host.DisposeAsync().AsTask().WaitAsync(TestDeadline);
        }
    }

    private TcpStoreHostOptions HostOptions(IEncodedEnvelopeStore backend) => new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
        Stores = [new(Alias, Manifest(), backend)], ClientCertificateValidation = _certificates.ValidateClient,
        AuthorizeClient = (certificate, alias) => certificate.Thumbprint == _certificates.Client.Thumbprint && alias == Alias,
        HandshakeTimeout = TimeSpan.FromSeconds(5), MaximumOperationTimeout = TimeSpan.FromSeconds(5),
        OnError = error => TestContext.WriteLine(error.ToString())
    };

    private TcpStoreClientOptions ClientOptions(TcpStoreHost host) => new()
    {
        Host = IPAddress.Loopback.ToString(), Port = host.LocalEndPoint.Port, TargetHost = "localhost",
        StoreAlias = Alias, Manifest = Manifest(), ClientCertificate = _certificates.Client,
        ServerCertificateValidation = _certificates.ValidateServer, ConnectTimeout = TimeSpan.FromSeconds(5),
        OperationTimeout = TimeSpan.FromSeconds(5)
    };

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0", EnvelopeFormat = "IDB6-or-IDBE",
        ContractIdentity = "test", ContractVersion = "1.0", ContractSchemaHash = "schema", ImplementationManifestHash = "types",
        IndexDefinitionHash = "indexes"
    };

    private static EncodedStoreRequest Ping() => new() { Command = EncodedStoreCommand.Ping };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WireMessage Hello() => new()
    {
        Kind = WireMessageKind.Hello, CorrelationId = 1, StoreAlias = Alias, Manifest = Manifest(),
        Limits = new(), Capabilities = EncodedStoreCapabilities.All
    };

    private static async Task AssertTlsFailureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TestDeadline); }
        catch (Exception exception) when (exception is AuthenticationException or IOException) { return; }
        Assert.Fail("TLS must reject the connection before a storage operation succeeds.");
    }

    private static async Task AssertTlsFailureCannotFallBackAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TestDeadline); }
        catch (Exception exception) when (exception is AuthenticationException or IOException)
        {
            Assert.IsFalse(Idb.Transport.Repositories.TcpRepositoryAvailability.IsUnavailable(exception), exception.ToString());
            return;
        }
        Assert.Fail("TLS must reject the connection without permitting offline fallback.");
    }

    private static async Task<TcpEncodedEnvelopeStore> ConnectAfterLeaseReleasedAsync(TcpStoreClientOptions options)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return await TcpEncodedEnvelopeStore.ConnectAsync(options, deadline.Token); }
            catch (RemoteStoreException error) when (error.Code == RemoteErrorCode.Busy) { }
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class CountingStore : IEncodedEnvelopeStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private int _calls;
        private int _active;
        private int _maximumActive;
        internal int Calls => Volatile.Read(ref _calls);
        internal int MaximumActiveCalls => Volatile.Read(ref _maximumActive);
        internal bool Disposed { get; private set; }
        internal Func<EncodedStoreRequest, CancellationToken, ValueTask<EncodedStoreResponse>>? Handler { get; init; }
        public EncodedStoreCapabilities Capabilities { get; init; } = EncodedStoreCapabilities.All;

        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensionsMax(ref _maximumActive, active);
            try
            {
                if (Handler is not null) return await Handler(request, cancellationToken);
                switch (request.Command)
                {
                    case EncodedStoreCommand.Get:
                        var found = _values.TryGetValue(request.Key!, out var payload);
                        return new() { Found = found, Payload = payload?.ToArray() };
                    case EncodedStoreCommand.Put:
                        _values[request.Key!] = request.Payload!.ToArray();
                        return new() { AffectedCount = 1 };
                    case EncodedStoreCommand.Ping: return new();
                    default: throw new NotSupportedException();
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        private static void InterlockedExtensionsMax(ref int value, int candidate)
        {
            int previous;
            do { previous = Volatile.Read(ref value); if (candidate <= previous) return; }
            while (Interlocked.CompareExchange(ref value, candidate, previous) != previous);
        }
    }
}
