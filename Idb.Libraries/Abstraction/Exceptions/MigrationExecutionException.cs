namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when migration code fails to upgrade a stored object.
/// </summary>
public sealed class MigrationExecutionException : MigrationException
{
    public MigrationExecutionException(
        Type migrationType,
        string sourceVersion,
        string targetVersion,
        string? storageKey,
        string? providerLocator,
        Exception? innerException = null)
        : base(
            $"Migration '{migrationType.FullName}' failed to upgrade storage object '{storageKey ?? "<unknown>"}' " +
            $"from '{sourceVersion}' to '{targetVersion}'.",
            innerException ?? new InvalidOperationException("Migration returned false."))
    {
        MigrationType = migrationType;
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
        StorageKey = storageKey;
        ProviderLocator = providerLocator;
    }

    public Type MigrationType { get; }
    public string SourceVersion { get; }
    public string TargetVersion { get; }
    public string? StorageKey { get; }
    public string? ProviderLocator { get; }
}
