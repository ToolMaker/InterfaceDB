using System.Collections.Generic;
using System.Threading;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Reads persisted envelopes from storage and materializes contract instances on demand.
    /// Typically invoked by <see cref="IEnvelopeRepository{TContract, TLocationType}"/> to hydrate envelopes before they are cached.
    /// </summary>
    /// <typeparam name="TContract">Contract interface associated with the reader instance.</typeparam>
    /// <typeparam name="TLocationType">Type describing where the payload lives (file system, blob, etc.).</typeparam>
    public interface IObjectReader<TContract, TLocationType>
        where TContract : class, IRepoDto<TContract, TLocationType>
    {
        /// <summary>
        /// Streams every persisted envelope discovered under the supplied root location.
        /// Implementations walk provider-specific layouts (folders, buckets, partitions) and hydrate envelopes on demand.
        /// </summary>
        /// <param name="rootLocationOfContract">Root descriptor (e.g., directory, container) that contains all contract versions.</param>
        /// <param name="cancellationToken">Token that propagates cancellation.</param>
        /// <returns>Async sequence of envelopes paired with their storage metadata.</returns>
        IAsyncEnumerable<IObjectEnvelope<TContract, TLocationType>> LoadAsync(
            TLocationType rootLocationOfContract,
            CancellationToken cancellationToken = default);
    }
}
