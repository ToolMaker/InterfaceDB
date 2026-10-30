using System.Net;
using System.Text;
using System.Text.Json;
using Idb.Authentication;

namespace InterfaceDB.Authentication.Tests;

[TestClass]
public sealed class ClientCredentialsTokenTests
{
    private const string Authority = "https://identity.example/realms/database";
    private const string Endpoint = Authority + "/protocol/openid-connect/token";
    private const string TestSecret = "test-secret:+ with spaces";

    [TestMethod]
    public async Task UnexaminedDeepExceptionChainFailsClosed()
    {
        Exception nested = new System.Security.Authentication.AuthenticationException(TestSecret);
        for (var i = 0; i < 9; i++) nested = new IOException(TestSecret, nested);
        using var provider = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, TestSecret, nested)));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.Unavailable, error.Failure);
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    [DataRow(HttpRequestError.NameResolutionError, TokenAcquisitionFailure.Network)]
    [DataRow(HttpRequestError.ConnectionError, TokenAcquisitionFailure.Network)]
    [DataRow(HttpRequestError.SecureConnectionError, TokenAcquisitionFailure.Tls)]
    [DataRow(HttpRequestError.InvalidResponse, TokenAcquisitionFailure.Unavailable)]
    public async Task EndpointFailuresAreClassifiedBeforeSecretsAreRedacted(HttpRequestError category, TokenAcquisitionFailure expected)
    {
        var fail = true;
        using var provider = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((request, _) =>
        {
            if (fail) throw new HttpRequestException(category, TestSecret);
            return Result(request.Method == HttpMethod.Get ? Metadata() : Token());
        }));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(expected, error.Failure);
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.ToString().Contains(TestSecret, StringComparison.Ordinal));
        fail = false;
        Assert.AreEqual("fixture.token", (await provider.GetAccessTokenAsync()).Value);
    }

    [TestMethod]
    public async Task NestedTlsAndSecretSourceNetworkErrorsDoNotBecomeRetryableNetworkFailures()
    {
        using var tls = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, TestSecret,
                new System.Security.Authentication.AuthenticationException(TestSecret))));
        Assert.AreEqual(TokenAcquisitionFailure.Tls,
            (await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => tls.GetAccessTokenAsync().AsTask())).Failure);
        using var secret = new ClientCredentialsAccessTokenProvider(Options() with {
            ClientSecretProvider = _ => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)
        }, new MockHandler((_, _) => Result(Metadata())));
        Assert.AreEqual(TokenAcquisitionFailure.Credentials,
            (await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => secret.GetAccessTokenAsync().AsTask())).Failure);
        using var unrelatedCancellation = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((_, _) => throw new OperationCanceledException(TestSecret)));
        Assert.AreEqual(TokenAcquisitionFailure.Unavailable,
            (await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => unrelatedCancellation.GetAccessTokenAsync().AsTask())).Failure);
    }

    [TestMethod]
    public async Task BasicCredentialsAreFormEncodedAndOnlySentToDiscoveredTokenEndpoint()
    {
        var time = new ManualTimeProvider();
        var requests = 0;
        var secretReads = 0;
        var handler = new MockHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            Assert.AreEqual("application/json", request.Headers.Accept.Single().MediaType);
            if (request.Method == HttpMethod.Get)
            {
                Assert.AreEqual(Authority + "/.well-known/openid-configuration", request.RequestUri!.AbsoluteUri);
                Assert.IsNull(request.Headers.Authorization);
                Assert.IsNull(request.Content);
                Assert.AreEqual(0, secretReads);
                return Metadata();
            }
            Assert.AreEqual(Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("test%3A+client:test-secret%3A%2B+with+spaces",
                Encoding.ASCII.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            var form = ParseForm(await request.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual("application/x-www-form-urlencoded", request.Content.Headers.ContentType!.MediaType);
            Assert.AreEqual("client_credentials", form["grant_type"]);
            Assert.AreEqual("database.read database.write", form["scope"]);
            Assert.IsFalse(form.ContainsKey("client_secret"));
            Assert.IsFalse(form.ContainsKey("client_id"));
            return Token("first.token.value");
        });
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        {
            ClientId = "test: client",
            Scope = "database.read database.write",
            ClientSecretProvider = _ => { secretReads++; return ValueTask.FromResult(TestSecret); }
        }, handler, time);

        var token = await provider.GetAccessTokenAsync();
        Assert.AreEqual("first.token.value", token.Value);
        Assert.AreEqual(time.GetUtcNow().AddSeconds(120), token.ExpiresAt);
        Assert.AreSame(token, await provider.GetAccessTokenAsync());
        Assert.AreEqual(2, requests);
        Assert.AreEqual(1, secretReads);
        Assert.IsFalse(token.ToString().Contains(token.Value, StringComparison.Ordinal));
        Assert.IsFalse(provider.ToString().Contains(TestSecret, StringComparison.Ordinal));
        Assert.IsFalse(Options().ToString().Contains(TestSecret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PostCredentialsReadEnvironmentAtRefreshAndRespectAdvertisedMethod()
    {
        var variable = "INTERFACEDB_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        var time = new ManualTimeProvider();
        var seenSecrets = new List<string>();
        var metadataRequests = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        {
            ClientSecretProvider = null,
            ClientSecretEnvironmentVariable = variable,
            AuthenticationMethod = ClientSecretAuthenticationMethod.Post
        }, new MockHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                metadataRequests++;
                return Metadata(methods: ["client_secret_post"]);
            }
            Assert.IsNull(request.Headers.Authorization);
            var form = ParseForm(await request.Content!.ReadAsStringAsync(ct));
            Assert.AreEqual("service-client", form["client_id"]);
            seenSecrets.Add(form["client_secret"]);
            return Token("token-" + seenSecrets.Count);
        }), time);
        try
        {
            Environment.SetEnvironmentVariable(variable, TestSecret);
            await provider.GetAccessTokenAsync();
            Environment.SetEnvironmentVariable(variable, "rotated-test-secret");
            time.Advance(TimeSpan.FromSeconds(90));
            await provider.GetAccessTokenAsync();
            CollectionAssert.AreEqual(new[] { TestSecret, "rotated-test-secret" }, seenSecrets);
            Assert.AreEqual(1, metadataRequests);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [TestMethod]
    public async Task ConcurrentAcquisitionHasOneRefreshAndCancelledWaiterDoesNotCancelIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            if (request.Method == HttpMethod.Get) return Metadata();
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Token("shared.token");
        }));
        var first = provider.GetAccessTokenAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        var waiter = provider.GetAccessTokenAsync(cancelled.Token).AsTask();
        var concurrent = Enumerable.Range(0, 20).Select(_ => provider.GetAccessTokenAsync().AsTask()).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        var token = await first.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var actual in await Task.WhenAll(concurrent).WaitAsync(TimeSpan.FromSeconds(10))) Assert.AreSame(token, actual);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task CachedTokenRefreshesBeforeExpiryAndDiscoveryRefreshesAtItsOwnDeadline()
    {
        var time = new ManualTimeProvider();
        var metadataRequests = 0;
        var tokenRequests = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        { DiscoveryCacheDuration = TimeSpan.FromSeconds(180) }, new MockHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) { metadataRequests++; return Result(Metadata()); }
            return Result(Token("token-" + ++tokenRequests));
        }), time);
        Assert.AreEqual("token-1", (await provider.GetAccessTokenAsync()).Value);
        time.Advance(TimeSpan.FromSeconds(89));
        Assert.AreEqual("token-1", (await provider.GetAccessTokenAsync()).Value);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("token-2", (await provider.GetAccessTokenAsync()).Value);
        Assert.AreEqual(1, metadataRequests);
        time.Advance(TimeSpan.FromSeconds(90));
        Assert.AreEqual("token-3", (await provider.GetAccessTokenAsync()).Value);
        Assert.AreEqual(2, metadataRequests);
    }

    [TestMethod]
    [DataRow("issuer")]
    [DataRow("http")]
    [DataRow("cross-origin")]
    [DataRow("userinfo")]
    [DataRow("fragment")]
    [DataRow("unsupported-method")]
    [DataRow("unsupported-grant")]
    [DataRow("duplicate")]
    public async Task UntrustedDiscoveryFailsBeforeSecretIsReadOrPosted(string invalid)
    {
        var requests = 0;
        var secretReads = 0;
        var content = invalid switch
        {
            "issuer" => JsonSerializer.Serialize(new { issuer = Authority + "/other", token_endpoint = Endpoint }),
            "http" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = "http://identity.example/token" }),
            "cross-origin" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = "https://identity.example.attacker.invalid/token" }),
            "userinfo" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = "https://user@identity.example/token" }),
            "fragment" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = Endpoint + "#fragment" }),
            "unsupported-method" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = Endpoint, token_endpoint_auth_methods_supported = new[] { "private_key_jwt" } }),
            "unsupported-grant" => JsonSerializer.Serialize(new { issuer = Authority, token_endpoint = Endpoint, grant_types_supported = new[] { "password" } }),
            _ => "{\"issuer\":\"" + Authority + "\",\"issuer\":\"" + Authority + "\",\"token_endpoint\":\"" + Endpoint + "\"}"
        };
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        { ClientSecretProvider = _ => { secretReads++; return ValueTask.FromResult(TestSecret); } },
            new MockHandler((_, _) => { requests++; return Result(Json(content)); }));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(invalid == "duplicate" ? TokenAcquisitionFailure.Http : TokenAcquisitionFailure.Metadata, error.Failure);
        Assert.AreEqual(1, requests);
        Assert.AreEqual(0, secretReads);
    }

    [TestMethod]
    public async Task ExplicitTrustedOriginAllowsSeparateTokenServiceAndSnapshotsOptions()
    {
        var trusted = new List<string> { "https://tokens.example" };
        var posts = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with { AdditionalTrustedTokenOrigins = trusted },
            new MockHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Get) return Result(Metadata(endpoint: "https://tokens.example/token"));
                Assert.AreEqual("https://tokens.example/token", request.RequestUri!.AbsoluteUri);
                posts++;
                return Result(Token());
            }));
        trusted.Clear();
        await provider.GetAccessTokenAsync();
        Assert.AreEqual(1, posts);
    }

    [TestMethod]
    public async Task PostAuthenticationRequiresExplicitDiscoverySupport()
    {
        var secretReads = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        {
            AuthenticationMethod = ClientSecretAuthenticationMethod.Post,
            ClientSecretProvider = _ => { secretReads++; return ValueTask.FromResult(TestSecret); }
        }, new MockHandler((_, _) => Result(Metadata())));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.Metadata, error.Failure);
        Assert.AreEqual(0, secretReads);
    }

    [TestMethod]
    [DataRow("missing-token")]
    [DataRow("token-type")]
    [DataRow("missing-expiry")]
    [DataRow("string-expiry")]
    [DataRow("zero-expiry")]
    [DataRow("huge-expiry")]
    [DataRow("whitespace-token")]
    [DataRow("oversized-token")]
    public async Task InvalidTokenResponsesAreNotCached(string invalid)
    {
        var values = new Dictionary<string, object?> { ["access_token"] = "fixture.token", ["token_type"] = "Bearer", ["expires_in"] = 120 };
        switch (invalid)
        {
            case "missing-token": values.Remove("access_token"); break;
            case "token-type": values["token_type"] = "MAC"; break;
            case "missing-expiry": values.Remove("expires_in"); break;
            case "string-expiry": values["expires_in"] = "120"; break;
            case "zero-expiry": values["expires_in"] = 0; break;
            case "huge-expiry": values["expires_in"] = long.MaxValue; break;
            case "whitespace-token": values["access_token"] = "token\ncredential"; break;
            case "oversized-token": values["access_token"] = new string('a', 129); break;
        }
        var posts = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with { MaximumAccessTokenBytes = 128 },
            new MockHandler((request, _) => Result(request.Method == HttpMethod.Get ? Metadata()
                : ++posts == 1 ? Json(JsonSerializer.Serialize(values)) : Token("recovered.token"))));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.TokenResponse, error.Failure);
        Assert.AreEqual("recovered.token", (await provider.GetAccessTokenAsync()).Value);
        Assert.AreEqual(2, posts);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DocumentSizeIsBoundedWithOrWithoutContentLength(bool declaredLength)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16 * 1024));
        var stream = new CountingNonSeekableStream(bytes);
        var content = new StreamContent(stream);
        if (declaredLength) content.Headers.ContentLength = bytes.Length;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        { MaximumDocumentBytes = 1024, MaximumAccessTokenBytes = 512 }, new MockHandler((_, _) => Result(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.Http, error.Failure);
        Assert.AreEqual(declaredLength ? 0 : 1025, stream.BytesRead);
    }

    [TestMethod]
    public async Task EndpointErrorsAndSecretSourceErrorsNeverExposeCredentialsOrResponseBody()
    {
        const string returnedToken = "must-not-appear-response-token";
        using (var provider = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((request, _) =>
                   Result(request.Method == HttpMethod.Get ? Metadata() : new(HttpStatusCode.BadRequest)
                   { Content = new StringContent(TestSecret + " " + returnedToken) }))))
        {
            var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
            Assert.AreEqual(TokenAcquisitionFailure.Http, error.Failure);
            Assert.IsFalse(error.ToString().Contains(TestSecret, StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains(returnedToken, StringComparison.Ordinal));
            Assert.IsNull(error.InnerException);
        }
        using (var provider = new ClientCredentialsAccessTokenProvider(Options() with
        { ClientSecretProvider = _ => throw new InvalidOperationException(TestSecret) }, new MockHandler((_, _) => Result(Metadata()))))
        {
            var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
            Assert.AreEqual(TokenAcquisitionFailure.Credentials, error.Failure);
            Assert.IsFalse(error.ToString().Contains(TestSecret, StringComparison.Ordinal));
            Assert.IsNull(error.InnerException);
        }
    }

    [TestMethod]
    public async Task DeadlineAndCallerCancellationAreDistinctAndAcquisitionCanRecover()
    {
        var requests = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with { RequestTimeout = TimeSpan.FromMilliseconds(100) },
            new MockHandler(async (request, ct) =>
            {
                if (Interlocked.Increment(ref requests) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return request.Method == HttpMethod.Get ? Metadata() : Token();
            }));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.Timeout, error.Failure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.GetAccessTokenAsync(cancelled.Token).AsTask());
        Assert.AreEqual(1, requests);
        Assert.AreEqual("fixture.token", (await provider.GetAccessTokenAsync()).Value);
    }

    [TestMethod]
    public async Task SecretSourceThatIgnoresCancellationStillHasBoundedWait()
    {
        var secret = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ClientCredentialsAccessTokenProvider(Options() with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100),
            ClientSecretProvider = _ => new ValueTask<string>(secret.Task)
        }, new MockHandler((_, _) => Result(Metadata())));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(TokenAcquisitionFailure.Timeout, error.Failure);
        secret.SetResult(TestSecret);
    }

    [TestMethod]
    public async Task RedirectIsRejectedAndNormalHandlersCannotEnableAutomaticRedirects()
    {
        var calls = 0;
        using var provider = new ClientCredentialsAccessTokenProvider(Options(), new MockHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://other.example/token");
            return Result(response);
        }));
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual(TokenAcquisitionFailure.Http, error.Failure);
        Assert.AreEqual(1, calls);
        using var redirecting = new HttpClientHandler();
        Assert.ThrowsExactly<ArgumentException>(() => new ClientCredentialsAccessTokenProvider(Options(), redirecting));
        using var nested = new PassThroughHandler { InnerHandler = new SocketsHttpHandler() };
        Assert.ThrowsExactly<ArgumentException>(() => new ClientCredentialsAccessTokenProvider(Options(), nested));
    }

    [TestMethod]
    public async Task DisposeCancelsInFlightRequestDisposesOwnedHandlerAndRejectsReuse()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MockHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Metadata();
        });
        var provider = new ClientCredentialsAccessTokenProvider(Options(), handler);
        var pending = provider.GetAccessTokenAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        provider.Dispose();
        provider.Dispose();
        var error = await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(TokenAcquisitionFailure.Unavailable, error.Failure);
        Assert.IsTrue(handler.Disposed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.GetAccessTokenAsync().AsTask());
    }

    [TestMethod]
    public async Task FixedProviderChecksExpiryCancellationAndRedactsDiagnostics()
    {
        var time = new ManualTimeProvider();
        var token = new AccessToken("caller.supplied.token", time.GetUtcNow().AddSeconds(10));
        var provider = new FixedAccessTokenProvider(token, time);
        Assert.AreSame(token, await provider.GetAccessTokenAsync());
        Assert.IsFalse(provider.ToString().Contains(token.Value, StringComparison.Ordinal));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => provider.GetAccessTokenAsync(cancelled.Token).AsTask());
        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<TokenAcquisitionException>(() => provider.GetAccessTokenAsync().AsTask());
        Assert.AreEqual("external.token", (await new FixedAccessTokenProvider(new AccessToken("external.token")).GetAccessTokenAsync()).Value);
        Assert.ThrowsExactly<TokenAcquisitionException>(() => new FixedAccessTokenProvider(new AccessToken("invalid token")));
    }

    [TestMethod]
    public void InvalidConfigurationCannotStartAnInsecureOrUnboundedAcquisition()
    {
        var invalid = new[]
        {
            Options() with { Authority = "http://identity.example" },
            Options() with { Authority = Authority + "?query=true" },
            Options() with { ClientSecretEnvironmentVariable = "ALSO_SET" },
            Options() with { ClientSecretProvider = null },
            Options() with { ClientSecretEnvironmentVariable = "BAD=NAME", ClientSecretProvider = null },
            Options() with { RequestTimeout = Timeout.InfiniteTimeSpan },
            Options() with { RefreshBeforeExpiry = TimeSpan.FromSeconds(-1) },
            Options() with { MaximumDocumentBytes = int.MaxValue },
            Options() with { MaximumAccessTokenBytes = int.MaxValue },
            Options() with { AdditionalTrustedTokenOrigins = ["https://tokens.example/a-path"] },
            Options() with { Scope = "read\nwrite" }
        };
        foreach (var options in invalid)
            Assert.ThrowsExactly<ArgumentException>(() => new ClientCredentialsAccessTokenProvider(options));
    }

    private static ClientCredentialsTokenOptions Options() => new()
    { Authority = Authority, ClientId = "service-client", ClientSecretProvider = _ => ValueTask.FromResult(TestSecret) };

    private static HttpResponseMessage Metadata(string endpoint = Endpoint, string[]? methods = null)
    {
        var data = new Dictionary<string, object> { ["issuer"] = Authority, ["token_endpoint"] = endpoint };
        if (methods is not null) data["token_endpoint_auth_methods_supported"] = methods;
        return Json(JsonSerializer.Serialize(data));
    }

    private static HttpResponseMessage Token(string value = "fixture.token")
        => Json(JsonSerializer.Serialize(new { access_token = value, token_type = "Bearer", expires_in = 120 }));

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> Result(HttpResponseMessage value) => Task.FromResult(value);

    private static Dictionary<string, string> ParseForm(string value) => value.Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x[1]));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class PassThroughHandler : DelegatingHandler;

    private sealed class CountingNonSeekableStream(byte[] bytes) : Stream
    {
        internal int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, bytes.Length - BytesRead);
            bytes.AsSpan(BytesRead, read).CopyTo(buffer.AsSpan(offset, read));
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, read).CopyTo(buffer);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}
