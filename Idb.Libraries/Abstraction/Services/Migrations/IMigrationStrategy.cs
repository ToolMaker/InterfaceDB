using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Migrations;

/// <summary>
/// Describes how migrations should be executed for a contract.
/// </summary>
public interface IMigrationStrategy<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Gets the desired migration strategy.
    /// </summary>
    MigrationStrategyType StrategyType { get; }

    // TODO: Expand parameters once migration pipeline is wired (e.g., include cancellation, logging, context services).
    IAsyncEnumerable<TContract> OnInitializeAsync(CancellationToken cancellationToken = default);

    TContract Migrate(TContract objectInstance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates storage identified by an envelope before the legacy object must be materialized.
    /// </summary>
    ValueTask<TContract?> MigrateAsync(
        IObjectEnvelope<TContract> envelope,
        CancellationToken cancellationToken = default);
}
