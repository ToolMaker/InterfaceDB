using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;
namespace Idb.Commercial.Repositories.Peers;
public sealed partial class PeerReplicatedRepository<T> where T : class, IStoredContract<T>
{
    public ValueTask<bool> ExistsAsync(T entityToCheck, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.ExistsAsync(entityToCheck, cancellationToken), cancellationToken);
    public ValueTask<T?> QueryAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<TImplementation?> QueryAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, T
        => WithRepositoryAsync(repository => repository.QueryAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<T> QueryManyAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.QueryManyAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TImplementation> QueryManyAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, T
        => StreamAsync(repository => repository.QueryManyAsync(filterFunction, cancellationToken), cancellationToken);
    public IAsyncEnumerable<T> SearchManyAsync(Expression<Func<T, bool>> filterExpression, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.SearchManyAsync(filterExpression, cancellationToken), cancellationToken);
    public IAsyncEnumerable<TImplementation> SearchManyAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken = default) where TImplementation : class, T
        => StreamAsync(repository => repository.SearchManyAsync(filterExpression, cancellationToken), cancellationToken);
    public IAsyncEnumerable<T> QueryManyByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => StreamAsync(repository => repository.QueryManyByStorageKeyAsync(storageKey, cancellationToken), cancellationToken);
    public ValueTask<T?> QueryFirstAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryFirstAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<TImplementation?> QueryFirstAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default) where TImplementation : class, T
        => WithRepositoryAsync(repository => repository.QueryFirstAsync(filterFunction, cancellationToken), cancellationToken);
    public ValueTask<T?> SearchFirstAsync(Expression<Func<T, bool>> filterExpression, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.SearchFirstAsync(filterExpression, cancellationToken), cancellationToken);
    public ValueTask<TImplementation?> SearchFirstAsync<TImplementation>(Expression<Func<TImplementation, bool>> filterExpression, CancellationToken cancellationToken = default) where TImplementation : class, T
        => WithRepositoryAsync(repository => repository.SearchFirstAsync(filterExpression, cancellationToken), cancellationToken);
    public ValueTask<T?> QueryFirstByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => WithRepositoryAsync(repository => repository.QueryFirstByStorageKeyAsync(storageKey, cancellationToken), cancellationToken);
    public ValueTask<T> QuerySingleAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null)
        => WithRepositoryAsync(repository => repository.QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory), cancellationToken);
    public ValueTask<TImplementation> QuerySingleAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null, Func<Exception>? multipleExceptionFactory = null) where TImplementation : class, T
        => WithRepositoryAsync(repository => repository.QuerySingleAsync(filterFunction, cancellationToken, noneExceptionFactory, multipleExceptionFactory), cancellationToken);
    public ValueTask<T?> QuerySingleOrDefaultAsync(Func<T, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
        => WithRepositoryAsync(repository => repository.QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory), cancellationToken);
    public ValueTask<TImplementation?> QuerySingleOrDefaultAsync<TImplementation>(Func<TImplementation, bool> filterFunction, CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null) where TImplementation : class, T
        => WithRepositoryAsync(repository => repository.QuerySingleOrDefaultAsync(filterFunction, cancellationToken, multipleExceptionFactory), cancellationToken);
}
