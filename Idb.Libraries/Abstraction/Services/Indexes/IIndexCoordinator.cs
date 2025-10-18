using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Indexes;

/// <summary>
/// Mediates between repositories, indexes, and the persistence layer.
/// Responsible for creating envelopes, updating indexes, and delegating
/// IO to writer/reader implementations.
/// </summary>
public interface IIndexCoordinator<TContract>
    where TContract : class
{
    ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default);

    ValueTask<TContract> LoadAsync(string? versionTag = null, CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(TContract objToCheck, CancellationToken cancellationToken = default);
        
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
