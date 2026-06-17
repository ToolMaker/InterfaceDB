namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when no complete forward migration path reaches the active version.
/// </summary>
public sealed class MissingMigrationException : MigrationException
{
    public MissingMigrationException(
        string sourceVersion,
        string activeVersion,
        string missingSourceVersion)
        : base(
            $"Migration chain from '{sourceVersion}' to '{activeVersion}' is missing a migration whose source version is '{missingSourceVersion}'.")
    {
        SourceVersion = sourceVersion;
        ActiveVersion = activeVersion;
        MissingSourceVersion = missingSourceVersion;
    }

    public string SourceVersion { get; }
    public string ActiveVersion { get; }
    public string MissingSourceVersion { get; }
}
