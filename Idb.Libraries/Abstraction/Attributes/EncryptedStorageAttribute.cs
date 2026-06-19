namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Marks a storage contract interface as requiring encrypted repository storage.
/// The repository must be configured with a non-default encryption provider.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class EncryptedStorageAttribute : Attribute
{
    public EncryptedStorageAttribute(string profileName = "")
    {
        ProfileName = profileName;
    }

    public string ProfileName { get; }
}
