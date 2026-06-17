namespace Idb.Libraries.Abstractions.Services.Encryption;

public interface IRotatingEncryptor : IEncryptor
{
    EncryptionDecryptionResult DecryptWithMetadata(ReadOnlySpan<byte> cipher);
}

public sealed record EncryptionDecryptionResult(
    byte[] Plaintext,
    string? KeyId,
    bool RequiresReencryption,
    bool UsesCurrentEncryption);
