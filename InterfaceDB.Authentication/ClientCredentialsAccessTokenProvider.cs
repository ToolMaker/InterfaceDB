using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Idb.Authentication;

/// <summary>
/// Obtains service-account tokens using OIDC discovery and OAuth client credentials. Tokens are
/// held only in memory; refreshes are serialized and never expose provider response bodies.
/// </summary>
public sealed class ClientCredentialsAccessTokenProvider : IAccessTokenProvider, IDisposable
{
    private readonly ClientCredentialsTokenOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _trustedOrigins;
    private readonly Uri _discoveryUri;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CacheEntry? _cached;
    private Uri? _tokenEndpoint;
    private DateTimeOffset _discoveryExpiresAt;
    private int _disposed;

    /// <summary>
    /// Owns the supplied handler. Custom HTTP handlers are a trusted integration boundary and
    /// must preserve TLS validation and prohibit redirects; normal redirect-enabled handlers are rejected.
    /// </summary>
    public ClientCredentialsAccessTokenProvider(ClientCredentialsTokenOptions options,
        HttpMessageHandler? httpHandler = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var authority = ValidateHttpsUri(options.Authority, originOnly: false, allowQuery: false);
        if (string.IsNullOrWhiteSpace(options.ClientId) || Encoding.UTF8.GetByteCount(options.ClientId) > 1024
            || options.ClientId.Any(char.IsControl)
            || (options.ClientSecretProvider is null) == (options.ClientSecretEnvironmentVariable is null)
            || !Enum.IsDefined(options.AuthenticationMethod)
            || options.RequestTimeout < TimeSpan.FromMilliseconds(1) || options.RequestTimeout > TimeSpan.FromMinutes(5)
            || options.RefreshBeforeExpiry < TimeSpan.Zero || options.RefreshBeforeExpiry > TimeSpan.FromMinutes(10)
            || options.DiscoveryCacheDuration < TimeSpan.FromSeconds(1) || options.DiscoveryCacheDuration > TimeSpan.FromDays(1)
            || options.MaximumDocumentBytes is < 1024 or > 1024 * 1024
            || options.MaximumAccessTokenBytes is < 1 or > 64 * 1024
            || options.MaximumAccessTokenBytes > options.MaximumDocumentBytes)
            throw new ArgumentException("Invalid client credentials configuration.", nameof(options));
        if (options.ClientSecretEnvironmentVariable is { } environment
            && (string.IsNullOrWhiteSpace(environment) || environment.Length > 256 || environment.Contains('=') || environment.Any(char.IsControl)))
            throw new ArgumentException("Invalid client secret environment variable name.", nameof(options));
        if (options.Scope is { } scope && (string.IsNullOrWhiteSpace(scope) || scope.Length > 2048
            || scope.Any(c => c != ' ' && (c < 0x21 || c == 0x22 || c == 0x5c || c > 0x7e))))
            throw new ArgumentException("Invalid OAuth scope.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.AdditionalTrustedTokenOrigins);
        if (options.AdditionalTrustedTokenOrigins.Count > 16)
            throw new ArgumentException("Too many trusted token origins.", nameof(options));
        _trustedOrigins = new(StringComparer.OrdinalIgnoreCase) { Origin(authority) };
        foreach (var origin in options.AdditionalTrustedTokenOrigins)
            _trustedOrigins.Add(Origin(ValidateHttpsUri(origin, originOnly: true, allowQuery: false)));
        _options = options with { AdditionalTrustedTokenOrigins = options.AdditionalTrustedTokenOrigins.ToArray() };
        _discoveryUri = new(options.Authority.TrimEnd('/') + "/.well-known/openid-configuration");
        _time = timeProvider ?? TimeProvider.System;
        httpHandler ??= new SocketsHttpHandler { AllowAutoRedirect = false };
        EnsureNoAutomaticRedirects(httpHandler);
        _http = new HttpClient(httpHandler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (UsableCache() is { } cached) return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(_options.RequestTimeout);
        var entered = false;
        try
        {
            await _refreshGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (UsableCache() is { } refreshed) return refreshed;
            var endpoint = await GetTokenEndpointAsync(deadline.Token).ConfigureAwait(false);
            var secret = await GetSecretAsync(deadline.Token).ConfigureAwait(false);
            var issuedNotBefore = _time.GetUtcNow();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var fields = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
            if (_options.Scope is not null) fields.Add(new("scope", _options.Scope));
            if (_options.AuthenticationMethod == ClientSecretAuthenticationMethod.Basic)
            {
                var credentials = WebUtility.UrlEncode(_options.ClientId) + ":" + WebUtility.UrlEncode(secret);
                request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(credentials)));
            }
            else
            {
                fields.Add(new("client_id", _options.ClientId));
                fields.Add(new("client_secret", secret));
            }
            request.Content = new FormUrlEncodedContent(fields);
            using var document = await ReadDocumentAsync(request, deadline.Token).ConfigureAwait(false);
            var root = document.RootElement;
            var value = RequiredString(root, "access_token", TokenAcquisitionFailure.TokenResponse);
            TokenValueValidation.Validate(value, _options.MaximumAccessTokenBytes);
            if (!string.Equals(RequiredString(root, "token_type", TokenAcquisitionFailure.TokenResponse), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var seconds)
                || seconds is < 1 or > 31 * 24 * 60 * 60)
                throw new TokenAcquisitionException(TokenAcquisitionFailure.TokenResponse);
            var expiresAt = issuedNotBefore.AddSeconds(seconds);
            if (expiresAt <= _time.GetUtcNow()) throw new TokenAcquisitionException(TokenAcquisitionFailure.TokenResponse);
            var token = new AccessToken(value, expiresAt);
            // Short-lived provider tokens still receive a usable cache interval without approaching expiry.
            var safety = TimeSpan.FromTicks(Math.Min(_options.RefreshBeforeExpiry.Ticks, TimeSpan.FromSeconds(seconds).Ticks / 2));
            Volatile.Write(ref _cached, new CacheEntry(token, expiresAt - safety));
            return token;
        }
        catch (TokenAcquisitionException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Access token acquisition was cancelled.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TokenAcquisitionException(Volatile.Read(ref _disposed) != 0
                ? TokenAcquisitionFailure.Unavailable : TokenAcquisitionFailure.Timeout);
        }
        catch (Exception)
        {
            // Custom secret sources, HTTP pipelines and JSON errors can contain credentials in their messages.
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Unavailable);
        }
        finally { if (entered) _refreshGate.Release(); }
    }

    private AccessToken? UsableCache()
    {
        var cache = Volatile.Read(ref _cached);
        return cache is not null && _time.GetUtcNow() < cache.RefreshAt ? cache.Token : null;
    }

    private async ValueTask<Uri> GetTokenEndpointAsync(CancellationToken token)
    {
        if (_tokenEndpoint is not null && _time.GetUtcNow() < _discoveryExpiresAt) return _tokenEndpoint;
        using var request = new HttpRequestMessage(HttpMethod.Get, _discoveryUri);
        using var document = await ReadDocumentAsync(request, token).ConfigureAwait(false);
        var root = document.RootElement;
        if (!string.Equals(RequiredString(root, "issuer", TokenAcquisitionFailure.Metadata), _options.Authority, StringComparison.Ordinal))
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata);
        Uri endpoint;
        try { endpoint = ValidateHttpsUri(RequiredString(root, "token_endpoint", TokenAcquisitionFailure.Metadata), false, true); }
        catch (ArgumentException) { throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata); }
        if (!_trustedOrigins.Contains(Origin(endpoint))) throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata);
        var method = _options.AuthenticationMethod == ClientSecretAuthenticationMethod.Basic ? "client_secret_basic" : "client_secret_post";
        if (!Supports(root, "token_endpoint_auth_methods_supported", method, _options.AuthenticationMethod == ClientSecretAuthenticationMethod.Basic)
            || !Supports(root, "grant_types_supported", "client_credentials", absentDefault: true))
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata);
        _tokenEndpoint = endpoint;
        _discoveryExpiresAt = _time.GetUtcNow() + _options.DiscoveryCacheDuration;
        return endpoint;
    }

    private async ValueTask<string> GetSecretAsync(CancellationToken token)
    {
        string? secret;
        try
        {
            secret = _options.ClientSecretProvider is { } provider
                ? await provider(token).AsTask().WaitAsync(token).ConfigureAwait(false)
                : Environment.GetEnvironmentVariable(_options.ClientSecretEnvironmentVariable!);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new TokenAcquisitionException(TokenAcquisitionFailure.Credentials); }
        if (string.IsNullOrEmpty(secret) || Encoding.UTF8.GetByteCount(secret) > 16 * 1024)
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Credentials);
        return secret;
    }

    private async ValueTask<JsonDocument> ReadDocumentAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is { } final && final != request.RequestUri
            || response.Content.Headers.ContentLength > _options.MaximumDocumentBytes)
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Http);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var remaining = _options.MaximumDocumentBytes - checked((int)body.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > remaining) throw new TokenAcquisitionException(TokenAcquisitionFailure.Http);
            body.Write(buffer, 0, read);
        }
        try
        {
            var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new JsonException(); }
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!fields.Add(property.Name)) { document.Dispose(); throw new JsonException(); }
            return document;
        }
        catch (JsonException) { throw new TokenAcquisitionException(TokenAcquisitionFailure.Http); }
    }

    private static bool Supports(JsonElement root, string name, string expected, bool absentDefault)
    {
        if (!root.TryGetProperty(name, out var values)) return absentDefault;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 32)
            throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata);
        var supported = false;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 128)
                throw new TokenAcquisitionException(TokenAcquisitionFailure.Metadata);
            supported |= value.GetString() == expected;
        }
        return supported;
    }

    private static string RequiredString(JsonElement root, string name, TokenAcquisitionFailure failure)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new TokenAcquisitionException(failure);

    private static Uri ValidateHttpsUri(string? value, bool originOnly, bool allowQuery)
    {
        if (value is null || value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || (!allowQuery && uri.Query.Length != 0) || (originOnly && uri.AbsolutePath != "/"))
            throw new ArgumentException("A trusted absolute HTTPS URL is required.");
        return uri;
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    private static void EnsureNoAutomaticRedirects(HttpMessageHandler handler)
    {
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
            if (current is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("Token HTTP handlers must disable automatic redirects.", nameof(handler));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _http.Dispose();
        Volatile.Write(ref _cached, null);
        // The gate and cancellation source remain valid for calls completing concurrently with disposal.
    }

    public override string ToString() => "[CLIENT CREDENTIALS ACCESS TOKEN PROVIDER]";
    private sealed record CacheEntry(AccessToken Token, DateTimeOffset RefreshAt);
}
