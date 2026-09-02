using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Emits committed storage changes for envelope payloads.
/// </summary>
public interface IEnvelopeStorageChangeSource<TContract> : IAsyncDisposable
    where TContract : class, IStoredContract<TContract>
{
    event Func<EnvelopeStorageChange, ValueTask>? Changed;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Compatibility hook for acknowledging an exact locally committed revision. A provider must
    /// not suppress changes from this path-only signal unless it can independently prove revision
    /// identity; duplicate delivery is safer than losing another writer's update.
    /// </summary>
    void AcknowledgeLocalChange(string providerLocator);

    /// <summary>
    /// Reconciles the watcher snapshot with the backing store.
    /// </summary>
    ValueTask RescanAsync(CancellationToken cancellationToken = default);
}
