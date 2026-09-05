namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Defines the storage-format limit for opaque envelope extension metadata.
/// </summary>
public static class EnvelopeExtensionMetadataLimits
{
    /// <summary>
    /// Maximum number of extension-metadata bytes accepted for one envelope (64 KiB).
    /// </summary>
    public const int MaximumByteLength = 64 * 1024;
}
