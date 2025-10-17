using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Libraries.Abstractions.Services.Migrations;

/// <summary>
/// Coordinates contract storage migrations for a repository implementation.
/// </summary>
public interface IMigrationManager
{

    MigrationStrategyType MigrationStrategyType { get; set; }
    /// <summary>
    /// Ensures that all known migrations have been applied for the supplied repository.
    /// </summary>
    /// <param name="repository">The repository instance that should run migrations.</param>
    /// <param name="cancellationToken">Propagation token for cancellation requests.</param>
    ValueTask InitializeAsync(IRepository repository, CancellationToken cancellationToken = default);
}

