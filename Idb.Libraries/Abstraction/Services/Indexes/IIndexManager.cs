using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Mediates between repositories, indexes, and the persistence layer.
/// Responsible for choosing the appropriate <see cref="IIndex{TContract}"/>, wrapping contracts into envelopes
/// before persistence, and unwrapping them on the way out so callers always work with plain contract instances.
/// </summary>
/// <typeparam name="TContract">Contract interface managed by the index manager.</typeparam>
public interface IIndexManager<TContract>
    where TContract : class
{
    /// <summary>
    /// Persists an entity by wrapping it into an envelope, updating relevant indexes, and delegating to the storage repository.
    /// Implementations must clear the contract's <c>Envelope</c> reference before handing the envelope to the repository to avoid circular references.
    /// </summary>
    ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entity that satisfies the supplied predicate.
    /// Implementations unwrap the envelope, restore the contract's envelope reference, and should clear <c>Envelope.ObjectInstance</c> before returning to prevent retain cycles.
    /// </summary>
    ValueTask<TContract> QueryAsync(Expression<Func<TContract, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an equivalent contract instance already exists.
    /// </summary>
    ValueTask<bool> ExistsAsync(TContract entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one-time initialization for indexes and underlying storage.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
