namespace Idb.Libraries.Abstractions.Services.Migrations;

/// <summary>
/// Represents a contract that can migrate its current state between schema versions.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Upgrades the current instance to the target (newer) schema.
    /// </summary>
    /// <returns><c>true</c> when the migration succeeded; otherwise <c>false</c>.</returns>
    bool Up();

    /// <summary>
    /// Downgrades the current instance to the source (older) schema.
    /// </summary>
    /// <returns><c>true</c> when the migration succeeded; otherwise <c>false</c>.</returns>
    bool Down();
}

