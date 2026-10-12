using Microsoft.IdentityModel.Tokens;

namespace Idb.Authentication;

public sealed record JwtAuthorityOptions
{
    public required string Id { get; init; }
    public required string Issuer { get; init; }
    public required IReadOnlyList<string> Audiences { get; init; }
    public IReadOnlyList<string> Algorithms { get; init; } = [SecurityAlgorithms.RsaSha256];
    public IReadOnlyList<string> TokenTypes { get; init; } = ["at+jwt"];
    /// <summary>Additional claims that must contain a non-empty value. The sub claim is always required.</summary>
    public IReadOnlyList<string> RequiredClaims { get; init; } = [];
    /// <summary>Optional exact claim values, for example an issuer-specific access-token discriminator.</summary>
    public IReadOnlyDictionary<string, string> RequiredClaimValues { get; init; } = new Dictionary<string, string>();
    /// <summary>Fixed trusted HTTPS key endpoint. Never obtained from a token.</summary>
    public Uri? JwksAddress { get; init; }
    /// <summary>Optional HTTPS discovery endpoint; its issuer and jwks_uri must match these explicit settings.</summary>
    public Uri? MetadataAddress { get; init; }
    /// <summary>For offline deployments, configured asymmetric signing keys instead of HTTP discovery/JWKS.</summary>
    public IReadOnlyList<SecurityKey> SigningKeys { get; init; } = [];
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record JwtAccessTokenValidatorOptions
{
    public required IReadOnlyList<JwtAuthorityOptions> Authorities { get; init; }
    public int MaximumTokenBytes { get; init; } = 16 * 1024;
    public int MaximumDocumentBytes { get; init; } = 256 * 1024;
    public int MaximumSigningKeys { get; init; } = 64;
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan MinimumRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Absolute age since a successful configuration load; failed refreshes never extend it.</summary>
    public TimeSpan MaximumConfigurationAge { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
