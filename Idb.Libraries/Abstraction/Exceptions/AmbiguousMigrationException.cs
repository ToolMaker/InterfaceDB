namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when more than one migration can handle the same source version.
/// </summary>
public sealed class AmbiguousMigrationException : MigrationException
{
    public AmbiguousMigrationException(string sourceVersion, IEnumerable<Type> migrationTypes)
        : base(
            $"Multiple migrations are registered for source version '{sourceVersion}': " +
            string.Join(", ", migrationTypes.Select(type => type.FullName ?? type.Name)) + ".")
    {
        SourceVersion = sourceVersion;
        MigrationTypes = migrationTypes.ToArray();
    }

    public string SourceVersion { get; }
    public IReadOnlyList<Type> MigrationTypes { get; }
}
