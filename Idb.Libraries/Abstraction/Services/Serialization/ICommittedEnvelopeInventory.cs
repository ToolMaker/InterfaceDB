using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Fresh physical identities independent of the application catalog. The caller must
/// hold provider exclusion for a consistent inventory. Does not hydrate, rotate keys or publish events.</summary>
public interface ICommittedEnvelopeInventory<T> where T : class, IStoredContract<T>
{
    IAsyncEnumerable<IObjectEnvelope<T>> EnumerateCommittedIdentitiesAsync(CancellationToken cancellationToken = default);
}
