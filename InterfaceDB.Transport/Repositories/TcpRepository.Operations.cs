using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Transport.Repositories;

public sealed partial class TcpRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    public ValueTask<TContract> SaveAsync(TContract entityToSave, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.SaveAsync(entityToSave, cancellationToken), cancellationToken);
    public ValueTask<IReadOnlyList<TContract>> SaveManyAsync(IEnumerable<TContract> entitiesToSave, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitiesToSave);
        var inputs = entitiesToSave.Take(_options.Connection.Limits.MaximumBatchItems).ToArray();
        if (inputs.Length >= _options.Connection.Limits.MaximumBatchItems)
            throw new ArgumentException("The batch exceeds the configured limit including repository metadata.");
        return WithRepositoryAsync(repository => repository.SaveManyAsync(inputs, cancellationToken), cancellationToken);
    }
    public ValueTask<bool> DeleteAsync(TContract entityToDelete, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.DeleteAsync(entityToDelete, cancellationToken), cancellationToken);
    public ValueTask<bool> ExistsAsync(TContract entityToCheck, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.ExistsAsync(entityToCheck, cancellationToken), cancellationToken);
    public ValueTask<TContract> QueryAsync(Func<TContract, bool> filterFunction, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<TImplementation> QueryAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, TContract
        => WithRepositoryAsync(repository => repository.QueryAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TContract> QueryManyAsync(Func<TContract, bool> filterFunction, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.QueryManyAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TImplementation> QueryManyAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, TContract
        => StreamAsync(repository => repository.QueryManyAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TContract> SearchManyAsync(Expression<Func<TContract, bool>> filterExpression, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.SearchManyAsync(filterExpression, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TImplementation> SearchManyAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken = default) where TImplementation : class, TContract
        => StreamAsync(repository => repository.SearchManyAsync(filterExpression, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TContract> QueryManyByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.QueryManyByStorageKeyAsync(storageKey, cancellationToken), cancellationToken);
    public ValueTask<TContract?> QueryFirstAsync(Func<TContract, bool> filterFunction, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryFirstAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<TImplementation?> QueryFirstAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, TContract
        => WithRepositoryAsync(repository => repository.QueryFirstAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<TContract?> SearchFirstAsync(Expression<Func<TContract, bool>> filterExpression, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.SearchFirstAsync(filterExpression, cancellationToken), cancellationToken);
    public ValueTask<TImplementation?> SearchFirstAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken = default) where TImplementation : class, TContract
        => WithRepositoryAsync(repository => repository.SearchFirstAsync(filterExpression, cancellationToken), cancellationToken);
    public ValueTask<TContract?> QueryFirstByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryFirstByStorageKeyAsync(storageKey, cancellationToken), cancellationToken);
    public ValueTask<TContract> QuerySingleAsync(Func<TContract, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null)
        => WithRepositoryAsync(repository => repository.QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory), cancellationToken);
    public ValueTask<TImplementation> QuerySingleAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null) where TImplementation : class, TContract
        => WithRepositoryAsync(repository => repository.QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory), cancellationToken);
    public ValueTask<TContract?> QuerySingleOrDefaultAsync(Func<TContract, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
        => WithRepositoryAsync(repository => repository.QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory), cancellationToken);
    public ValueTask<TImplementation?> QuerySingleOrDefaultAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null) where TImplementation : class, TContract
        => WithRepositoryAsync(repository => repository.QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory), cancellationToken);
}
