using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using Microsoft.IdentityModel.Tokens;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class JwtTransportTests
{
    private const string Alias = "jwt-test-store";
    private const string Issuer = "https://issuer.example.test/realms/integration";
    private const string Audience = "interfacedb-storage";
    private const string Subject = "trusted-service";
    private TestCertificates _certificates = null!;
    private RSA _signingKey = null!;
    private ManualClock _clock = null!;
    private JwtAccessTokenValidator _validator = null!;

    [TestInitialize]
    public void Initialize()
    {
        _certificates = new();
        _signingKey = RSA.Create(2048);
        _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        _validator = new(new JwtAccessTokenValidatorOptions
        {
            Authorities = [new()
            {
                Id = "integration", Issuer = Issuer, Audiences = [Audience], ClockSkew = TimeSpan.Zero,
                SigningKeys = [new RsaSecurityKey(_signingKey.ExportParameters(false)) { KeyId = "integration-key" }]
            }]
        }, timeProvider: _clock);
    }

    [TestCleanup]
    public void Cleanup()
    {
        (_validator as IDisposable)?.Dispose();
        _signingKey.Dispose();
        _certificates.Dispose();
    }

    [TestMethod]
    [DataRow(TcpStoreAuthenticationMode.JwtBearer)]
    [DataRow(TcpStoreAuthenticationMode.MutualTlsAndJwtBearer)]
    public async Task ValidJwtAuthenticatesWithConfiguredTlsMode(TcpStoreAuthenticationMode mode)
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend, mode));
        var options = ClientOptions(host, IssueToken(), mode);
        if (mode == TcpStoreAuthenticationMode.JwtBearer) Assert.IsNull(options.ClientCertificate);
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(options);

        await client.PutAsync("opaque", [0xc1, 0xff, 0]);
        CollectionAssert.AreEqual(new byte[] { 0xc1, 0xff, 0 }, await client.GetAsync("opaque"));

        Assert.AreEqual(2, backend.Calls);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("signature")]
    [DataRow("issuer")]
    [DataRow("audience")]
    [DataRow("expired")]
    [DataRow("missing-subject")]
    public async Task InvalidJwtIsRejectedBeforeStorageAndDoesNotLeaseAlias(string defect)
    {
        var backend = new CountingStore();
        var diagnostics = new ConcurrentQueue<Exception>();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with { OnError = diagnostics.Enqueue });
        var token = IssueToken(defect);

        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, token));
        });

        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        Assert.AreEqual(0, backend.Calls);
        Assert.IsFalse(rejected.ToString().Contains(token, StringComparison.Ordinal), "The client error must not expose the credential.");
        Assert.IsFalse(string.Join("\n", diagnostics.Select(error => error.ToString())).Contains(token, StringComparison.Ordinal),
            "Host diagnostics must not expose the rejected credential.");
        await using var accepted = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));
        await accepted.PutAsync("accepted", [1]);
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    [DataRow(TcpStoreAuthenticationMode.JwtBearer)]
    [DataRow(TcpStoreAuthenticationMode.MutualTlsAndJwtBearer)]
    public async Task HostRejectsMissingJwtAfterTlsWithoutExecutingStorage(TcpStoreAuthenticationMode mode)
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend, mode));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.LocalEndPoint.Port, deadline.Token);
        using var stream = new SslStream(tcp.GetStream(), false, _certificates.ValidateServer);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = mode == TcpStoreAuthenticationMode.MutualTlsAndJwtBearer
                ? new X509CertificateCollection { _certificates.Client } : null,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, deadline.Token);
        await MessagePackFrameCodec.WriteAsync(stream, Hello(mode, token: null), new(), deadline.Token);

        var reply = await MessagePackFrameCodec.ReadAsync(stream, new(), deadline.Token);

        Assert.IsNotNull(reply);
        Assert.AreEqual(WireMessageKind.HelloResult, reply.Kind);
        Assert.AreEqual(RemoteErrorCode.AuthenticationRequired, reply.Error);
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task OversizedHelloIsRejectedBeforeJwtValidationAndOperationalFramesRemainAvailable()
    {
        var backend = new CountingStore();
        var validator = new CountingTokenValidator(_validator);
        var diagnostics = new ConcurrentQueue<Exception>();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            TokenValidator = validator, MaximumConnections = 1, OnError = diagnostics.Enqueue
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, host.LocalEndPoint.Port, deadline.Token);
            using var stream = new SslStream(tcp.GetStream(), false, _certificates.ValidateServer);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token);
            var header = MessagePackFrameCodec.Encode(Hello(TcpStoreAuthenticationMode.JwtBearer, null), new())
                [..MessagePackFrameCodec.HeaderBytes];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4),
                (uint)(TransportValidation.MaximumHelloFrameBytes - MessagePackFrameCodec.HeaderBytes + 1));
            await stream.WriteAsync(header, deadline.Token);
            try
            {
                var closed = await stream.ReadAsync(new byte[1], deadline.Token);
                Assert.AreEqual(0, closed);
            }
            catch (IOException) { }
        }

        Assert.AreEqual(0, validator.Calls);
        Assert.AreEqual(0, backend.Calls);
        Assert.IsTrue(diagnostics.OfType<RemoteStoreException>()
            .Any(error => error.Code == RemoteErrorCode.LimitExceeded));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));
        var payload = RandomNumberGenerator.GetBytes(96 * 1024);
        await client.PutAsync("large-authenticated-payload", payload);
        CollectionAssert.AreEqual(payload, await client.GetAsync("large-authenticated-payload"));
        Assert.AreEqual(1, validator.Calls);
        Assert.AreEqual(2, backend.Calls);
    }

    [TestMethod]
    [DataRow("alias")]
    [DataRow("scope")]
    [DataRow("capability")]
    public async Task ValidIdentityNeedsStoreAndCapabilityAuthorization(string denied)
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        var token = IssueToken(scope: denied == "scope" ? "unrelated" : denied == "capability" ? "storage.read" : "storage.full");
        var options = ClientOptions(host, token);
        if (denied == "alias") options = options with { StoreAlias = "another-store" };

        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(options);
        });

        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task AuthorizationIsCheckedAgainBeforeExecutingAMutation()
    {
        var backend = new CountingStore();
        var permitWrites = true;
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            AuthorizeIdentity = (identity, alias, capabilities) => Authorize(identity, alias, capabilities)
                && (permitWrites || !capabilities.HasFlag(EncodedStoreCapabilities.Write))
        });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));
        await client.GetAsync("missing");
        permitWrites = false;

        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.PutAsync("denied", [2]));

        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        Assert.AreEqual(1, backend.Calls, "The rejected write must never reach the backend.");
    }

    [TestMethod]
    [DataRow("write")]
    [DataRow("delete")]
    public async Task BatchAuthorizationRequiresEveryMutationCapability(string deniedCapability)
    {
        var backend = new CountingStore();
        var permit = true;
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            AuthorizeIdentity = (identity, alias, capabilities) => Authorize(identity, alias, capabilities)
                && (permit || (deniedCapability == "write"
                    ? !capabilities.HasFlag(EncodedStoreCapabilities.Write)
                    : !capabilities.HasFlag(EncodedStoreCapabilities.Delete)))
        });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));
        permit = false;
        var mutations = deniedCapability == "write"
            ? new[] { new EncodedMutation("denied-write", new byte[] { 1 }) }
            : [new EncodedMutation("denied-delete", null)];

        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            await client.ApplyBatchAsync(mutations));

        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        Assert.AreEqual(0, backend.Calls, "A denied batch must never reach the backend.");
    }

    [TestMethod]
    public async Task FullyAuthorizedMixedBatchReachesBackend()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));

        var response = await client.ApplyBatchAsync(
            [new("written", [1]), new("deleted", null)]);

        Assert.AreEqual(1, response.AffectedCount);
        Assert.AreEqual(1, backend.Calls);
    }

    [TestMethod]
    public async Task SubjectIsolationLeasesSessionsPerAuthenticatedSubject()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            IsolateBySubject = true,
            AuthorizeIdentity = (identity, alias, _) => identity.AuthorityId == "integration" && alias == Alias
        });
        await using var first = await TcpEncodedEnvelopeStore.ConnectAsync(
            ClientOptions(host, IssueToken(subject: "subject-a")));
        await using var second = await TcpEncodedEnvelopeStore.ConnectAsync(
            ClientOptions(host, IssueToken(subject: "subject-b")));
        await first.PutAsync("same", [1]);
        await second.PutAsync("same", [2]);
        CollectionAssert.AreEqual(new byte[] { 1 }, await first.GetAsync("same"));
        CollectionAssert.AreEqual(new byte[] { 2 }, await second.GetAsync("same"));

        var busy = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var duplicate = await TcpEncodedEnvelopeStore.ConnectAsync(
                ClientOptions(host, IssueToken(subject: "subject-a")));
        });
        Assert.AreEqual(RemoteErrorCode.Busy, busy.Code);
    }

    [TestMethod]
    public async Task TokenExpiringDuringAuthorizationCannotStartMutation()
    {
        var backend = new CountingStore();
        var authorizedWrites = 0;
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend) with
        {
            AuthorizeIdentity = (identity, alias, capabilities) =>
            {
                if (!Authorize(identity, alias, capabilities)) return false;
                if (capabilities == EncodedStoreCapabilities.Write)
                {
                    Interlocked.Increment(ref authorizedWrites);
                    _clock.Advance(TimeSpan.FromMinutes(6));
                }
                return true;
            }
        });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));

        var expired = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.PutAsync("expired-during-authorization", [2]));

        Assert.AreEqual(RemoteErrorCode.AuthenticationExpired, expired.Code);
        Assert.AreEqual(1, authorizedWrites, "The token must expire inside the successful command authorization callback.");
        Assert.AreEqual(0, backend.Calls, "An expired identity must not reach the backend even when authorization succeeds.");
    }

    [TestMethod]
    public async Task ExpiredSessionRejectsMutationBeforeBackendAndNewTokenCanReconnect()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()));
        await client.PutAsync("before-expiry", [1]);
        _clock.Advance(TimeSpan.FromMinutes(6));

        var expired = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.PutAsync("after-expiry", [2]));

        Assert.AreEqual(RemoteErrorCode.AuthenticationExpired, expired.Code);
        Assert.AreEqual(1, backend.Calls, "An expired identity must not start a storage mutation.");
        await client.DisposeAsync();
        await using var fresh = await ConnectAfterLeaseReleasedAsync(ClientOptions(host, IssueToken()));
        await fresh.PutAsync("fresh-session", [3]);
        Assert.AreEqual(2, backend.Calls);
    }

    [TestMethod]
    public async Task JwtModeStillRejectsWrongServerHostname()
    {
        var backend = new CountingStore();
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend));
        await AssertTlsRejectedAsync(async () =>
        {
            await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, IssueToken()) with
            {
                TargetHost = "wrong.example.test"
            });
        });
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public async Task CombinedModeRejectsMissingClientCertificateDespiteValidJwt()
    {
        var backend = new CountingStore();
        var mode = TcpStoreAuthenticationMode.MutualTlsAndJwtBearer;
        await using var host = await TcpStoreHost.StartAsync(HostOptions(backend, mode));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            await MessagePackFrameCodec.WriteAsync(stream, Hello(mode, IssueToken()), new(), deadline.Token);
            Assert.IsNull(await MessagePackFrameCodec.ReadAsync(stream, new(), deadline.Token),
                "A token must not substitute for the required client certificate.");
        }
        catch (Exception error) when (error is AuthenticationException or IOException) { }
        Assert.AreEqual(0, backend.Calls);
    }

    private TcpStoreHostOptions HostOptions(IEncodedEnvelopeStore backend, TcpStoreAuthenticationMode mode = TcpStoreAuthenticationMode.JwtBearer) => new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
        Stores = [new(Alias, Manifest(), backend)], AuthenticationMode = mode,
        TokenValidator = _validator, AuthorizeIdentity = Authorize, TimeProvider = _clock,
        ClientCertificateValidation = mode == TcpStoreAuthenticationMode.JwtBearer ? null : _certificates.ValidateClient,
        AuthorizeClient = mode == TcpStoreAuthenticationMode.JwtBearer ? null
            : (certificate, alias) => certificate.GetCertHashString(HashAlgorithmName.SHA256)
                == _certificates.Client.GetCertHashString(HashAlgorithmName.SHA256) && alias == Alias,
        HandshakeTimeout = TimeSpan.FromSeconds(5), MaximumOperationTimeout = TimeSpan.FromSeconds(5)
    };

    private TcpStoreClientOptions ClientOptions(TcpStoreHost host, string token,
        TcpStoreAuthenticationMode mode = TcpStoreAuthenticationMode.JwtBearer) => new()
    {
        Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost",
        StoreAlias = Alias, Manifest = Manifest(), AuthenticationMode = mode,
        ClientCertificate = mode == TcpStoreAuthenticationMode.JwtBearer ? null : _certificates.Client,
        TokenProvider = new FixedTokenProvider(token), ServerCertificateValidation = _certificates.ValidateServer,
        ConnectTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5)
    };

    private static bool Authorize(AuthenticatedIdentity identity, string alias, EncodedStoreCapabilities capabilities)
    {
        if (identity.AuthorityId != "integration" || identity.Principal.FindFirst("sub")?.Value != Subject || alias != Alias) return false;
        var scopes = (identity.Principal.FindFirst("scope")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (scopes.Contains("storage.full", StringComparer.Ordinal)) return true;
        const EncodedStoreCapabilities read = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.GetMany
            | EncodedStoreCapabilities.Enumerate | EncodedStoreCapabilities.DurableCommits;
        return scopes.Contains("storage.read", StringComparer.Ordinal) && (capabilities & ~read) == 0;
    }

    private string IssueToken(string? defect = null, string scope = "storage.full", string subject = Subject)
    {
        if (defect == "malformed") return "jwt-test-secret-do-not-log";
        var now = _clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = defect == "issuer" ? "https://untrusted.example.test/realms/other" : Issuer,
            ["aud"] = defect == "audience" ? "unrelated-audience" : Audience,
            ["sub"] = subject, ["scope"] = scope,
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = (defect == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5)).ToUnixTimeSeconds()
        };
        if (defect == "missing-subject") claims.Remove("sub");
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt", kid = "integration-key" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
        using var wrongKey = defect == "signature" ? RSA.Create(2048) : null;
        var signature = (wrongKey ?? _signingKey).SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Encode(signature);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1", EnvelopeFormat = "IDB6",
        ContractIdentity = "jwt-test", ContractVersion = "1.0.0", ContractSchemaHash = "schema",
        ImplementationManifestHash = "types", IndexDefinitionHash = "indexes"
    };

    private static WireMessage Hello(TcpStoreAuthenticationMode mode, string? token) => new()
    {
        Kind = WireMessageKind.Hello, CorrelationId = 1, StoreAlias = Alias, Manifest = Manifest(),
        Limits = new(), Capabilities = EncodedStoreCapabilities.All, AuthenticationMode = mode, AccessToken = token
    };

    private static async Task AssertTlsRejectedAsync(Func<Task> action)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is AuthenticationException or IOException) { return; }
        Assert.Fail("TLS must reject the peer before a storage request is accepted.");
    }

    private static async Task<TcpEncodedEnvelopeStore> ConnectAfterLeaseReleasedAsync(TcpStoreClientOptions options)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return await TcpEncodedEnvelopeStore.ConnectAsync(options, deadline.Token); }
            catch (RemoteStoreException error) when (error.Code == RemoteErrorCode.Busy)
            {
                await Task.Delay(20, deadline.Token);
            }
        }
    }

    private sealed class FixedTokenProvider(string value) : IAccessTokenProvider
    {
        public ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(value));
        }
    }

    private sealed class CountingTokenValidator(IAccessTokenValidator inner) : IAccessTokenValidator
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);

        public ValueTask<AuthenticatedIdentity> ValidateAsync(string token, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.ValidateAsync(token, cancellationToken);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long _unixSeconds = now.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _unixSeconds));
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _unixSeconds, checked((long)duration.TotalSeconds));
    }

    private sealed class CountingStore : IEncodedEnvelopeStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public EncodedStoreCapabilities Capabilities => EncodedStoreCapabilities.All;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (request.Command == EncodedStoreCommand.Put)
            {
                _values[request.Key!] = request.Payload!.ToArray();
                return ValueTask.FromResult(new EncodedStoreResponse { AffectedCount = 1 });
            }
            if (request.Command == EncodedStoreCommand.Get)
            {
                var found = _values.TryGetValue(request.Key!, out var value);
                return ValueTask.FromResult(new EncodedStoreResponse { Found = found, Payload = value?.ToArray() });
            }
            if (request.Command == EncodedStoreCommand.ApplyBatch)
            {
                var affected = 0;
                foreach (var mutation in request.Mutations!)
                {
                    if (mutation.Payload is not null)
                    {
                        _values[mutation.Key] = mutation.Payload.ToArray();
                        affected++;
                    }
                    else if (_values.TryRemove(mutation.Key, out _)) affected++;
                }
                return ValueTask.FromResult(new EncodedStoreResponse { AffectedCount = affected });
            }
            if (request.Command == EncodedStoreCommand.Ping) return ValueTask.FromResult(new EncodedStoreResponse());
            throw new NotSupportedException();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
