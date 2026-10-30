using System.Text.Json;
using System.Text.Json.Serialization;

namespace Idb.Authentication;

/// <summary>Supplies a password only to a TLS-authenticated host. Does not persist it.</summary>
public sealed class UsernamePasswordCredentialsProvider(string username, Func<CancellationToken, ValueTask<string>> passwordProvider) : IClientCredentialsProvider
{
    public async ValueTask<AuthenticationCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var password = await passwordProvider(cancellationToken).ConfigureAwait(false);
        return new("password", JsonSerializer.Serialize(new PasswordLogin(username, password)));
    }
    public override string ToString() => "[REDACTED PASSWORD PROVIDER]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PasswordLogin(string Username, string Password)
{
    public override string ToString() => "[REDACTED PASSWORD LOGIN]";
}
