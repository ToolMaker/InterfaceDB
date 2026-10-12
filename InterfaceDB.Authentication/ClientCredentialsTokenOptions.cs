namespace Idb.Authentication;

public enum ClientSecretAuthenticationMethod { Basic, Post }

/// <summary>Service-account acquisition settings. Supply a secret source, never a literal secret in configuration.</summary>
public sealed record ClientCredentialsTokenOptions
{
    public required string Authority { get; init; }
    public required string ClientId { get; init; }
    public Func<CancellationToken, ValueTask<string>>? ClientSecretProvider { get; init; }
    public string? ClientSecretEnvironmentVariable { get; init; }
    public string? Scope { get; init; }
    public ClientSecretAuthenticationMethod AuthenticationMethod { get; init; } = ClientSecretAuthenticationMethod.Basic;
    /// <summary>Explicit additional HTTPS origins allowed to receive the client secret. Authority origin is always allowed.</summary>
    public IReadOnlyCollection<string> AdditionalTrustedTokenOrigins { get; init; } = [];
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RefreshBeforeExpiry { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DiscoveryCacheDuration { get; init; } = TimeSpan.FromHours(1);
    public int MaximumDocumentBytes { get; init; } = 64 * 1024;
    public int MaximumAccessTokenBytes { get; init; } = 16 * 1024;
    public override string ToString() => "[REDACTED CLIENT CREDENTIALS OPTIONS]";
}

public enum TokenAcquisitionFailure { Metadata, Credentials, Http, TokenResponse, Timeout, Unavailable }

/// <summary>Safe diagnostic classification; token endpoint bodies, credentials and underlying exception messages are omitted.</summary>
public sealed class TokenAcquisitionException : System.Security.Authentication.AuthenticationException
{
    internal TokenAcquisitionException(TokenAcquisitionFailure failure)
        : base($"Access token acquisition failed ({failure}).") => Failure = failure;
    public TokenAcquisitionFailure Failure { get; }
}
