namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Holds cross-instance/process coordination throughout validation and atomic replacement.</summary>
public interface IConditionalFileWriteCoordinator : IFileWriteCoordinator
{
    ValueTask<bool> CommitEnvelopeAsync(string payloadPath, bool delete,
        Func<string?, string, CancellationToken, ValueTask> prepareAsync,
        CancellationToken cancellationToken = default, Action? afterCommit = null);

    ValueTask<bool> CommitEnvelopeAsync(string payloadPath, bool delete,
        Func<string?, string, CancellationToken, ValueTask> prepareAsync,
        CancellationToken cancellationToken, Action? afterCommit, Func<bool>? shouldCommit)
        => shouldCommit is null ? CommitEnvelopeAsync(payloadPath, delete, prepareAsync, cancellationToken, afterCommit)
            : throw new NotSupportedException("This file coordinator does not support no-change decisions.");

    ValueTask InspectCommittedAsync(string payloadPath, Func<string?, CancellationToken, ValueTask> inspect,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This file coordinator does not support retained capture.");
}
