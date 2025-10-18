using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using System.Linq.Expressions;

namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>
/// Provides persistence operations for contract instances backed by a concrete storage medium.
/// Serves as the entry point for callers, delegating query/command flows to the index coordinator while
/// managing repository-level concerns such as scope discovery and storage policy selection.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by the repository.</typeparam>
public interface IRepository<TContract>
    where TContract : class
{
    /// <summary>
    /// Identifies the repository implementation type (file system, memory, etc.).
    /// </summary>
    ContractRepositoryType RepositoryType { get; }

    /// <summary>
    /// Performs one-time setup such as directory creation and migration execution.
    /// </summary>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied entity and returns the tracked instance (post-write mutations included).
    /// The repository routes the save through the configured <see cref="IIndexCoordinator{TContract}"/> so indexes
    /// can update before the writer commits the envelope to disk.
    /// </summary>
    /// <param name="entityToSave">Contract instance to store.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>The entity reference managed by the repository after persistence.</returns>
    ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the repository for an entity that satisfies the predicate and returns the materialized instance.
    /// The repository forwards the predicate to the index coordinator, which selects the optimal index,
    /// retrieves matching envelopes, and materializes contract instances to hand back.
    /// </summary>
    /// <param name="predicate">Expression used to locate the entity.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>The entity that matches the predicate, or a default value when not found (implementation-specific).</returns>
    ValueTask<TContract> QueryAsync(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an entity with equivalent identity already exists.
    /// Delegates to the index coordinator so inexpensive index lookups can short-circuit the operation.
    /// </summary>
    /// <param name="entityToCheck">Contract instance to compare.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns><c>true</c> if the repository already contains the entity.</returns>
    ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default);
}

