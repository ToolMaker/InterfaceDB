using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Presents a preferred and peer repository as one offline-resilient repository.
/// </summary>
public interface IResilientRepository<TContract> : IRepository<TContract>, IAsyncDisposable, IRepositoryChangeNotifications
    where TContract : class, IStoredContract<TContract>
{


    /// <summary>Explicitly accepts remote state or retries a queued edit against the currently observed remote revision.</summary>
    ValueTask ResolveConflictAsync(string storageKey, long expectedLocalRevision,
        RepositoryConflictResolution resolution, CancellationToken cancellationToken = default);

    RepositoryConnection Connection { get; }
    RepositoryReadPreference? EffectiveReadPreference { get; }

    /// <summary>
    /// Records an authenticated peer change hint without waiting for peer I/O.
    /// </summary>
    ValueTask NotifyChangedAsync(
        RepositoryChangeSignal signal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current invalidation and pull checkpoint state.
    /// </summary>
    ValueTask<ResilientSynchronizationState> GetSynchronizationStateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers durable pending mutations and processes dirty peer revisions.
    /// </summary>
    ValueTask<ResilientSynchronizationResult> SynchronizeAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes one logical entity key.
    /// </summary>
    ValueTask<ResilientSynchronizationResult> SynchronizeAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}
