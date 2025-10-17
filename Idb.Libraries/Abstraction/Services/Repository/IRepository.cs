using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Dtos;
using System.Linq.Expressions;

namespace Idb.Libraries.Abstractions.Services.Repository;

public interface IRepository
{
    ContractRepositoryType RepositoryType { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<IObjectWriteResult> SaveAsync<TContract>(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
        where TContract : class;

    ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TContract : class;

    ValueTask<bool> ExistsAsync<TContract>(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
        where TContract : class;
}

