namespace Idb.Libraries.Abstractions.Services.Encryption.Options;

public sealed class AesGcmEncryptorOptions
{
    /// <summary>First byte placed in every payload so we can swap algorithms later.</summary>
    public byte AlgorithmVersion { get; init; } = 0x01;

    /// <summary>Size of the nonce (IV). 12 is the NIST-recommended value for GCM.</summary>
    public int NonceSize { get; init; } = 12;

    /// <summary>
    /// Allows existing plaintext envelope payloads to be read during an encryption rollout.
    /// New writes are always encrypted.
    /// </summary>
    public bool AllowUnencryptedPayloads { get; init; }

    /// <summary>
    /// Identifies the key used by legacy IDBE version 1 payloads, which did not store a key identifier.
    /// </summary>
    public string? LegacyKeyId { get; init; }

    /// <summary>Controls whether reads transparently rewrite payloads that use a historical key.</summary>
    public Idb.Libraries.Abstractions.Enums.EncryptionRotationMode RotationMode { get; init; }
        = Idb.Libraries.Abstractions.Enums.EncryptionRotationMode.ReadHistoricalOnly;
}

