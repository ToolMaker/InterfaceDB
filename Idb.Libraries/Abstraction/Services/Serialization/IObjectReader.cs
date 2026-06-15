using System.Threading;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    /// <summary>
    /// Reads persisted envelopes from storage and materializes contract instances on demand.
    /// Typically invoked by <see cref="IEnvelopeRepository{TContractType}"/> to hydrate envelopes before they are cached.
    /// </summary>
    /// <typeparam name="TContract">Contract interface associated with the reader instance.</typeparam>
    /// <typeparam name="TLocationType">Type describing where the payload lives (file system, blob, etc.).</typeparam>
    public interface IObjectReader<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        Task<TContract> ReadAsync(
            FileInfo file,
            CancellationToken cancellationToken = default);
    }
}
