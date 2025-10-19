using System.Collections.Generic;
using System.Threading;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Migrations;

/// <summary>
/// Describes how migrations should be executed for a contract.
/// </summary>
public interface IMigrationStrategy<TContract, TLocationType>
    where TContract : class, IRepoDto<TContract, TLocationType>
{
    /// <summary>
    /// Gets the desired migration strategy.
    /// </summary>
    MigrationStrategyType StrategyType { get; }

    // TODO: Expand parameters once migration pipeline is wired (e.g., include cancellation, logging, context services).
    IAsyncEnumerable<IObjectEnvelope<TContract, TLocationType>> OnInitializeAsync(CancellationToken cancellationToken = default);

    IObjectEnvelope<TContract, TLocationType> Migrate(IObjectEnvelope<TContract, TLocationType> envelopeToMigrate, CancellationToken cancellationToken = default);
}
