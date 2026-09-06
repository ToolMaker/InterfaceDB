namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>Identifies the items that committed before a non-transactional bulk operation failed or was cancelled.</summary>
public interface IStorageBulkOperationResult
{
    IReadOnlyList<int> SucceededIndexes { get; }
    IReadOnlyDictionary<int, object> CommittedItems { get; }
}

/// <summary>Cancellation does not roll back successful File writes. Inspect the committed results before retrying.</summary>
public sealed class StorageBulkOperationCanceledException : OperationCanceledException, IStorageBulkOperationResult
{
    public StorageBulkOperationCanceledException(IReadOnlyDictionary<int, object> committedItems,
        OperationCanceledException innerException, CancellationToken cancellationToken)
        : base("The bulk write was cancelled; successful items remain committed.", innerException, cancellationToken)
    {
        CommittedItems = committedItems;
        SucceededIndexes = committedItems.Keys.Order().ToArray();
    }
    public IReadOnlyList<int> SucceededIndexes { get; }
    public IReadOnlyDictionary<int, object> CommittedItems { get; }
}
