using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Reports partial or complete failure of a non-transactional bulk storage operation.
/// Successful writes remain committed unless a higher-level transaction feature is introduced.
/// </summary>
public sealed class StorageBulkOperationException : StorageException
{
    public StorageBulkOperationException(
        StorageOperation operation,
        IReadOnlyList<int> succeededIndexes,
        IReadOnlyList<StorageBulkOperationFailure> failures)
        : base(CreateMessage(operation, succeededIndexes, failures))
    {
        Operation = operation;
        SucceededIndexes = succeededIndexes;
        Failures = failures;
    }

    public StorageOperation Operation { get; }
    public IReadOnlyList<int> SucceededIndexes { get; }
    public IReadOnlyList<StorageBulkOperationFailure> Failures { get; }

    private static string CreateMessage(
        StorageOperation operation,
        IReadOnlyList<int> succeededIndexes,
        IReadOnlyList<StorageBulkOperationFailure> failures)
        => $"Bulk storage operation '{operation}' failed for {failures.Count} item(s); {succeededIndexes.Count} item(s) succeeded before or after the failures.";
}

public sealed record StorageBulkOperationFailure(
    int Index,
    string? StorageKey,
    string? ProviderLocator,
    Exception Exception);
