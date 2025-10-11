using System.Collections.Generic;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Libraries.Abstractions.Services.Repository;

public interface IRepository
{
    ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entity,
        string? id = null,
        CancellationToken cancellationToken = default)
        where TContract : class;

    ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class;

    ValueTask<bool> ExistsAsync<TContract>(
        string id,
        string? versionTag = null,
        CancellationToken cancellationToken = default)
        where TContract : class;

    void ConfigureIndex(IRepositoryIndexConfiguration configuration);

    void ConfigureIndexes(IEnumerable<IRepositoryIndexConfiguration> configurations);

    IReadOnlyCollection<IRepositoryIndexConfiguration> GetIndexConfigurations(Type contractType);

    bool TryGetSortedListIndex<TContract, TKey>(
        string name,
        out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull;
}
