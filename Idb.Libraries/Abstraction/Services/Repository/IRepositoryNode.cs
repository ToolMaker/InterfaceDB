using System.Collections.Generic;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Libraries.Abstractions.Services.Repository;

public interface IRepositoryNode : IRepository
{
    ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class;

    void Subscribe(IndexSubscription subscription, Action<IndexChange> handler);

    void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler);
}
