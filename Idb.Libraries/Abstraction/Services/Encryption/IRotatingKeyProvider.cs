namespace Idb.Libraries.Abstractions.Services.Encryption;

/// <summary>
/// Resolves an active encryption key and historical keys by stable identifier.
/// </summary>
public interface IRotatingKeyProvider : IKeyProvider
{
    string GetActiveKeyId();

    ReadOnlyMemory<byte> GetKey(string keyId);
}
