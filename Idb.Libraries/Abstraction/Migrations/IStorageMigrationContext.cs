using Idb.Libraries.Abstractions.Serialization;

namespace Idb.Libraries.Abstractions.Migrations
{
    /// <summary>
    /// Provides helpers to read legacy payloads and write upgraded objects for a specific storage migration.
    /// </summary>
    public interface IStorageMigrationContext
    {
        /// <summary>The storage contract interface being migrated.</summary>
        Type ContractType { get; }

        /// <summary>Tag of the version currently stored on disk.</summary>
        string SourceVersionTag { get; }

        /// <summary>Tag of the version results will be written to.</summary>
        string TargetVersionTag { get; }

        /// <summary>File extension (including leading dot) used for persisted payloads.</summary>
        string FileExtension { get; }

        /// <summary>Serializer responsible for reading and writing envelopes.</summary>
        IObjectSerializer Serializer { get; }

        /// <summary>Enumerates all payloads from the source version as the specified runtime type.</summary>
        IAsyncEnumerable<IStorageMigrationRecord<T>> ReadSourceAsync<T>(CancellationToken cancellationToken = default);

        /// <summary>Writes the upgraded entity (using the target contract) keeping the supplied identifier.</summary>
        ValueTask WriteAsync<TContract>(string id, TContract entity, CancellationToken cancellationToken = default);

        /// <summary>Deletes the legacy payload once the upgraded entity has been persisted.</summary>
        ValueTask DeleteSourceAsync(string id, CancellationToken cancellationToken = default);
    }
}
