namespace Idb.Libraries.Abstractions.Services.Migrations
{
    /// <summary>
    /// Describes a migration that upgrades persisted storage data for a specific contract interface
    /// from one version tag to the next.
    /// </summary>
    public interface IStorageMigration
    {
        /// <summary>The contract interface this migration handles.</summary>
        Type ContractType { get; }

        /// <summary>Version tag from which this migration reads.</summary>
        string SourceVersionTag { get; }

        /// <summary>Version tag to which this migration writes.</summary>
        string TargetVersionTag { get; }

        /// <summary>Executes the migration logic.</summary>
        Task ExecuteAsync(IStorageMigrationContext context, CancellationToken cancellationToken);
    }
}
