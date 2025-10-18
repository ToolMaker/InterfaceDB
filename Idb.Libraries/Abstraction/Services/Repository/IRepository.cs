using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Dtos;
using System.Linq.Expressions;

namespace Idb.Libraries.Abstractions.Services.Repository;

public interface IRepository<TContract>
    where TContract : class
{
    ContractRepositoryType RepositoryType { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<IObjectWriteResult<TContract>> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default);

    ValueTask<IObjectReadResult<TContract>> QueryAsync(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default);
}

