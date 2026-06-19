using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.PubSub;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Composite facade that wraps reader, writer, and serializer behaviours behind a single dependency.
/// Concrete implementations can add concerns like hashing or caching internally without exposing them upstream.
/// </summary>
/// <typeparam name="TContract">Contract interface represented in the repository.</typeparam>
public interface IEnvelopeRepository<TContract> :
    IContractPubSubService<TContract>
        where TContract : class, IStoredContract<TContract>
{
    IReaderAndWriter<TContract> ReaderAndWriter { get; }

    /// <summary>
    /// Checks whether an envelope group with the supplied logical or physical storage key exists.
    /// </summary>
    bool Exists(string storageKey);

    /// <summary>
    /// Attempts to read the cached envelope group for the supplied logical or physical storage key.
    /// Returned envelopes may still be lightweight skeletons until materialized by <see cref="GetAsync"/>.
    /// </summary>
    bool TryGet(string storageKey, out IReadOnlyList<IObjectEnvelope<TContract>> envelopes);

    /// <summary>
    /// Performs one-time initialization for the repository (e.g., ensuring data directories exist).
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams materialized envelopes from the logical storage-key group.
    /// </summary>
    IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Materializes the exact physical envelope for an index result key.
    /// </summary>
    ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams all materialized envelopes known to the repository.
    /// </summary>
    IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams materialized envelopes whose concrete implementation type matches the requested type.
    /// </summary>
    IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract;

    ValueTask<TContract> WriteAsync(
            TContract contractInstance,
            CancellationToken cancellationToken = default);

    async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
            IEnumerable<TContract> contractInstances,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstances);

        var materialized = contractInstances as IReadOnlyList<TContract> ?? contractInstances.ToArray();
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

    ValueTask<bool> DeleteAsync(
            TContract contractInstance,
            CancellationToken cancellationToken = default);
}
