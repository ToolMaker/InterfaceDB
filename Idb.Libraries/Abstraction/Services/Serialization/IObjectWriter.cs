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

        /// <summary>
        /// Persists multiple contract instances using the writer's bulk path.
        /// </summary>
        async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
            IEnumerable<TContract> envalopes,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(envalopes);

            var materialized = envalopes as IReadOnlyList<TContract> ?? envalopes.ToArray();
            if (materialized.Count == 0)
            {
                return Array.Empty<TContract>();
            }

            var results = new TContract[materialized.Count];
            for (var index = 0; index < materialized.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = await WriteAsync(materialized[index], cancellationToken).ConfigureAwait(false);
            }

            return results;
        }
    }
}

