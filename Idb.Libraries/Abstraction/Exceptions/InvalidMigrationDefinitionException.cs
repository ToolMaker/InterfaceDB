namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when a migration declares malformed, backward, or cyclic version metadata.
/// </summary>
public sealed class InvalidMigrationDefinitionException : MigrationException
{
    public InvalidMigrationDefinitionException(
        Type migrationType,
        string sourceVersion,
        string targetVersion,
        string reason)
        : base(
            $"Migration '{migrationType.FullName}' from '{sourceVersion}' to '{targetVersion}' is invalid: {reason}")
    {
        MigrationType = migrationType;
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        Reason = reason;
    }

    public Type MigrationType { get; }
    public string SourceVersion { get; }
    public string TargetVersion { get; }
    public string Reason { get; }
}
