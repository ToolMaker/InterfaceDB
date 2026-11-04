using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Repository;

/// <summary>Imports committed records without publishing application repository notifications.
/// Index maintenance, durability and expected-missing conflict checks remain enabled.</summary>
public interface IEnvelopeImportRepository<T> where T : class, IStoredContract<T>
{
    /// <summary>Imports a detached record with its existing positive revision. Suppression
    /// applies only to this operation; concurrent application writes still notify normally.</summary>
    ValueTask<T> ImportWithoutNotificationsAsync(T detachedEntity, string physicalKey,
        long revision, byte[]? extensionMetadata, CancellationToken cancellationToken = default);
}
