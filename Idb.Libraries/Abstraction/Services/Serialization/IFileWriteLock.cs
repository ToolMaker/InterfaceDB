namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Coordinates exclusive writes to a payload path.
/// </summary>
public interface IFileWriteLock
{
    /// <summary>
    /// Acquires an exclusive write lock for the supplied payload path.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireAsync(
        string payloadPath,
        CancellationToken cancellationToken = default);
}
