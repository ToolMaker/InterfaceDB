using System.Security.Claims;

namespace Idb.Authentication;

/// <summary>A bearer credential. Never log Value or serialize this object into diagnostics.</summary>
public sealed class AccessToken(string value, DateTimeOffset? expiresAt = null)
{
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
    public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    public override string ToString() => "[REDACTED ACCESS TOKEN]";
}

public interface IAccessTokenProvider
{
    ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public interface IAccessTokenValidator
{
    ValueTask<AuthenticatedIdentity> ValidateAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Signature-validated identity. Host authorization must still approve the requested store.</summary>
public sealed record AuthenticatedIdentity(ClaimsPrincipal Principal, DateTimeOffset ExpiresAt, string AuthorityId);
