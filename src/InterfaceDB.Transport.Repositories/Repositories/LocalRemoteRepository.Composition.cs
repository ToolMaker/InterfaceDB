using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Transport.Repositories;

public sealed partial class LocalRemoteRepository<TContract> : IResilientRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public RepositoryReadPreference? EffectiveReadPreference =>
        (_lease?.Repository as IResilientRepository<TContract>)?.EffectiveReadPreference;
    private static IResilientRepository<TContract> Composition(IRepository<TContract> repository) =>
        repository as IResilientRepository<TContract> ?? throw new NotSupportedException("This version plan has no local/remote composition.");

    public ValueTask<ResilientSynchronizationResult> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        WithRepositoryAsync(repository => Composition(repository).SynchronizeAsync(cancellationToken), cancellationToken);
    public ValueTask<ResilientSynchronizationResult> SynchronizeAsync(string storageKey, CancellationToken cancellationToken = default) =>
        WithRepositoryAsync(repository => Composition(repository).SynchronizeAsync(storageKey, cancellationToken), cancellationToken);
    public ValueTask<ResilientSynchronizationState> GetSynchronizationStateAsync(CancellationToken cancellationToken = default) =>
        WithRepositoryAsync(repository => Composition(repository).GetSynchronizationStateAsync(cancellationToken), cancellationToken);
    public async ValueTask NotifyChangedAsync(RepositoryChangeSignal signal, CancellationToken cancellationToken = default) =>
        _ = await WithRepositoryAsync(async repository =>
        { await Composition(repository).NotifyChangedAsync(signal, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
    public async ValueTask ResolveConflictAsync(string storageKey, long expectedLocalRevision,
        RepositoryConflictResolution resolution, CancellationToken cancellationToken = default) =>
        _ = await WithWritableRepositoryAsync(async repository =>
        { await Composition(repository).ResolveConflictAsync(storageKey, expectedLocalRevision, resolution, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
}
