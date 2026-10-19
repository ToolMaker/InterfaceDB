using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Diagnostics;

/// <summary>Borrowed repository-operation boundary; preserves the provider's interfaces and ownership.</summary>
internal sealed class TelemetryIndexManager<T>(IIndexManager<T> inner, IDatabaseTelemetry telemetry) : IIndexManager<T>
    where T : class, IStoredContract<T>
{
    private ValueTask<R> Observe<R>(DatabaseOperation operation, Func<ValueTask<R>> action)
        => DatabaseTelemetryOperation.ObserveAsync(telemetry, DatabaseComponent.Repository, operation, action, log: true);
    private IAsyncEnumerable<R> Observe<R>(DatabaseOperation operation, Func<IAsyncEnumerable<R>> source, CancellationToken token)
    {
        try { return DatabaseTelemetryOperation.ObserveStream(telemetry, DatabaseComponent.Repository, operation, source(), token); }
        catch (Exception exception)
        {
            DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, operation, log: true).Fail(exception);
            throw;
        }
    }
    public ValueTask<T> SaveAsync(T entity, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Save, () => inner.SaveAsync(entity, cancellationToken));
    public ValueTask<IReadOnlyList<T>> SaveManyAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.SaveBatch, () => inner.SaveManyAsync(entities, cancellationToken));
    public ValueTask<bool> DeleteAsync(T entity, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Delete, () => inner.DeleteAsync(entity, cancellationToken));
    public ValueTask<bool> ExistsAsync(T entity, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Read, () => inner.ExistsAsync(entity, cancellationToken));
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(telemetry, DatabaseComponent.Repository, DatabaseOperation.Initialize,
            () => inner.InitializeAsync(cancellationToken), log: true);
    public IAsyncEnumerable<T> QueryAsync(Func<T, bool> predicate, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Query, () => inner.QueryAsync(predicate, cancellationToken), cancellationToken);
    public IAsyncEnumerable<R> QueryAsync<R>(Func<R, bool> predicate, CancellationToken cancellationToken = default) where R : class, T
        => Observe(DatabaseOperation.Query, () => inner.QueryAsync(predicate, cancellationToken), cancellationToken);
    public IAsyncEnumerable<T> SearchAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Search, () => inner.SearchAsync(predicate, cancellationToken), cancellationToken);
    public IAsyncEnumerable<R> SearchAsync<R>(Expression<Func<R, bool>> predicate, CancellationToken cancellationToken = default) where R : class, T
        => Observe(DatabaseOperation.Search, () => inner.SearchAsync(predicate, cancellationToken), cancellationToken);
    public IAsyncEnumerable<T> QueryByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default)
        => Observe(DatabaseOperation.Read, () => inner.QueryByStorageKeyAsync(storageKey, cancellationToken), cancellationToken);
    public SearchQueryPlanExplanation ExplainSearch(Expression<Func<T, bool>> predicate) => inner.ExplainSearch(predicate);
}
