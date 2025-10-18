using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Writes serialized envelopes to the backing store.
    /// Complements <see cref="IObjectReader{TContract}"/> and the higher-level <see cref="IEnvelopeRepository{TContract}"/>
    /// by accepting envelopes the reader produced and committing them after indexing decisions are made.
    /// </summary>
    public interface IObjectWriter<TContract>
        where TContract : class, IRepoDto<TContract>
    {
        /// <summary>
        /// Persists the supplied contract instance using the provided envelope metadata.
        /// </summary>
        /// <param name="obj">Concrete contract instance to persist.</param>
        /// <param name="envelope">Envelope containing metadata and payload bytes to store; index coordination can amend it prior to persistence.</param>
        /// <param name="cancellationToken">Token that propagates cancellation.</param>
        /// <returns>Envelope representing the stored payload, including any repository-specific mutations.</returns>
        ValueTask<IObjectEnvelope<TContract>> WriteAsync(
            TContract obj,
            IObjectEnvelope<TContract> envelope,
            CancellationToken cancellationToken = default);
    }
}

