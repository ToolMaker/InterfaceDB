using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Repository;

public interface IRepositoryNode : IRepository
{
    ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class;

    void Subscribe(IndexSubscription subscription, Action<IndexChange> handler);

    void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler);
}
