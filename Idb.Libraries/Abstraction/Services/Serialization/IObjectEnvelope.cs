using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Wraps a serialized contract payload together with storage metadata.
    /// Acts as the hand-off artifact between readers, the envelope repository cache, writers, and index coordination
    /// so raw bytes can be deferred until the consumer needs the contract instance.
    /// </summary>
    /// <typeparam name="TContract">Contract interface associated with the payload.</typeparam>
    public interface IObjectEnvelope<TContract>
        where TContract : class, IRepoDto<TContract>
    {
        /// <summary>
        /// Stable cache key derived from storage metadata (e.g., normalized file path + creation timestamp).
        /// Used by the envelope repository to index instances across process boundaries.
        /// </summary>
        string CacheKey { get; }

        /// <summary>
        /// Version tag recorded when the payload was persisted.
        /// </summary>
        string VersionTag { get; }

        /// <summary>
        /// Actual concrete type of the serialized object.
        /// </summary>
        Type ObjectType { get; }

        /// <summary>
        /// File housing the serialized payload.
        /// </summary>
        FileInfo File { get; }

        /// <summary>
        /// Raw bytes that represent the serialized payload (before or after encryption).
        /// Readers populate this when a lookup materializes the envelope; writers consume it when persisting.
        /// </summary>
        byte[] Payload { get; set; }
    }
}

