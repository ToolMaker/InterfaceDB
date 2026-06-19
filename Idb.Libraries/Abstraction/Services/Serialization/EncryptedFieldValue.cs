namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Encrypted value stored inside an envelope for a field marked with EncryptedStorageFieldAttribute.
/// </summary>
public sealed class EncryptedFieldValue
{
    public string ProfileName { get; set; } = string.Empty;

    public string PropertyTypeName { get; set; } = string.Empty;

    public byte[] Ciphertext { get; set; } = Array.Empty<byte>();
}
