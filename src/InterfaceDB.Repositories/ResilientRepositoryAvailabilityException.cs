namespace Idb.Commercial.Repositories;

/// <summary>
/// Indicates that neither endpoint could provide a reliable repository result.
/// </summary>
public sealed class ResilientRepositoryAvailabilityException : IOException
{
    public ResilientRepositoryAvailabilityException(
        ResilientRepositoryOperation operation,
        string? storageKey,
        bool isPartialResult,
        Exception? preferredException,
        Exception? peerException)
        : base(
            CreateMessage(operation, storageKey, isPartialResult),
            CreateInnerException(preferredException, peerException))
    {
        Operation = operation;
        StorageKey = storageKey;
        IsPartialResult = isPartialResult;
        PreferredException = preferredException;
        PeerException = peerException;
    }

    public ResilientRepositoryOperation Operation { get; }

    public string? StorageKey { get; }

    public bool IsPartialResult { get; }

    public Exception? PreferredException { get; }

    public Exception? PeerException { get; }

    private static string CreateMessage(
        ResilientRepositoryOperation operation,
        string? storageKey,
        bool isPartialResult)
    {
        var scope = storageKey is null ? "the repository" : $"storage key '{storageKey}'";
        var result = isPartialResult ? "complete" : "reliable";
        return $"The resilient repository could not produce a {result} result for {scope} during {operation}.";
    }

    private static Exception CreateInnerException(
        Exception? preferredException,
        Exception? peerException)
    {
        if (preferredException is not null && peerException is not null)
        {
            return new AggregateException(preferredException, peerException);
        }

        return preferredException
            ?? peerException
            ?? throw new ArgumentException("At least one endpoint exception is required.");
    }
}
