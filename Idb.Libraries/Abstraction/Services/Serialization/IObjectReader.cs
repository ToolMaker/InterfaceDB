using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Reads persisted envelopes from storage and materializes contract instances on demand.
    /// Typically invoked by <see cref="IEnvelopeRepository{TContract}"/> to hydrate envelopes before they are cached.
    /// </summary>
    /// <typeparam name="TContract">Contract interface associated with the reader instance.</typeparam>
    public interface IObjectReader<TContract>
        where TContract : class, IRepoDto<TContract>
    {
        /// <summary>
        /// Loads a stored object and returns the hydrated envelope representation.
        /// Index coordinators use the envelope to update indexes first, delaying the actual object materialization
        /// until the contract instance is explicitly requested.
        /// </summary>
        /// <param name="cancellationToken">Token that propagates cancellation.</param>
        /// <returns>Envelope containing the serialized payload and metadata for the retrieved contract instance.</returns>
        ValueTask<IObjectEnvelope<TContract>> ReadAsync(
            CancellationToken cancellationToken = default);
    }
}
