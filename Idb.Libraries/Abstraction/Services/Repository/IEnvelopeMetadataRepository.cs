using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>
/// Adds an atomic save operation for callers that need to persist opaque envelope metadata
/// with a contract payload while retaining the standard repository surface.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public interface IEnvelopeMetadataRepository<TContract> : IRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>Conditionally saves a detached payload. Infrastructure import and acknowledgement remain subject to destination matching.</summary>
    ValueTask<TContract> SaveWithExtensionMetadataAsync(TContract detachedEntity, string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata, EnvelopeWriteCondition condition, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider does not support conditional envelope metadata writes.");

    /// <summary>
    /// Gets the durability guarantee in effect when a completed metadata-aware save returns.
    /// </summary>
    StorageDurabilityMode CommitDurability { get; }

    /// <summary>
    /// Saves a detached contract payload and its optional opaque envelope metadata in one provider write.
    /// </summary>
    /// <param name="detachedEntity">Contract instance to save. It must not already have an envelope.</param>
    /// <param name="existingEnvelopeCacheKey">
    /// Must be <see langword="null"/> for an insert. Existing-key updates require the overload with an explicit condition.
    /// </param>
    /// <param name="extensionMetadata">
    /// Opaque bytes to persist without interpretation. The value cannot exceed
    /// <see cref="EnvelopeExtensionMetadataLimits.MaximumByteLength"/> bytes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the save operation.</param>
    /// <remarks>
    /// Validation failures that occur before staging leave <paramref name="detachedEntity"/> detached.
    /// Once staging begins, the candidate is consumed and must not be reused, even when persistence
    /// fails; construct a new detached candidate for a retry.
    /// </remarks>
    ValueTask<TContract> SaveWithExtensionMetadataAsync(
        TContract detachedEntity,
        string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata,
        CancellationToken cancellationToken = default);
}
