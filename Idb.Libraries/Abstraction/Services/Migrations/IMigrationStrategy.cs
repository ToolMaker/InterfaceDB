using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Services.Migrations;

/// <summary>
/// Describes how migrations should be executed for a contract.
/// </summary>
public interface IMigrationStrategy
{
    /// <summary>
    /// Gets the desired migration strategy.
    /// </summary>
    MigrationStrategyType Strategy { get; }
}

