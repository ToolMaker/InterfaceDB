using System;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Repository facade that delegates persistence operations to the injected <see cref="IIndexManager{TContract}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class InterfaceDb<TContract> : IRepository<TContract>
    where TContract : class
{
    private readonly IIndexManager<TContract> _indexManager;

    public InterfaceDb(
        IIndexManager<TContract> indexManager,
        ContractRepositoryType repositoryType)
    {
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        RepositoryType = repositoryType;

        var contractType = typeof(TContract);
        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException($"InterfaceDb requires TContract to be an interface. Found '{contractType.FullName}'.");
        }

        if (!Attribute.IsDefined(contractType, typeof(StorageScopeAttribute), inherit: false))
        {
            throw new InvalidOperationException($"Contract '{contractType.FullName}' must be decorated with [StorageScope].");
        }
    }

    public ContractRepositoryType RepositoryType { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _indexManager.InitializeAsync(cancellationToken);

    public ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
    {
        if (entityToSave is null)
        {
            throw new ArgumentNullException(nameof(entityToSave));
        }

        return _indexManager.SaveAsync(entityToSave, cancellationToken);
    }

    public async ValueTask<TContract> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        if (filterFunction is null)
        {
            throw new ArgumentNullException(nameof(filterFunction));
        }

        await foreach (var match in _indexManager.QueryAsync(filterFunction, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null!;
    }

    public ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
    {
        if (entityToCheck is null)
        {
            throw new ArgumentNullException(nameof(entityToCheck));
        }

        return _indexManager.ExistsAsync(entityToCheck, cancellationToken);
    }
}
