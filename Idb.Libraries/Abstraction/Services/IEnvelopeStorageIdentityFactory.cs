namespace Idb.Libraries.Abstractions.Services
{
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Enums;
    using Idb.Libraries.Abstractions.Services.Serialization;

    /// <summary>
    /// Resolves the storage identity and physical location for an envelope.
    /// </summary>
    /// <typeparam name="TContract">Contract type associated with the value.</typeparam>
    public interface IEnvelopeStorageIdentityFactory<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        /// <summary>
        /// Gets the object cardinality used when resolving new storage identities.
        /// </summary>
        StorageObjectCardinality ObjectCardinality { get; }

        /// <summary>
        /// Sets the FileName and EnvelopeCacheKey properties on the envelope based on the contract instance.
        /// </summary>
        /// <param name="envelope">Contract instance being persisted.</param>
        void InitEnvelope(IObjectEnvelope<TContract> envelope);
    }
}
