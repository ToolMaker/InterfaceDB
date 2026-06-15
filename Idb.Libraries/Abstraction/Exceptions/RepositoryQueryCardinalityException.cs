namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when a repository query returns a number of results outside the requested bounds.
/// </summary>
public sealed class RepositoryQueryCardinalityException : InvalidOperationException
{
    private RepositoryQueryCardinalityException(string message)
        : base(message)
    {
    }

    public static RepositoryQueryCardinalityException NoMatches()
        => new("The repository query expected exactly one result but found none.");

    public static RepositoryQueryCardinalityException MultipleMatches()
        => new("The repository query expected at most one result but found multiple matches.");
}
