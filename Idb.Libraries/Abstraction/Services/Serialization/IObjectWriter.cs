using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Writes serialized envelopes to the backing store.
    /// Complements <see cref="IObjectReader{TContractType}"/> and the higher-level <see cref="IEnvelopeRepository{TContractType}"/>
    /// by creating new envelopes for persisted objects and committing them after indexing decisions are made.
    /// </summary>
    /// <typeparam name="TContract">Contract interface the writer persists.</typeparam>
    /// <typeparam name="TLocationType">Type describing where the payload will be written (file info, blob, etc.).</typeparam>
    public interface IObjectWriter<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        /// <summary>
        /// Persists the supplied contract instance and produces a fresh envelope describing the stored payload.
        /// </summary>
        /// <param name="envalope">Concrete contract instance to persist.</param>
        /// <param name="cancellationToken">Token that propagates cancellation.</param>
        /// <returns>Envelope representing the stored payload, including any repository-specific metadata.</returns>
        ValueTask<TContract> WriteAsync(
            TContract envalope,
            CancellationToken cancellationToken = default);
    }
}

