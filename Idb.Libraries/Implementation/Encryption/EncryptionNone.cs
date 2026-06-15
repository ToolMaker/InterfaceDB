using Idb.Libraries.Abstractions.Services.Encryption;

namespace Idb.Libraries.Implementation.Encryption;

/// <summary>
/// Default encryption strategy that leaves serialized payload bytes unchanged.
/// </summary>
public sealed class EncryptionNone : IEncryptor
{
    public byte[] Encrypt(ReadOnlySpan<byte> plain) => plain.ToArray();

    public byte[] Decrypt(ReadOnlySpan<byte> cipher) => cipher.ToArray();
}
