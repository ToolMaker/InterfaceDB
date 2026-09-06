using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Reports partial or complete failure of a non-transactional bulk storage operation.
/// Successful writes remain committed unless a higher-level transaction feature is introduced.
/// </summary>
public sealed class StorageBulkOperationException : StorageException, IStorageBulkOperationResult
{
    public StorageBulkOperationException(
        StorageOperation operation,
        IReadOnlyList<int> succeededIndexes,
        IReadOnlyList<StorageBulkOperationFailure> failures,
        IReadOnlyDictionary<int, object>? committedItems = null)
        : base(CreateMessage(operation, succeededIndexes, failures))
    {
        Operation = operation;
        SucceededIndexes = succeededIndexes;
        Failures = failures;
        CommittedItems = committedItems ?? new Dictionary<int, object>();
    }

    public StorageOperation Operation { get; }
    public IReadOnlyList<int> SucceededIndexes { get; }
    public IReadOnlyDictionary<int, object> CommittedItems { get; }
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
