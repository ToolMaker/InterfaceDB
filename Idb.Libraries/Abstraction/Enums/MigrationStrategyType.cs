namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Describes how a repository should apply storage migrations.
/// </summary>
public enum MigrationStrategyType
{
    None = 0,
    /// <summary>
    /// Migrate records lazily on demand; failures leave the record on the legacy version and are logged.
    /// </summary>
    Lazy = 1,

    /// <summary>
    /// Run migrations eagerly during initialization and fail startup if any record cannot be upgraded.
    /// </summary>
    EagerStrict = 2,

    /// <summary>
    /// Run migrations eagerly but tolerate failures by recording them and keeping legacy data accessible.
    /// </summary>
    EagerTolerant = 3
}

