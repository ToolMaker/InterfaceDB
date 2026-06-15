namespace Idb.Libraries.Abstractions.Services
{
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Enums;
    using Idb.Libraries.Abstractions.Services.Serialization;
    using System.IO;
    /// <summary>
    /// Produces unique storage identifiers for contract instances and initialized the location property
    /// </summary>
    /// <typeparam name="TContract">Contract type associated with the value.</typeparam>
    public interface IEnvelopeKeyAndLocationFactory<TContract>
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

