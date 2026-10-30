using System.Security.Claims;

namespace Idb.Authentication;

/// <summary>A stable provider authority and account ID. Neither value is a credential.</summary>
public sealed record AccountIdentity(string Authority, string Subject);

/// <summary>Opaque provider credentials. Never log or persist Secret.</summary>
public sealed class AuthenticationCredentials(string scheme, string secret)
{
    public string Scheme { get; } = scheme;
    public string Secret { get; } = secret;
    public override string ToString() => "[REDACTED AUTHENTICATION CREDENTIALS]";
}

public interface IClientCredentialsProvider
{
    ValueTask<AuthenticationCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Server-owned authentication. A provider must revalidate account state for active sessions.</summary>
public interface IAuthenticationProvider
{
    bool RequiresDataIsolation { get; }
    ValueTask ValidateConfigurationAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask<AuthenticatedIdentity> AuthenticateAsync(AuthenticationCredentials credentials, CancellationToken cancellationToken = default);
    ValueTask<bool> IsSessionValidAsync(AuthenticatedIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>Adapts existing JWT validation to the provider-independent boundary.</summary>
public sealed class JwtAuthenticationProvider(IAccessTokenValidator validator, TimeProvider? timeProvider = null) : IAuthenticationProvider
{
    private readonly IAccessTokenValidator _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    public bool RequiresDataIsolation => false;
    public ValueTask<AuthenticatedIdentity> AuthenticateAsync(AuthenticationCredentials credentials, CancellationToken cancellationToken = default)
        => credentials.Scheme == "jwt" ? _validator.ValidateAsync(credentials.Secret, cancellationToken)
            : ValueTask.FromException<AuthenticatedIdentity>(new UnauthorizedAccessException("Authentication failed."));
    public ValueTask<bool> IsSessionValidAsync(AuthenticatedIdentity identity, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(identity.Principal.Identity?.IsAuthenticated == true && identity.ExpiresAt > _clock.GetUtcNow());
}

public static class AuthenticatedIdentityExtensions
{
    public static AccountIdentity GetAccountIdentity(this AuthenticatedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Principal.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("Authentication required.");
        string One(string type)
        {
            var values = identity.Principal.FindAll(type).Take(2).ToArray();
            return values.Length == 1 && !string.IsNullOrWhiteSpace(values[0].Value) && values[0].Value.Length <= 1024
                ? values[0].Value : throw new UnauthorizedAccessException("Invalid account identity.");
        }
        return new(One("iss"), One("sub"));
    }
}
