using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Mediates between repositories, indexes, and the persistence layer.
/// Responsible for choosing the appropriate <see cref="IIndex{TContract}"/>, wrapping contracts into envelopes
/// before persistence, and unwrapping them on the way out so callers always work with plain contract instances.
/// </summary>
/// <typeparam name="TContract">Contract interface managed by the index manager.</typeparam>
public interface IIndexManager<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Persists an entity by wrapping it into an envelope, updating relevant indexes, and delegating to the storage repository.
    /// Implementations must clear the contract's <c>Envelope</c> reference before handing the envelope to the repository to avoid circular references.
    /// </summary>
    ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists multiple entities using the implementation's bulk persistence path.
    /// </summary>
    async ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var materialized = entities as IReadOnlyList<TContract> ?? entities.ToArray();
        if (materialized.Count == 0)
        {
            return Array.Empty<TContract>();
        }

        var results = new TContract[materialized.Count];
        for (var index = 0; index < materialized.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[index] = await SaveAsync(materialized[index], cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Deletes the persisted object identified by the entity's envelope.
    /// </summary>
    ValueTask<bool> DeleteAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entity that satisfies the supplied predicate.
    /// Implementations unwrap the envelope, restore the contract's envelope reference, and should clear <c>Envelope.ObjectInstance</c> before returning to prevent retain cycles.
    /// </summary>
    IAsyncEnumerable<TContract> QueryAsync(Func<TContract, bool> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves entities of the requested implementation/interface type that satisfy the supplied predicate.
    /// </summary>
    IAsyncEnumerable<TImplementation> QueryAsync<TImplementation>(
        Func<TImplementation, bool> predicate,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract;

    /// <summary>
    /// Retrieves entities matching an inspectable expression, using search indexes when possible.
    /// </summary>
    IAsyncEnumerable<TContract> SearchAsync(Expression<Func<TContract, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves entities of the requested implementation/interface type matching an inspectable expression.
    /// </summary>
    IAsyncEnumerable<TImplementation> SearchAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract;

    /// <summary>
    /// Explains the candidate-retrieval plan for an inspectable search expression without loading results.
    /// </summary>
    SearchQueryPlanExplanation ExplainSearch(Expression<Func<TContract, bool>> predicate);

    /// <summary>
    /// Retrieves contracts by logical storage identity through the envelope cache.
    /// This path is equality-only and should not evaluate range predicates.
    /// </summary>
    IAsyncEnumerable<TContract> QueryByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an equivalent contract instance already exists.
    /// </summary>
    ValueTask<bool> ExistsAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one-time initialization for indexes and underlying storage.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
