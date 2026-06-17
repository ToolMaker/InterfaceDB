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
    /// Suppresses watcher echoes caused by a repository operation performed in this process.
    /// </summary>
    void AcknowledgeLocalChange(string providerLocator);

    /// <summary>
    /// Reconciles the watcher snapshot with the backing store.
    /// </summary>
    ValueTask RescanAsync(CancellationToken cancellationToken = default);
}
