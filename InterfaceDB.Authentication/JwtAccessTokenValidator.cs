using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Idb.Authentication;

/// <summary>
/// Validates JWT access tokens against an immutable allowlist of authorities. Network configuration
/// comes exclusively from host settings. The optional HTTP handler is owned by this validator and
/// must not follow redirects; custom handlers are trusted integration code.
/// </summary>
public sealed class JwtAccessTokenValidator : IAccessTokenValidator, IDisposable
{
    private sealed record KeySnapshot(SecurityKey[] Keys, long LoadedAt);
    private sealed class Authority(JwtAuthorityOptions options)
    {
        internal JwtAuthorityOptions Options { get; } = options;
        internal SemaphoreSlim RefreshGate { get; } = new(1, 1);
        internal KeySnapshot? Snapshot;
        internal long? LastAttempt;
    }

    private static readonly HashSet<string> SupportedAlgorithms = new(StringComparer.Ordinal)
    {
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
    };
    private readonly JwtAccessTokenValidatorOptions _options;
    private readonly Dictionary<string, Authority> _authorities;
    private readonly JsonWebTokenHandler _tokens;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private int _disposed;

    public JwtAccessTokenValidator(JwtAccessTokenValidatorOptions options, HttpMessageHandler? httpHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _authorities = new(StringComparer.Ordinal);
        var authorityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in options.Authorities)
        {
            var frozen = Freeze(item);
            if (!authorityIds.Add(frozen.Id) || !_authorities.TryAdd(frozen.Issuer, new(frozen)))
                throw new ArgumentException("Authority identifiers and issuers must be unique.", nameof(options));
            if (frozen.SigningKeys.Count > 0)
                _authorities[frozen.Issuer].Snapshot = new(ValidateKeys(frozen.SigningKeys, options.MaximumSigningKeys), _time.GetTimestamp());
        }
        httpHandler ??= new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None };
        VerifyNoRedirects(httpHandler);
        _http = new HttpClient(httpHandler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _tokens = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = options.MaximumTokenBytes };
    }

    public async ValueTask<AuthenticatedIdentity> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > _options.MaximumTokenBytes
                || Encoding.UTF8.GetByteCount(token) > _options.MaximumTokenBytes) throw Rejected();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) throw Rejected();
            using var header = ParseJson(Base64UrlEncoder.DecodeBytes(parts[0]));
            using var payload = ParseJson(Base64UrlEncoder.DecodeBytes(parts[1]));
            var issuer = RequiredString(payload.RootElement, "iss");
            _ = RequiredString(payload.RootElement, "sub");
            if (!_authorities.TryGetValue(issuer, out var authority)) throw Rejected();
            var settings = authority.Options;
            var algorithm = RequiredString(header.RootElement, "alg");
            var type = RequiredString(header.RootElement, "typ");
            var keyId = RequiredString(header.RootElement, "kid");
            if (!settings.Algorithms.Contains(algorithm, StringComparer.Ordinal)
                || !settings.TokenTypes.Contains(type, StringComparer.Ordinal) || keyId.Length > 256)
                throw Rejected();
            foreach (var prohibited in new[] { "jku", "x5u", "jwk", "x5c", "crit" })
                if (header.RootElement.TryGetProperty(prohibited, out _)) throw Rejected();

            var keys = await GetKeysAsync(authority, keyId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _tokens.ValidateTokenAsync(token, new TokenValidationParameters
            {
                AuthenticationType = "Bearer", NameClaimType = "sub", RoleClaimType = "role",
                RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = keys,
                TryAllIssuerSigningKeys = false, ValidateIssuer = true, ValidIssuer = settings.Issuer,
                RequireAudience = true, ValidateAudience = true, ValidAudiences = settings.Audiences,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = settings.ClockSkew,
                ValidAlgorithms = settings.Algorithms, ValidTypes = settings.TokenTypes,
                IncludeTokenOnFailedValidation = false, LogValidationExceptions = false,
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    var now = _time.GetUtcNow().UtcDateTime;
                    return expires is not null && expires.Value > now - settings.ClockSkew
                        && (notBefore is null || (notBefore.Value <= now + settings.ClockSkew && notBefore.Value < expires.Value));
                }
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsValid || result.ClaimsIdentity is null || result.SecurityToken is not JsonWebToken validated)
                throw Rejected();
            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            foreach (var claim in settings.RequiredClaims.Append("sub"))
                if (!principal.FindAll(claim).Any(value => !string.IsNullOrWhiteSpace(value.Value))) throw Rejected();
            foreach (var required in settings.RequiredClaimValues)
                if (!principal.FindAll(required.Key).Any(value => value.Value == required.Value)) throw Rejected();
            var expiration = new DateTimeOffset(DateTime.SpecifyKind(validated.ValidTo, DateTimeKind.Utc));
            // Skew may tolerate a future nbf; it must not grant an already expired storage session.
            if (expiration <= _time.GetUtcNow()) throw Rejected();
            return new(principal, expiration, settings.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // IdentityModel exceptions may contain token data. Neither token nor inner exception escapes.
            throw Rejected();
        }
    }

    private async ValueTask<SecurityKey[]> GetKeysAsync(Authority authority, string keyId, CancellationToken cancellationToken)
    {
        if (authority.Options.SigningKeys.Count > 0)
            return authority.Snapshot!.Keys.Any(key => key.KeyId == keyId) ? authority.Snapshot.Keys : throw Rejected();
        var snapshot = Volatile.Read(ref authority.Snapshot);
        if (Fresh(snapshot) && HasKey(snapshot, keyId)) return snapshot!.Keys;
        await authority.RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = authority.Snapshot;
            if (Fresh(snapshot) && HasKey(snapshot, keyId)) return snapshot!.Keys;
            if (authority.LastAttempt is { } last && _time.GetElapsedTime(last) < _options.MinimumRefreshInterval)
                return Usable(snapshot) && HasKey(snapshot, keyId) ? snapshot!.Keys : throw Rejected();
            authority.LastAttempt = _time.GetTimestamp();
            try
            {
                var keys = await LoadKeysAsync(authority.Options, cancellationToken).ConfigureAwait(false);
                snapshot = new(keys, _time.GetTimestamp());
                Volatile.Write(ref authority.Snapshot, snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                or AuthenticationException or OperationCanceledException or ArgumentException or SecurityTokenException)
            {
                if (!Usable(snapshot) || !HasKey(snapshot, keyId)) throw Rejected();
            }
            return Usable(snapshot) && HasKey(snapshot, keyId) ? snapshot!.Keys : throw Rejected();
        }
        finally { authority.RefreshGate.Release(); }
    }

    private bool Fresh(KeySnapshot? snapshot) => snapshot is not null && _time.GetElapsedTime(snapshot.LoadedAt) < _options.RefreshInterval;
    private bool Usable(KeySnapshot? snapshot) => snapshot is not null && _time.GetElapsedTime(snapshot.LoadedAt) < _options.MaximumConfigurationAge;
    private static bool HasKey(KeySnapshot? snapshot, string keyId) => snapshot?.Keys.Any(key => key.KeyId == keyId) == true;

    private async Task<SecurityKey[]> LoadKeysAsync(JwtAuthorityOptions options, CancellationToken cancellationToken)
    {
        if (options.MetadataAddress is { } metadataAddress)
        {
            using var metadata = ParseJson(await DownloadAsync(metadataAddress, cancellationToken).ConfigureAwait(false));
            if (RequiredString(metadata.RootElement, "issuer") != options.Issuer
                || RequiredString(metadata.RootElement, "jwks_uri") != options.JwksAddress!.AbsoluteUri)
                throw Rejected();
        }
        var bytes = await DownloadAsync(options.JwksAddress!, cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(bytes);
        if (!document.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array
            || keys.GetArrayLength() < 1 || keys.GetArrayLength() > _options.MaximumSigningKeys) throw Rejected();
        var admitted = new List<SecurityKey>();
        foreach (var key in keys.EnumerateArray())
        {
            if (RequiredString(key, "kty") is not ("RSA" or "EC") || key.TryGetProperty("x5u", out _)) throw Rejected();
            if (key.TryGetProperty("d", out _) || key.TryGetProperty("p", out _) || key.TryGetProperty("q", out _)) throw Rejected();
            if (key.TryGetProperty("use", out var use) && use.GetString() != "sig") continue;
            if (key.TryGetProperty("key_ops", out var operations)
                && (operations.ValueKind != JsonValueKind.Array || !operations.EnumerateArray().Any(operation => operation.GetString() == "verify")))
                continue;
            admitted.Add(new JsonWebKey(key.GetRawText()));
        }
        return ValidateKeys(admitted, _options.MaximumSigningKeys);
    }

    private async Task<byte[]> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.HttpTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != address
            || response.Content.Headers.ContentLength > _options.MaximumDocumentBytes) throw Rejected();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > _options.MaximumDocumentBytes - output.Length) throw Rejected();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static SecurityKey[] ValidateKeys(IEnumerable<SecurityKey> source, int maximum)
    {
        var keys = source.Take(maximum + 1).ToArray();
        if (keys.Length == 0 || keys.Length > maximum) throw new ArgumentException("A bounded non-empty signing key set is required.");
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || key.KeyId.Length > 256 || !identifiers.Add(key.KeyId)
                || key is not (RsaSecurityKey or ECDsaSecurityKey or JsonWebKey or X509SecurityKey)
                || key.KeySize < 256 || key.KeySize > 8192 || (key is RsaSecurityKey && key.KeySize < 2048)
                || (key is JsonWebKey jwk && (jwk.Kty is not ("RSA" or "EC") || jwk.Kty == "RSA" && jwk.KeySize < 2048)))
                throw new ArgumentException("Signing keys must be asymmetric, sufficiently sized, and have unique key identifiers.");
            if (key is X509SecurityKey certificateKey)
            {
                using var rsa = certificateKey.Certificate.GetRSAPublicKey();
                using var ec = certificateKey.Certificate.GetECDsaPublicKey();
                if (rsa is not null ? rsa.KeySize < 2048 : ec is null || ec.KeySize < 256)
                    throw new ArgumentException("Certificate signing keys must use sufficiently sized RSA or ECDSA keys.");
            }
        }
        return keys;
    }

    private static JwtAuthorityOptions Freeze(JwtAuthorityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Id) || options.Id.Length > 128
            || !Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Fragment)
            || options.Audiences is null || options.Audiences.Count is < 1 or > 32
            || options.Algorithms is null || options.Algorithms.Count is < 1 or > 16
            || options.TokenTypes is null || options.TokenTypes.Count is < 1 or > 16
            || options.RequiredClaims is null || options.RequiredClaimValues is null || options.SigningKeys is null
            || options.RequiredClaims.Count > 32 || options.RequiredClaimValues.Count > 32
            || options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Invalid trusted authority configuration.");
        foreach (var value in options.Audiences.Concat(options.TokenTypes).Concat(options.RequiredClaims)
            .Concat(options.RequiredClaimValues.SelectMany(pair => new[] { pair.Key, pair.Value })))
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024) throw new ArgumentException("Invalid authority claim or audience.");
        if (options.Algorithms.Any(algorithm => !SupportedAlgorithms.Contains(algorithm)))
            throw new ArgumentException("Only explicitly configured asymmetric signature algorithms are allowed.");
        if (options.SigningKeys.Count == 0) ValidateAddress(options.JwksAddress);
        else if (options.JwksAddress is not null || options.MetadataAddress is not null)
            throw new ArgumentException("Configure either static keys or trusted HTTP key endpoints.");
        if (options.MetadataAddress is not null) ValidateAddress(options.MetadataAddress);
        return options with
        {
            Audiences = options.Audiences.ToArray(), Algorithms = options.Algorithms.ToArray(), TokenTypes = options.TokenTypes.ToArray(),
            RequiredClaims = options.RequiredClaims.ToArray(), RequiredClaimValues = new Dictionary<string, string>(options.RequiredClaimValues, StringComparer.Ordinal),
            SigningKeys = options.SigningKeys.ToArray()
        };
    }

    private static void ValidateOptions(JwtAccessTokenValidatorOptions options)
    {
        if (options.Authorities is null || options.Authorities.Count is < 1 or > 32
            || options.MaximumTokenBytes is < 1024 or > 64 * 1024 || options.MaximumDocumentBytes is < 1024 or > 1024 * 1024
            || options.MaximumSigningKeys is < 1 or > 128 || options.MinimumRefreshInterval < TimeSpan.FromSeconds(1)
            || options.RefreshInterval < options.MinimumRefreshInterval || options.MaximumConfigurationAge < options.RefreshInterval
            || options.MaximumConfigurationAge > TimeSpan.FromDays(1)
            || options.HttpTimeout < TimeSpan.FromMilliseconds(1) || options.HttpTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid JWT validator limits.");
    }

    private static void ValidateAddress(Uri? address)
    {
        if (address is null || !address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Fragment))
            throw new ArgumentException("Trusted discovery and JWKS addresses must use HTTPS without credentials or fragments.");
    }

    private static void VerifyNoRedirects(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is { } inner) handler = inner;
        if (handler is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
            throw new ArgumentException("Authentication HTTP handlers must disable automatic redirects.");
    }

    private static JsonDocument ParseJson(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        try { UniqueProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Rejected();
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }

    private static string RequiredString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw Rejected();

    private static AuthenticationException Rejected() => new("Access token validation failed.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }
}
