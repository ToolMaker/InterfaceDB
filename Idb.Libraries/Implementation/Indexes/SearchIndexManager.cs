using System.Linq.Expressions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// In-memory search index implementation that forwards persistence to the envelope repository.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the manager.</typeparam>
public sealed class SearchIndexManager<TContract> : IIndexManager<TContract>, IDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;
    private readonly SearchQueryPlannerOptions _plannerOptions;
    private readonly bool _writesPublishStoredNotifications;
    private readonly SearchIndexStore<TContract> _searchIndexes;
    private readonly ConcurrentDictionary<Type, IRuntimeSearchIndexStore> _runtimeSearchIndexes = new();
    private IContractSubscription? _storedSubscription;
    private IContractSubscription? _deletedSubscription;
    private int _subscriptionsStarted;
    private int _disposed;

    public SearchIndexManager(
        IEnvelopeRepository<TContract> envelopeRepository,
        StoragePolicy storagePolicy)
    {
        _envelopeRepository = envelopeRepository ?? throw new ArgumentNullException(nameof(envelopeRepository));
        var resolvedStoragePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _writesPublishStoredNotifications = resolvedStoragePolicy.RaiseNotifications;
        _plannerOptions = SearchQueryPlannerOptions.For(resolvedStoragePolicy.ObjectCacheMode);
        _searchIndexes = new SearchIndexStore<TContract>(_plannerOptions);
    }

    public ValueTask<bool> ExistsAsync(TContract objToCheck, CancellationToken cancellationToken = default)
    {
        if (objToCheck is null)
        {
            throw new ArgumentNullException(nameof(objToCheck));
        }

        var envelope = objToCheck.Envelope;
        return new ValueTask<bool>(
            envelope is not null && _envelopeRepository.Exists(envelope.EnvelopeCacheKey));
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _envelopeRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await RebuildIndexesAsync(cancellationToken).ConfigureAwait(false);
        StartIndexSubscriptions();
    }

    public async ValueTask<bool> DeleteAsync(
        TContract entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var physicalKey = entity.Envelope?.EnvelopeCacheKey;
        var deleted = await _envelopeRepository.DeleteAsync(entity, cancellationToken).ConfigureAwait(false);
        if (deleted && !string.IsNullOrWhiteSpace(physicalKey))
        {
            _searchIndexes.Remove(physicalKey);
            RemoveRuntimeSearchIndexes(physicalKey);
        }

        return deleted;
    }

    public IAsyncEnumerable<TContract> QueryAsync(
        Func<TContract, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryInternalAsync(predicate, cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> QueryAsync<TImplementation>(
        Func<TImplementation, bool> predicate,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryInternalAsync(predicate, cancellationToken);
    }

    public IAsyncEnumerable<TContract> SearchAsync(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryExpressionInternalAsync(predicate, cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> SearchAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryExpressionInternalAsync(predicate, cancellationToken);
    }

    public SearchQueryPlanExplanation ExplainSearch(Expression<Func<TContract, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return _searchIndexes.ExplainPlan(predicate);
    }

    public IAsyncEnumerable<TContract> QueryByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        return QueryByStorageKeyInternalAsync(storageKey, cancellationToken);
    }

    public ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default)
    {
        return SaveInternalAsync(entity, cancellationToken);
    }

    public ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return SaveManyInternalAsync(entities, cancellationToken);
    }

    private async IAsyncEnumerable<TContract> QueryInternalAsync(
        Func<TContract, bool> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in _envelopeRepository.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) || entity is null)
            {
                throw new InvalidOperationException("Envelope does not contain an object instance.");
            }
            if (!predicate(entity))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
        }
    }

    private async IAsyncEnumerable<TImplementation> QueryInternalAsync<TImplementation>(
        Func<TImplementation, bool> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TImplementation : class, TContract
    {
        await foreach (var envelope in _envelopeRepository
            .EnumerateByImplementationTypeAsync<TImplementation>(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) || entity is null)
            {
                throw new InvalidOperationException("Envelope does not contain an object instance.");
            }
            var implementation = (TImplementation)entity;
            if (!predicate(implementation))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(implementation, envelope);
            yield return implementation;
        }
    }

    private async IAsyncEnumerable<TContract> QueryExpressionInternalAsync(
        Expression<Func<TContract, bool>> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var compiled = predicate.Compile();
        var keys = _searchIndexes.TryEvaluate(predicate);
        if (keys is null)
        {
            await foreach (var entity in QueryInternalAsync(compiled, cancellationToken).ConfigureAwait(false))
            {
                yield return entity;
            }

            yield break;
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = await _envelopeRepository.GetPhysicalAsync(key, cancellationToken).ConfigureAwait(false);
            TContract? entity = null;
            if (envelope is not null)
            {
                StorageObjectCachePolicy.TryGetObjectInstance(envelope, out entity);
            }
            if (entity is null || !compiled(entity))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
        }
    }

    private async IAsyncEnumerable<TImplementation> QueryExpressionInternalAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TImplementation : class, TContract
    {
        if (typeof(TImplementation) == typeof(TContract))
        {
            var contractPredicate = (Expression<Func<TContract, bool>>)(object)predicate;
            await foreach (var entity in QueryExpressionInternalAsync(contractPredicate, cancellationToken).ConfigureAwait(false))
            {
                yield return (TImplementation)entity;
            }

            yield break;
        }

        var compiled = predicate.Compile();
        var searchIndexes = GetRuntimeSearchIndexStore(typeof(TImplementation));
        var keys = searchIndexes.TryEvaluate(predicate);
        if (keys is null)
        {
            await foreach (var entity in QueryInternalAsync(compiled, cancellationToken).ConfigureAwait(false))
            {
                yield return entity;
            }

            yield break;
        }

        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = await _envelopeRepository.GetPhysicalAsync(key, cancellationToken).ConfigureAwait(false);
            TContract? entity = null;
            if (envelope is not null)
            {
                StorageObjectCachePolicy.TryGetObjectInstance(envelope, out entity);
            }
            if (entity is not TImplementation implementation || !compiled(implementation))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(implementation, envelope);
            yield return implementation;
        }
    }

    private async IAsyncEnumerable<TContract> QueryByStorageKeyInternalAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in _envelopeRepository.GetAsync(storageKey, cancellationToken).ConfigureAwait(false))
        {
            if (!StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) || entity is null)
            {
                throw new InvalidOperationException("Envelope does not contain an object instance.");
            }
            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
        }
    }

    private async ValueTask<TContract> SaveInternalAsync(
        TContract entity,
        CancellationToken cancellationToken)
    {
        var saved = await _envelopeRepository.WriteAsync(entity, cancellationToken).ConfigureAwait(false);
        if (!_writesPublishStoredNotifications)
        {
            UpdateIndex(saved);
        }

        return saved;
    }

    private async ValueTask<IReadOnlyList<TContract>> SaveManyInternalAsync(
        IEnumerable<TContract> entities,
        CancellationToken cancellationToken)
    {
        var saved = await _envelopeRepository.WriteManyAsync(entities, cancellationToken).ConfigureAwait(false);
        if (!_writesPublishStoredNotifications)
        {
            foreach (var entity in saved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateIndex(entity);
            }
        }

        return saved;
    }

    private async ValueTask RebuildIndexesAsync(CancellationToken cancellationToken)
    {
        _searchIndexes.Clear();
        foreach (var runtimeIndex in _runtimeSearchIndexes.Values)
        {
            runtimeIndex.Dispose();
        }

        _runtimeSearchIndexes.Clear();
        if (!_searchIndexes.HasIndexes)
        {
            // Runtime implementation types can still have their own index attributes.
        }

        await foreach (var envelope in _envelopeRepository.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) && entity is not null)
            {
                UpdateIndexes(envelope.EnvelopeCacheKey, entity);
            }
        }
    }

    private void StartIndexSubscriptions()
    {
        if (Interlocked.Exchange(ref _subscriptionsStarted, 1) != 0)
        {
            return;
        }

        _storedSubscription = _envelopeRepository.SubscribeToStored(envelope =>
        {
            if (StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) && entity is not null)
            {
                UpdateIndexes(envelope.EnvelopeCacheKey, entity);
            }

            return ValueTask.CompletedTask;
        });
        _deletedSubscription = _envelopeRepository.SubscribeToDeleted(envelope =>
        {
            _searchIndexes.Remove(envelope.EnvelopeCacheKey);
            RemoveRuntimeSearchIndexes(envelope.EnvelopeCacheKey);
            return ValueTask.CompletedTask;
        });
    }

    private void UpdateIndex(TContract entity)
    {
        var envelope = entity.Envelope;
        if (envelope is not null)
        {
            UpdateIndexes(envelope.EnvelopeCacheKey, entity);
        }
    }

    private void UpdateIndexes(string physicalKey, TContract entity)
    {
        _searchIndexes.Upsert(physicalKey, entity);
        var runtimeStore = GetRuntimeSearchIndexStore(entity.GetType());
        if (runtimeStore.HasIndexes)
        {
            runtimeStore.Upsert(physicalKey, entity);
        }
    }

    private IRuntimeSearchIndexStore GetRuntimeSearchIndexStore(Type runtimeType)
        => _runtimeSearchIndexes.GetOrAdd(
            runtimeType,
            type => new RuntimeSearchIndexStore(type, _plannerOptions));

    private void RemoveRuntimeSearchIndexes(string physicalKey)
    {
        foreach (var store in _runtimeSearchIndexes.Values)
        {
            store.Remove(physicalKey);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _storedSubscription?.Dispose();
        _deletedSubscription?.Dispose();
        _searchIndexes.Dispose();
        foreach (var runtimeIndex in _runtimeSearchIndexes.Values)
        {
            runtimeIndex.Dispose();
        }

        _runtimeSearchIndexes.Clear();
    }

    private interface IRuntimeSearchIndexStore : IDisposable
    {
        bool HasIndexes { get; }

        void Upsert(string physicalKey, object entity);

        void Remove(string physicalKey);

        HashSet<string>? TryEvaluate(LambdaExpression expression);
    }

    private sealed class RuntimeSearchIndexStore : IRuntimeSearchIndexStore
    {
        private readonly object _store;
        private readonly Action<string, object> _upsert;
        private readonly Action<string> _remove;
        private readonly Func<LambdaExpression, HashSet<string>?> _tryEvaluate;

        public bool HasIndexes { get; }

        public RuntimeSearchIndexStore(Type runtimeType, SearchQueryPlannerOptions plannerOptions)
        {
            var storeType = typeof(SearchIndexStore<>).MakeGenericType(runtimeType);
            _store = Activator.CreateInstance(storeType, plannerOptions)
                ?? throw new InvalidOperationException($"Unable to create search index store for '{runtimeType.FullName}'.");

            var upsert = storeType.GetMethod(nameof(SearchIndexStore<TContract>.Upsert))
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose Upsert.");
            var remove = storeType.GetMethod(nameof(SearchIndexStore<TContract>.Remove))
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose Remove.");
            var tryEvaluate = storeType.GetMethod(nameof(SearchIndexStore<TContract>.TryEvaluate))
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose TryEvaluate.");
            var hasIndexes = storeType.GetProperty(nameof(SearchIndexStore<TContract>.HasIndexes))
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose HasIndexes.");

            HasIndexes = (bool)(hasIndexes.GetValue(_store) ?? false);
            _upsert = (key, entity) => upsert.Invoke(_store, new[] { key, entity });
            _remove = key => remove.Invoke(_store, new object[] { key });
            _tryEvaluate = expression => (HashSet<string>?)tryEvaluate.Invoke(_store, new object[] { expression });
        }

        public void Upsert(string physicalKey, object entity)
            => _upsert(physicalKey, entity);

        public void Remove(string physicalKey)
            => _remove(physicalKey);

        public HashSet<string>? TryEvaluate(LambdaExpression expression)
            => _tryEvaluate(expression);

        public void Dispose()
        {
            if (_store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
