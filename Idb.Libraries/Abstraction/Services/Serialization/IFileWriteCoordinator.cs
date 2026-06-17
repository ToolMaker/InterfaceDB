namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Publishes complete file replacements and coalesces concurrent proposals using last-write-wins semantics.
/// </summary>
public interface IFileWriteCoordinator
{
    /// <summary>
    /// Prevents new writes from starting and waits for active writers to finish.
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireMigrationScopeAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a proposal, publishes it atomically, and ensures pending proposals for the payload are committed.
    /// </summary>
    /// <returns><c>true</c> when the published proposal is the canonical payload after draining.</returns>
    ValueTask<bool> PublishAndCommitAsync(
        string payloadPath,
        bool requireNewPayload,
        Func<string, CancellationToken, ValueTask> writeProposalAsync,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces a payload only when its committed bytes still match the expected bytes.
    /// </summary>
    ValueTask<bool> ReplaceIfUnchangedAsync(
        string payloadPath,
        ReadOnlyMemory<byte> expectedPayload,
        ReadOnlyMemory<byte> replacementPayload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a delete proposal into the same ordered stream as writes.
    /// </summary>
    /// <returns><c>true</c> when an active payload or pending write was removed.</returns>
    ValueTask<bool> DeleteAndCommitAsync(
        string payloadPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits any complete proposals left behind beneath a storage root.
    /// </summary>
    ValueTask DrainPendingWritesAsync(
        string storageRoot,
        string payloadExtension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits complete proposals for one payload path.
    /// </summary>
    ValueTask DrainPendingWritesAsync(
        string payloadPath,
        CancellationToken cancellationToken = default);
}
