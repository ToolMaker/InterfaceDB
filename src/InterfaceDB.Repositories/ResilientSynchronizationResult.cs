namespace Idb.Commercial.Repositories;

/// <summary>
/// Summary of one explicit synchronization pass.
/// </summary>
public sealed record ResilientSynchronizationResult(
    int PendingMutationsExamined,
    int PeerCommits,
    int Acknowledgements,
    int PreferredRefreshes,
    int Conflicts,
    IReadOnlyList<ResilientRepositoryFailure> Failures)
{
    public bool IsSuccessful => Conflicts == 0 && Failures.Count == 0;
}
