using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InterfaceDB.Authentication.Tests;

[TestClass]
public sealed class JwtAccessTokenValidatorTests
{
    private const string Issuer = "https://authority.test/realm";
    private static readonly Uri Jwks = new("https://authority.test/realm/keys");
    private static readonly Uri Metadata = new("https://authority.test/realm/.well-known/openid-configuration");
    private RSA _rsa = null!;
    private RsaSecurityKey _key = null!;
    private FakeTime _clock = null!;

    [TestInitialize]
    public void Initialize()
    {
        _rsa = RSA.Create(2048);
        _key = new(_rsa) { KeyId = "key-1" };
        _clock = new();
    }

    [TestCleanup]
    public void Cleanup() => _rsa.Dispose();

    [TestMethod]
    public async Task StaticKeyValidatesIdentityAndReturnsActualExpirationWithoutHttp()
    {
        using var validator = new JwtAccessTokenValidator(StaticOptions(), new MockHttp(_ => throw new AssertFailedException("Offline keys must not access HTTP.")), _clock);
        var identity = await validator.ValidateAsync(Token());
        Assert.AreEqual("primary", identity.AuthorityId);
        Assert.AreEqual("user-1", identity.Principal.FindFirst("sub")!.Value);
        Assert.IsTrue(identity.Principal.Identity!.IsAuthenticated);
        Assert.AreEqual(_clock.GetUtcNow().AddMinutes(10), identity.ExpiresAt);
    }

    [TestMethod]
    [DataRow("issuer")]
    [DataRow("audience")]
    [DataRow("expired")]
    [DataRow("future")]
    [DataRow("signature")]
    [DataRow("type")]
    [DataRow("algorithm")]
    [DataRow("subject")]
    [DataRow("missing-expiration")]
    [DataRow("none")]
    [DataRow("required-claim")]
    [DataRow("required-value")]
    public async Task InvalidTokensFailWithSanitizedErrors(string failure)
    {
        var authority = Authority();
        if (failure == "required-claim") authority = authority with { RequiredClaims = ["tenant"] };
        if (failure == "required-value") authority = authority with { RequiredClaimValues = new Dictionary<string, string> { ["scope"] = "write" } };
        using var validator = new JwtAccessTokenValidator(new() { Authorities = [authority] }, timeProvider: _clock);
        var claims = Claims();
        string type = "at+jwt", algorithm = SecurityAlgorithms.RsaSha256;
        SecurityKey key = _key;
        using var otherRsa = RSA.Create(2048);
        switch (failure)
        {
            case "issuer": claims["iss"] = "https://untrusted.test"; break;
            case "audience": claims["aud"] = "wrong-audience"; break;
            case "expired": claims["exp"] = _clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds(); break;
            case "future": claims["nbf"] = _clock.GetUtcNow().AddMinutes(2).ToUnixTimeSeconds(); break;
            case "signature": key = new RsaSecurityKey(otherRsa) { KeyId = _key.KeyId }; break;
            case "type": type = "JWT"; break;
            case "algorithm": algorithm = SecurityAlgorithms.RsaSha512; break;
            case "subject": claims.Remove("sub"); break;
            case "missing-expiration": claims.Remove("exp"); break;
        }
        var token = failure == "none"
            ? Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"at+jwt\",\"kid\":\"key-1\"}") + "." + Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims)) + "."
            : Token(claims, key, type, algorithm);
        var exception = await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(token).AsTask());
        Assert.AreEqual("Access token validation failed.", exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(token, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("jku")]
    [DataRow("x5u")]
    [DataRow("jwk")]
    [DataRow("x5c")]
    [DataRow("crit")]
    public async Task TokenSuppliedKeyAndAuthoritySourcesAreNeverFetched(string name)
    {
        var http = new MockHttp(_ => throw new AssertFailedException("No endpoint may be called for prohibited token headers."));
        using var validator = new JwtAccessTokenValidator(NetworkOptions(), http, _clock);
        var token = Token(additionalHeaders: new Dictionary<string, object> { [name] = "https://attacker.test/keys" });
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(token).AsTask());
        Assert.AreEqual(0, http.Calls);
    }

    [TestMethod]
    public async Task DuplicateIssuerAndOversizedTokensAreRejectedBeforeHttp()
    {
        var http = new MockHttp(_ => throw new AssertFailedException("Invalid token must be rejected before HTTP."));
        using var validator = new JwtAccessTokenValidator(NetworkOptions() with { MaximumTokenBytes = 1024 }, http, _clock);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(new string('a', 1025)).AsTask());
        var duplicate = Base64UrlEncoder.Encode("{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"kid\":\"key-1\"}") + "."
            + Base64UrlEncoder.Encode("{\"iss\":\"" + Issuer + "\",\"iss\":\"https://evil.test\",\"sub\":\"u\"}") + ".invalid";
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(duplicate).AsTask());
        Assert.AreEqual(0, http.Calls);
    }

    [TestMethod]
    public async Task ConcurrentFirstRequestsShareOneConfigurationLoad()
    {
        var http = ServingKeys();
        using var validator = new JwtAccessTokenValidator(NetworkOptions(), http, _clock);
        var token = Token();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => validator.ValidateAsync(token).AsTask()));
        Assert.AreEqual(2, http.Calls, "One discovery and one JWKS request serve all callers.");
    }

    [TestMethod]
    public async Task CancellingTheFirstCallerDoesNotPoisonSharedKeyRefresh()
    {
        var http = new GatedHttp(request => Reply(request.RequestUri!, _key));
        using var validator = new JwtAccessTokenValidator(NetworkOptions(), http, _clock);
        using var cancellation = new CancellationTokenSource();
        var first = validator.ValidateAsync(Token(), cancellation.Token).AsTask();
        await http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        http.Release.TrySetResult();
        Assert.IsNotNull(await validator.ValidateAsync(Token()));
        Assert.AreEqual(2, http.Calls, "Cancelled callers must not create refresh storms or suppress the shared load.");
    }

    private sealed class GatedHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var result = response(request);
            result.RequestMessage = request;
            return result;
        }
    }

    [TestMethod]
    public async Task UnknownKeyRefreshIsRateLimitedAndAcceptsConfiguredKeyRollover()
    {
        using var nextRsa = RSA.Create(2048);
        var next = new RsaSecurityKey(nextRsa) { KeyId = "key-2" };
        var current = _key;
        var http = ServingKeys(() => current);
        using var validator = new JwtAccessTokenValidator(NetworkOptions(), http, _clock);
        await validator.ValidateAsync(Token());
        current = next;
        var token = Token(key: next);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(token).AsTask());
        Assert.AreEqual(2, http.Calls);
        _clock.Advance(TimeSpan.FromSeconds(31));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => validator.ValidateAsync(token).AsTask()));
        Assert.AreEqual(4, http.Calls);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(Token()).AsTask());
        Assert.AreEqual(4, http.Calls, "Removed keys do not silently remain trusted and cannot trigger refresh flooding.");
    }

    [TestMethod]
    public async Task FailedRefreshCannotExtendHardConfigurationAge()
    {
        var outage = false;
        var http = new MockHttp(request => outage ? new(HttpStatusCode.ServiceUnavailable) : Reply(request.RequestUri!, _key));
        using var validator = new JwtAccessTokenValidator(NetworkOptions(), http, _clock);
        var token = Token();
        await validator.ValidateAsync(token);
        outage = true;
        _clock.Advance(TimeSpan.FromMinutes(2));
        await validator.ValidateAsync(token);
        Assert.AreEqual(3, http.Calls, "A stale but bounded cached key survives one transient discovery failure.");
        _clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(token).AsTask());
        Assert.AreEqual(4, http.Calls);
        outage = false;
        _clock.Advance(TimeSpan.FromSeconds(31));
        await validator.ValidateAsync(token);
        Assert.AreEqual(6, http.Calls);
    }

    [TestMethod]
    [DataRow("issuer")]
    [DataRow("jwks-uri")]
    [DataRow("redirect")]
    [DataRow("oversize")]
    [DataRow("too-many-keys")]
    [DataRow("duplicate-key-id")]
    public async Task InvalidRemoteConfigurationFailsClosed(string failure)
    {
        var http = new MockHttp(request =>
        {
            if (failure == "redirect") return new(HttpStatusCode.Redirect) { Headers = { Location = new("https://attacker.test") } };
            if (failure == "oversize") return Json(new string('a', 2049));
            if (request.RequestUri == Metadata)
                return Json(JsonSerializer.Serialize(new
                {
                    issuer = failure == "issuer" ? "https://attacker.test" : Issuer,
                    jwks_uri = failure == "jwks-uri" ? "https://attacker.test/keys" : Jwks.AbsoluteUri
                }));
            var key = PublicJwk(_key);
            return failure is "too-many-keys" or "duplicate-key-id"
                ? Json("{\"keys\":[" + key + "," + key + "]}") : Reply(request.RequestUri!, _key);
        });
        var options = NetworkOptions() with { MaximumDocumentBytes = 2048, MaximumSigningKeys = failure == "too-many-keys" ? 1 : 64 };
        using var validator = new JwtAccessTokenValidator(options, http, _clock);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(Token()).AsTask());
        Assert.IsTrue(http.Addresses.All(address => address == Metadata || address == Jwks));
    }

    [TestMethod]
    public async Task MultipleAuthoritiesRouteOnlyToTheirExactConfiguredIssuerAndKeys()
    {
        using var secondRsa = RSA.Create(2048);
        var secondKey = new RsaSecurityKey(secondRsa) { KeyId = "key-2" };
        var second = Authority() with { Id = "second", Issuer = "https://second.test", SigningKeys = [secondKey] };
        using var validator = new JwtAccessTokenValidator(new() { Authorities = [Authority(), second] }, timeProvider: _clock);
        var claims = Claims(); claims["iss"] = second.Issuer;
        Assert.AreEqual("second", (await validator.ValidateAsync(Token(claims, secondKey))).AuthorityId);
        await Assert.ThrowsExactlyAsync<AuthenticationException>(() => validator.ValidateAsync(Token(claims)).AsTask());
        Assert.ThrowsExactly<ArgumentException>(() => new JwtAccessTokenValidator(new() { Authorities = [Authority(), Authority() with { Id = "duplicate-issuer" }] }));
    }

    [TestMethod]
    public void InsecureEndpointsHandlersAndWeakKeysAreRejectedDuringConfiguration()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new JwtAccessTokenValidator(NetworkOptions() with
        { Authorities = [Authority() with { SigningKeys = [], JwksAddress = new("http://authority.test/keys") }] }));
        using var redirects = new HttpClientHandler();
        Assert.ThrowsExactly<ArgumentException>(() => new JwtAccessTokenValidator(NetworkOptions(), redirects));
        using var weak = RSA.Create(1024);
        Assert.ThrowsExactly<ArgumentException>(() => new JwtAccessTokenValidator(new()
        { Authorities = [Authority() with { SigningKeys = [new RsaSecurityKey(weak) { KeyId = "weak" }] }] }));
    }

    private JwtAuthorityOptions Authority() => new()
    {
        Id = "primary", Issuer = Issuer, Audiences = ["storage"], SigningKeys = [_key], TokenTypes = ["at+jwt"]
    };
    private JwtAccessTokenValidatorOptions StaticOptions() => new() { Authorities = [Authority()] };
    private JwtAccessTokenValidatorOptions NetworkOptions() => new()
    {
        Authorities = [Authority() with { SigningKeys = [], JwksAddress = Jwks, MetadataAddress = Metadata }],
        RefreshInterval = TimeSpan.FromMinutes(1), MinimumRefreshInterval = TimeSpan.FromSeconds(30),
        MaximumConfigurationAge = TimeSpan.FromMinutes(5)
    };

    private Dictionary<string, object> Claims() => new()
    {
        ["iss"] = Issuer, ["aud"] = "storage", ["sub"] = "user-1", ["scope"] = "read",
        ["exp"] = _clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(),
        ["nbf"] = _clock.GetUtcNow().AddMinutes(-1).ToUnixTimeSeconds()
    };

    private string Token(Dictionary<string, object>? claims = null, SecurityKey? key = null,
        string type = "at+jwt", string algorithm = SecurityAlgorithms.RsaSha256,
        Dictionary<string, object>? additionalHeaders = null)
        => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims ?? Claims(), SigningCredentials = new(key ?? _key, algorithm), TokenType = type,
            AdditionalHeaderClaims = additionalHeaders
        });

    private MockHttp ServingKeys(Func<RsaSecurityKey>? key = null) => new(request => Reply(request.RequestUri!, key?.Invoke() ?? _key));
    private static HttpResponseMessage Reply(Uri address, RsaSecurityKey key) => address == Metadata
        ? Json(JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = Jwks.AbsoluteUri }))
        : Json("{\"keys\":[" + PublicJwk(key) + "]}");
    private static string PublicJwk(RsaSecurityKey key)
    {
        var parameters = key.Rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new { kty = "RSA", kid = key.KeyId, use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) });
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class MockHttp(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal System.Collections.Concurrent.ConcurrentBag<Uri> Addresses { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls); Addresses.Add(request.RequestUri!);
            var result = response(request); result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private long _seconds;
        private readonly DateTimeOffset _start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(Volatile.Read(ref _seconds));
        public override long GetTimestamp() => Volatile.Read(ref _seconds) * TimestampFrequency;
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _seconds, (long)duration.TotalSeconds);
    }
}
