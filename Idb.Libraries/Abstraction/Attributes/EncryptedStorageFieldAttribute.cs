namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Marks an interface property as an encrypted storage field.
/// Access through the InterfaceDB contract proxy decrypts on get and encrypts on set.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class EncryptedStorageFieldAttribute : Attribute
{
    public EncryptedStorageFieldAttribute(string profileName = "")
    {
        ProfileName = profileName;
    }

    public string ProfileName { get; }
}
