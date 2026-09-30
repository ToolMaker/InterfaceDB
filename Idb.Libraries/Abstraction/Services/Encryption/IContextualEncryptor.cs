namespace Idb.Libraries.Abstractions.Services.Encryption;

/// <summary>
/// Authenticates a caller-supplied storage context together with an encrypted payload.
/// </summary>
public interface IContextualEncryptor : IEncryptor
{
    byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> associatedData);

    byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> associatedData);

    EncryptionDecryptionResult DecryptWithMetadata(
        ReadOnlySpan<byte> cipher,
        ReadOnlySpan<byte> associatedData);
}
