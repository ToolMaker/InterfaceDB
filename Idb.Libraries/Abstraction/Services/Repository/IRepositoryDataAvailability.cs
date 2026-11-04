namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>Optional completeness contract. An incomplete enumeration must never be
/// used as an authoritative inventory for reconciliation, deletion or export.</summary>
public interface IRepositoryDataAvailability
{
    bool IsDataComplete { get; }
}

public static class RepositoryDataAvailability
{
    public static void RequireComplete(object repository)
    {
        if (repository is IRepositoryDataAvailability { IsDataComplete: false })
            throw new InvalidOperationException("Repository data is incomplete. Complete its recovery before synchronization or authoritative export.");
    }
}
