using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.PubSub;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Composite facade that wraps reader, writer, and serializer behaviours behind a single dependency.
/// Concrete implementations can add concerns like hashing or caching internally without exposing them upstream.
/// </summary>
/// <typeparam name="TContract">Contract interface represented in the repository.</typeparam>
/// <typeparam name="TLocationType">Type describing the storage location tracked by the envelopes.</typeparam>
public interface IEnvelopeRepository<TContract, TLocationType> :
    IObjectReader<TContract, TLocationType>,
    IObjectWriter<TContract, TLocationType>,
    IContractPubSubService<TContract>
        where TContract : class, IRepoDto<TContract, TLocationType>
{
    /// <summary>
    /// Serializer used to convert contract instances to and from the persisted binary payloads.
    /// </summary>
    IObjectSerializer<TContract> Serializer { get; }

    /// <summary>
    /// Checks whether an envelope with the supplied cache key already exists in the repository.
    /// </summary>
    bool ContainsEnvelope(string cacheKey);

    /// <summary>
    /// Attempts to retrieve an envelope by cache key from the repository.
    /// </summary>
    bool TryGetEnvelope(string cacheKey, out IObjectEnvelope<TContract, TLocationType>? envelope);

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
}
