namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Declares a string ownership criterion on a stored interface property. The server must
/// register the contract access policy to enforce it; this attribute alone is not authorization.
/// The property receives an exact-match search index automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class StorageAccessClaimAttribute(string claimType) : Attribute
{
    public string ClaimType { get; } = !string.IsNullOrWhiteSpace(claimType)
        ? claimType : throw new ArgumentException("A trusted claim name is required.", nameof(claimType));
}
