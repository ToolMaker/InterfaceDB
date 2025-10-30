namespace Idb.Libraries.Abstractions.Services
{
    using Idb.Libraries.Abstractions.Dtos;
    using Idb.Libraries.Abstractions.Services.Serialization;
    using System.IO;
    /// <summary>
    /// Produces unique storage identifiers for contract instances and initialized the location property
    /// </summary>
    /// <typeparam name="TContract">Contract type associated with the value.</typeparam>
    public interface IEnvelopeKeyAndLocationFactory<TContract, TLocation>
        where TContract : class, IRepoDto<TContract, TLocation>
    {
        /// <summary>
        /// Sets the FileName and EnvelopeCacheKey properties on the envelope based on the contract instance.
        /// </summary>
        /// <param name="envelope">Contract instance being persisted.</param>
        void InitEnvelope(IObjectEnvelope<TContract, TLocation> envelope);
    }
}

