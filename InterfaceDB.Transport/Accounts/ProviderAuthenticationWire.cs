using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Authentication;

namespace Idb.Transport;

internal static class ProviderAuthenticationWire
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    internal static string Encode(AuthenticationCredentials credentials)
    {
        Validate(credentials);
        var encoded = JsonSerializer.Serialize(credentials, Json);
        if (System.Text.Encoding.UTF8.GetByteCount(encoded) > TransportValidation.MaximumAccessTokenBytes)
            throw new ArgumentException("Credentials exceed the connection limit.");
        return encoded;
    }
    internal static AuthenticationCredentials Decode(string? encoded)
    {
        try
        {
            if (encoded is null || encoded.Length > TransportValidation.MaximumAccessTokenBytes) throw new ArgumentException();
            var credentials = JsonSerializer.Deserialize<AuthenticationCredentials>(encoded, Json) ?? throw new ArgumentException();
            Validate(credentials); return credentials;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new UnauthorizedAccessException("Authentication failed."); }
    }
    private static void Validate(AuthenticationCredentials credentials)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Scheme) || credentials.Scheme.Length > 64
            || string.IsNullOrEmpty(credentials.Secret) || credentials.Secret.Length > 8192)
            throw new ArgumentException("Invalid provider credentials.");
    }
    internal static string? Receipt(TcpStoreAuthenticationMode mode, AuthenticatedIdentity? identity)
        => mode == TcpStoreAuthenticationMode.Provider && identity is not null ? JsonSerializer.Serialize(identity.GetAccountIdentity(), Json) : null;
    internal static AccountIdentity ReadReceipt(string? encoded)
    {
        try
        {
            var identity = encoded is null ? null : JsonSerializer.Deserialize<AccountIdentity>(encoded, Json);
            if (identity is null || string.IsNullOrWhiteSpace(identity.Authority) || string.IsNullOrWhiteSpace(identity.Subject)
                || identity.Authority.Length > 1024 || identity.Subject.Length > 1024) throw new ArgumentException();
            return identity;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid authenticated account receipt."); }
    }
}
