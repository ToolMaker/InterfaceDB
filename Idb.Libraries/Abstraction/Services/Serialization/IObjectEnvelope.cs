using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Wraps a serialized contract payload together with persistence metadata.
/// Acts as the hand-off artifact between readers, the envelope repository cache, writers, and index coordination
/// so raw bytes can be deferred until the consumer needs the contract instance.
/// </summary>
/// <typeparam name="TContract">Contract interface associated with the payload.</typeparam>
public interface IObjectEnvelope<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Stable cache key derived from storage metadata (e.g., normalized location identifier).
    /// Used by the envelope repository to index instances across process boundaries regardless of physical medium.
    /// </summary>
    string EnvelopeCacheKey { get; set; }

    /// <summary>
    /// Version tag recorded when the payload was persisted.
    /// </summary>
    string VersionTag { get; set; }

    /// <summary>
    /// Contract instance associated with this envelope.
    /// </summary>
    TContract? ObjectInstance { get; set; }

    /// <summary>
    /// Runtime-only weak reference to the contract instance. This is used by cache modes that should reuse
    /// live objects without keeping them alive. It is not part of the serialized envelope payload.
    /// </summary>
    WeakReference<TContract>? ObjectInstanceWeakReference { get; set; }

    /// <summary>
    /// Concrete implementation type stored in this envelope.
    /// Used by polymorphic queries over a contract repository.
    /// </summary>
    string? ImplementationTypeName { get; set; }

    /// <summary>
    /// Overrides the repository object-cache policy for this specific envelope.
    /// When true, the materialized object instance may be retained in the envelope cache.
    /// </summary>
    bool CacheObjectInstance { get; set; }

    /// <summary>
    /// Encrypted per-field values keyed by contract property path.
    /// Values remain encrypted until accessed through the InterfaceDB contract proxy.
    /// </summary>
    IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }

    /// <summary>
    /// Optional opaque application-defined metadata associated with this envelope.
    /// Storage providers persist and copy these bytes without interpreting their contents. Values
    /// persisted through InterfaceDB cannot exceed
    /// <see cref="EnvelopeExtensionMetadataLimits.MaximumByteLength"/> bytes.
    /// </summary>
    byte[]? ExtensionMetadata { get; set; }

    /// <summary>
    /// Location descriptor where the serialized payload resides (e.g., file, blob, queue message).
    /// </summary>
    string? Location { get; set; }

    /// <summary>
    /// Tracks whether this envelope still represents an active storage object.
    /// </summary>
    StorageObjectState State { get; set; }
}
