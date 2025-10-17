using System.Linq.Expressions;
using Idb.Libraries.Abstraction.Dtos;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;

namespace IdbDemoStorage.ConsoleHost;

internal sealed class ProvisionedRepositoryNode : IRepositoryNode
{
    private readonly IRepositoryNode _inner;

    public ProvisionedRepositoryNode(
        IRepositoryNode inner,
        IDbContractConfigDto option,
        Type contractType)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ = option ?? throw new ArgumentNullException(nameof(option));
        _ = contractType ?? throw new ArgumentNullException(nameof(contractType));
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _inner.InitializeAsync(cancellationToken);

    public ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entityToSave,
        string? id = null,
        CancellationToken cancellationToken = default)
        where TContract : class
        => _inner.SaveAsync(entityToSave, id, cancellationToken);

    public ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TContract : class
        => _inner.LoadAsync(predicate, cancellationToken);

    public ValueTask<bool> ExistsAsync<TContract>(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
        where TContract : class
        => _inner.ExistsAsync(entityToCheck, cancellationToken);

    public ValueTask<IReadOnlyList<TContract>> QueryAsync<TContract>(
        IndexQuery query,
        CancellationToken cancellationToken = default)
        where TContract : class
        => _inner.QueryAsync<TContract>(query, cancellationToken);

    public void Subscribe(IndexSubscription subscription, Action<IndexChange> handler)
        => _inner.Subscribe(subscription, handler);

    public void Unsubscribe(IndexSubscription subscription, Action<IndexChange> handler)
        => _inner.Unsubscribe(subscription, handler);
}
