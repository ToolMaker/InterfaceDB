namespace Idb.Libraries.Abstraction.Encryption.Options;

public sealed class AesGcmEncryptorOptions
{
    /// <summary>First byte placed in every payload so we can swap algorithms later.</summary>
    public byte AlgorithmVersion { get; init; } = 0x01;

    /// <summary>Size of the nonce (IV). 12 is the NIST-recommended value for GCM.</summary>
    public int NonceSize { get; init; } = 12;

    /// <summary>Add future flags here, e.g., compression or encoding scheme.</summary>
    // public bool UseCompression { get; init; }
}
