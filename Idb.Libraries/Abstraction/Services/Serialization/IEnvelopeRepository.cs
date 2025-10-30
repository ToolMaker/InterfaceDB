using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.PubSub;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Composite facade that wraps reader, writer, and serializer behaviours behind a single dependency.
/// Concrete implementations can add concerns like hashing or caching internally without exposing them upstream.
/// </summary>
/// <typeparam name="TContract">Contract interface represented in the repository.</typeparam>
/// <typeparam name="TLocationType">Type describing the storage location tracked by the envelopes.</typeparam>
public interface IEnvelopeRepository<TContract, TLocationType> :
    IContractPubSubService<TContract, TLocationType>
        where TContract : class, IRepoDto<TContract, TLocationType>
{
    IReaderAndWriter<TContract, TLocationType> ReaderAndWriter { get; }
    /// <summary>
    /// Checks whether an envelope with the supplied cache key already exists in the repository.
    /// </summary>
    bool ContainsEnvelope(string cacheKey);

    /// <summary>
    /// Attempts to retrieve an envelope by cache key from the repository.
    /// </summary>
    bool TryGetEnvelope(string cacheKey, out TContract objectInstance);

    /// <summary>
    /// Performs one-time initialization for the repository (e.g., ensuring data directories exist).
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams envelopes that satisfy the supplied filter.
    /// </summary>
    IAsyncEnumerable<IObjectEnvelope<TContract, TLocationType>> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IObjectEnvelope<TContract, TLocationType>> LoadAsync(
            TLocationType rootLocationOfContract,
            CancellationToken cancellationToken = default);

    ValueTask<TContract> WriteAsync(
            TContract contractInstance,
            CancellationToken cancellationToken = default);
}
