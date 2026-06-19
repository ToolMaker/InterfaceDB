using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using System.Linq.Expressions;
namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>
/// Provides persistence operations for contract instances backed by a concrete storage medium.
/// Serves as the entry point for callers, delegating query/command flows to the index manager while
/// managing repository-level concerns such as scope discovery and storage policy selection.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by the repository.</typeparam>
public interface IRepository<TContract>
    where TContract : class, IStoredContract<TContract>
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
    /// The repository routes the save through the configured <see cref="IIndexManager{TContract}"/> so indexes
    /// can update before the writer commits the envelope to disk.
    /// </summary>
    /// <param name="entityToSave">Contract instance to store.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>
    /// The canonical entity after persistence. Under coalescing last-write-wins storage, this may be a newer
    /// competing value rather than <paramref name="entityToSave"/>.
    /// </returns>
    ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists multiple entities using the repository's configured bulk-write policy.
    /// </summary>
    /// <returns>The canonical entities returned by the underlying storage layer, in input order.</returns>
    ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entitiesToSave,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the exact persisted object identified by its envelope.
    /// </summary>
    /// <returns><c>true</c> when active storage existed; otherwise <c>false</c>.</returns>
    ValueTask<bool> DeleteAsync(
        TContract entityToDelete,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the repository for an entity that satisfies the predicate and returns the materialized instance.
    /// The repository forwards the predicate to the index manager, which selects the optimal index,
    /// retrieves matching envelopes, and materializes contract instances to hand back.
    /// </summary>
    /// <param name="filterFunction">Expression used to locate the entity.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns>The entity that matches the predicate, or a default value when not found (implementation-specific).</returns>
    ValueTask<TContract> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every entity matching the supplied predicate.
    /// </summary>
    IAsyncEnumerable<TContract> QueryManyAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every entity matching the supplied expression, using search indexes when possible.
    /// </summary>
    IAsyncEnumerable<TContract> SearchManyAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams entities whose logical storage key equals the supplied value.
    /// This is an envelope-cache equality lookup, not a predicate scan.
    /// </summary>
    IAsyncEnumerable<TContract> QueryManyByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first matching entity, or <c>null</c> when no entity matches.
    /// </summary>
    ValueTask<TContract?> QueryFirstAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first matching entity using search indexes when possible, or <c>null</c>.
    /// </summary>
    ValueTask<TContract?> SearchFirstAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first entity whose logical storage key equals the supplied value, or <c>null</c>.
    /// This is an envelope-cache equality lookup, not a predicate scan.
    /// </summary>
    ValueTask<TContract?> QueryFirstByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns exactly one matching entity.
    /// </summary>
    ValueTask<TContract> QuerySingleAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null);

    /// <summary>
    /// Returns one matching entity or <c>null</c>, and rejects multiple matches.
    /// </summary>
    ValueTask<TContract?> QuerySingleOrDefaultAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null);

    /// <summary>
    /// Checks whether an entity with equivalent identity already exists.
    /// Delegates to the index manager so inexpensive index lookups can short-circuit the operation.
    /// </summary>
    /// <param name="entityToCheck">Contract instance to compare.</param>
    /// <param name="cancellationToken">Token that propagates cancellation.</param>
    /// <returns><c>true</c> if the repository already contains the entity.</returns>
    ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default);

}

