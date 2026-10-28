using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;

namespace Idb.Transport;

/// <summary>Server-owned authorization input. RequestedScope is an untrusted selector, never a grant.</summary>
public sealed record DataAccessContext(AuthenticatedIdentity? Identity, string StoreAlias,
    string? RequestedScope, EncodedStoreCapabilities RequiredCapabilities);

/// <summary>
/// Resolves and authorizes the data boundary for one registration. Return null to deny.
/// Called at admission and before every operation/notification; implementations must be thread-safe.
/// The returned scope must remain unchanged for the lifetime of a session.
/// </summary>
public interface IDataAccessPolicy
{
    DataAccessScope? Authorize(DataAccessContext context);
}

/// <summary>An immutable server-derived partition. Only its opaque identity is sent to clients.</summary>
public sealed record DataAccessScope
{
    public static DataAccessScope Unrestricted { get; } = new("");
    private DataAccessScope(string partition) => Partition = partition;
    internal string Partition { get; }
    public string Identity => Partition.Length == 0 ? "unrestricted-v1" : Partition;

    /// <summary>Authority and scope must come from trusted server policy or validated claims.</summary>
    public static DataAccessScope Partitioned(string authority, string scope)
    {
        Validate(authority); Validate(scope);
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        { writer.Write(authority); writer.Write(scope); }
        return new("idbp1." + Convert.ToHexString(SHA256.HashData(bytes.ToArray())) + ".");
    }

    private static void Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 4096)
            throw new ArgumentException("A nonempty, bounded scope identity is required.");
    }
}

/// <summary>Explicit no-op data policy. Transport authentication and alias authorization still apply.</summary>
public sealed class UnrestrictedDataAccessPolicy : IDataAccessPolicy
{
    public static UnrestrictedDataAccessPolicy Instance { get; } = new();
    public DataAccessScope? Authorize(DataAccessContext context) =>
        context.RequestedScope is null ? DataAccessScope.Unrestricted : null;
}

/// <summary>Private data owned by the validated issuer/subject pair.</summary>
public sealed class SubjectDataAccessPolicy : IDataAccessPolicy
{
    public EncodedStoreCapabilities AllowedCapabilities { get; }
    public SubjectDataAccessPolicy(EncodedStoreCapabilities allowedCapabilities = EncodedStoreCapabilities.All)
        => AllowedCapabilities = allowedCapabilities;

    public DataAccessScope? Authorize(DataAccessContext context)
    {
        if (context.RequestedScope is not null || (context.RequiredCapabilities & ~AllowedCapabilities) != 0
            || !DataAccessClaims.TrySubject(context.Identity, out var issuer, out var subject)) return null;
        return DataAccessScope.Partitioned(issuer, subject);
    }
}

/// <summary>
/// Shared data for one group/account claim value. A configured group pins the registration;
/// otherwise the client must select a membership, or have exactly one membership.
/// Configure Keycloak's access-token mapper to emit this claim. No identity-provider calls are made.
/// </summary>
public sealed class ClaimDataAccessPolicy : IDataAccessPolicy
{
    public string ClaimType { get; }
    public string? Scope { get; }
    public EncodedStoreCapabilities AllowedCapabilities { get; }

    public ClaimDataAccessPolicy(string claimType, string? scope = null,
        EncodedStoreCapabilities allowedCapabilities = EncodedStoreCapabilities.All)
    {
        TransportValidation.ControlString(claimType, false);
        if (scope is not null) TransportValidation.ControlString(scope, false);
        ClaimType = claimType; Scope = scope; AllowedCapabilities = allowedCapabilities;
    }

    public DataAccessScope? Authorize(DataAccessContext context)
    {
        if ((context.RequiredCapabilities & ~AllowedCapabilities) != 0
            || !DataAccessClaims.TrySubject(context.Identity, out var issuer, out _)) return null;
        var memberships = DataAccessClaims.Memberships(context.Identity!, ClaimType);
        if (memberships is null) return null;
        var selected = Scope ?? context.RequestedScope ?? (memberships.Count == 1 ? memberships.Single() : null);
        if (selected is null || (Scope is not null && context.RequestedScope is not null && context.RequestedScope != Scope)
            || !memberships.Contains(selected)) return null;
        // Structured authority keeps groups separate from user partitions and from other claim types/issuers.
        return DataAccessScope.Partitioned(JsonSerializer.Serialize(new[] { "claim-v1", issuer, ClaimType }), selected);
    }
}

/// <summary>Composition adapter for application-specific roles/permissions and scope resolution.</summary>
public sealed class DelegateDataAccessPolicy(Func<DataAccessContext, DataAccessScope?> authorize) : IDataAccessPolicy
{
    private readonly Func<DataAccessContext, DataAccessScope?> _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
    public DataAccessScope? Authorize(DataAccessContext context) => _authorize(context);
}

internal static class DataAccessClaims
{
    internal static HashSet<string>? Memberships(AuthenticatedIdentity identity, string claimType)
    {
        var memberships = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in identity.Principal.FindAll(claimType))
        {
            if (claim.ValueType != "JSON_ARRAY") { memberships.Add(claim.Value); continue; }
            try
            {
                using var array = JsonDocument.Parse(claim.Value);
                if (array.RootElement.ValueKind != JsonValueKind.Array) return null;
                foreach (var value in array.RootElement.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) return null;
                    memberships.Add(value.GetString()!);
                }
            }
            catch (JsonException) { return null; }
        }
        return memberships;
    }

    internal static bool TrySubject(AuthenticatedIdentity? identity, out string issuer, out string subject)
    {
        issuer = subject = "";
        if (identity?.Principal.Identity?.IsAuthenticated != true) return false;
        string? Single(string name)
        {
            var claims = identity.Principal.FindAll(name).Take(2).ToArray();
            return claims.Length == 1 && !string.IsNullOrWhiteSpace(claims[0].Value)
                && Encoding.UTF8.GetByteCount(claims[0].Value) <= 4096 ? claims[0].Value : null;
        }
        var iss = Single("iss"); var sub = Single("sub");
        if (iss is null || sub is null) return false;
        issuer = iss; subject = sub; return true;
    }
}
