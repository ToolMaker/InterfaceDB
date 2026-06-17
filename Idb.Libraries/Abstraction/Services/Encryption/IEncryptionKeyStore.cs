namespace Idb.Libraries.Abstractions.Services.Encryption;

/// <summary>
/// A mutable multi-key store used to provision and rotate encryption keys.
/// </summary>
public interface IEncryptionKeyStore : IRotatingKeyProvider
{
    void AddOrUpdateKey(string keyId, ReadOnlySpan<byte> key);

    void SetActiveKey(string keyId);

    bool RemoveKey(string keyId);
}
