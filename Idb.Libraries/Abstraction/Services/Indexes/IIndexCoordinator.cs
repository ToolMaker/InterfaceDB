using System.Linq.Expressions;

namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Mediates between repositories, indexes, and the persistence layer.
/// Evaluates incoming LINQ expressions, selects the optimal <see cref="IIndex{TContract}"/> to service them,
/// and orchestrates envelope hydration via <see cref="Serialization.IEnvelopeRepository{TContract}"/> before returning materialized contract instances.
/// </summary>
public interface IIndexCoordinator<TContract>
    where TContract : class
{
    /// <summary>
    /// Persists an entity, staging it through an envelope so indexes can update before the writer commits to storage.
    /// </summary>
    /// <param name="entity">Contract instance to store.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>The contract instance after persistence.</returns>
    ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entity that satisfies the supplied predicate, leveraging available indexes.
    /// The coordinator first resolves the best index for the predicate, gathers matching envelopes from storage,
    /// and then materializes concrete contract instances for the caller.
    /// </summary>
    /// <param name="predicate">Expression used to filter contract instances.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>The entity that matches the predicate.</returns>
    ValueTask<TContract> QueryAsync(
        Expression<Func<TContract, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a semantically equivalent contract instance already exists by consulting indexes first,
    /// only touching storage when an index hint suggests a possible match.
    /// </summary>
    /// <param name="objToCheck">Entity candidate to verify against stored indexes.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns><c>true</c> if the entity is already present.</returns>
    ValueTask<bool> ExistsAsync(TContract objToCheck, CancellationToken cancellationToken = default);
        
    /// <summary>
    /// Initializes the coordinator, provisioning repository state and indexes.
    /// </summary>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
