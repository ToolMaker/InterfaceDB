namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Provider-neutral lease abstraction for protecting storage writes.
/// </summary>
public interface IStorageWriteLock
{
    /// <summary>
    /// Acquires an exclusive write lock for the supplied provider locator.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireAsync(
        string providerLocator,
        CancellationToken cancellationToken = default);
}
