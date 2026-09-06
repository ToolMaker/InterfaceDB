using System.Linq.Expressions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
public sealed class SearchIndexManager<TContract> : IIndexManager<TContract>, IDisposable, IAsyncDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;
    private readonly SearchQueryPlannerOptions _plannerOptions;
    private readonly bool _writesPublishStoredNotifications;
    private readonly SearchIndexStore<TContract> _searchIndexes;
    private readonly ConcurrentDictionary<Type, IRuntimeSearchIndexStore> _runtimeSearchIndexes = new();
    private readonly ConcurrentDictionary<string, Type> _runtimeTypesByPhysicalKey = new(StringComparer.Ordinal);
    private readonly object _initializationGate = new();
    private readonly object _indexMutationGate = new();
    private readonly Dictionary<string, long> _lastMutationOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingIndexMutation> _pendingIndexMutations = new(StringComparer.Ordinal);
    private IPersistentSearchIndexCatalogRepository? _persistentCatalogRepository;
    private string? _persistentCatalogPath;
    private int _persistentCatalogDirty;
    private IContractSubscription? _storedSubscription;
    private IContractSubscription? _deletedSubscription;
    private Task? _initializationTask;
    private Task? _disposalTask;
    private int _subscriptionsStarted;
    private int _bufferIndexMutations = 1;
    private int _initialized;
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

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _initialized) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Task initialization;
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            initialization = _initializationTask ??= InitializeCoreAsync();
        }

        return new ValueTask(initialization.WaitAsync(cancellationToken));
    }

    private async Task InitializeCoreAsync()
    {
        // Subscribe before the snapshot/catalog rebuild begins. Mutations are journaled until
        // the rebuild is installed, then replayed under the same gate used by live updates.
        StartIndexSubscriptions();

        if (_envelopeRepository is IPersistentSearchIndexCatalogRepository persistentCatalog)
        {
            await persistentCatalog.InitializeForSearchIndexCatalogAsync(CancellationToken.None).ConfigureAwait(false);
            _persistentCatalogRepository = persistentCatalog;
            _persistentCatalogPath = PersistentSearchIndexCatalog.GetPath(
                persistentCatalog.SearchIndexCatalogDirectory,
                _searchIndexes.SchemaFingerprint.Span);
            var storageIdentity = await persistentCatalog
                .GetSearchIndexStorageIdentityAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (TryLoadPersistentCatalog(storageIdentity, CancellationToken.None))
            {
                CompleteIndexBootstrap();
                Volatile.Write(ref _initialized, 1);
                return;
            }

            var initializationEnvelopes = _envelopeRepository
                is IEnvelopeRepositoryInitializationStream<TContract> persistentInitializationStream
                    ? persistentInitializationStream.InitializeAndEnumerateAsync(CancellationToken.None)
                    : _envelopeRepository.EnumerateAsync(CancellationToken.None);
            await RebuildIndexesAsync(
                initializationEnvelopes,
                CancellationToken.None).ConfigureAwait(false);
            await TryPersistCatalogAsync(CancellationToken.None, storageIdentity).ConfigureAwait(false);
            CompleteIndexBootstrap();
            Volatile.Write(ref _initialized, 1);
            return;
        }

        if (_envelopeRepository is IEnvelopeRepositoryInitializationStream<TContract> initializationStream)
        {
            await RebuildIndexesAsync(
                initializationStream.InitializeAndEnumerateAsync(CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _envelopeRepository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await RebuildIndexesAsync(
                _envelopeRepository.EnumerateAsync(CancellationToken.None),
                CancellationToken.None).ConfigureAwait(false);
        }

        CompleteIndexBootstrap();
        Volatile.Write(ref _initialized, 1);
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
            QueueOrApplyDelete(physicalKey, EnvelopeCommitOrder.Get(entity.Envelope));
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
        var compiled = GetCompiledPredicate(predicate);
        var keys = _searchIndexes.TryEvaluate(predicate);
        if (keys is null)
        {
            await foreach (var entity in QueryInternalAsync(compiled, cancellationToken).ConfigureAwait(false))
            {
                yield return entity;
            }

            yield break;
        }

        await foreach (var envelope in EnvelopeBatchEnumeration
            .ReadAsync(_envelopeRepository, keys, cancellationToken).ConfigureAwait(false))
        {
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

        var compiled = GetCompiledPredicate(predicate);
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

        await foreach (var envelope in EnvelopeBatchEnumeration
            .ReadAsync(_envelopeRepository, keys, cancellationToken).ConfigureAwait(false))
        {
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
        IReadOnlyList<TContract> saved;
        var materialized = entities as IReadOnlyList<TContract> ?? entities.ToArray();
        try { saved = await _envelopeRepository.WriteManyAsync(materialized, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is Idb.Libraries.Abstractions.Exceptions.IStorageBulkOperationResult)
        {
            var partial = (Idb.Libraries.Abstractions.Exceptions.IStorageBulkOperationResult)exception;
            if (!_writesPublishStoredNotifications)
            {
                try
                {
                    foreach (var index in partial.SucceededIndexes)
                    {
                        if (partial.CommittedItems.TryGetValue(index, out var committed))
                        {
                            UpdateIndex((TContract)committed);
                            continue;
                        }
                        var entity = materialized[index];
                        if (entity.Envelope is { } envelope
                            && _envelopeRepository is ICommittedEnvelopeSnapshotSource<TContract> snapshots)
                        {
                            var snapshot = await snapshots.CaptureCommittedAsync(envelope, int.MaxValue, CancellationToken.None).ConfigureAwait(false)
                                ?? throw new InvalidDataException("A successful batch write could not be read for index refresh.");
                            UpdateIndex(snapshot.Materialize());
                        }
                        else UpdateIndex(entity);
                    }
                }
                catch (Exception refreshFailure)
                { exception.Data["InterfaceDB.IndexRefreshFailure"] = refreshFailure; }
            }
            throw;
        }
        if (!_writesPublishStoredNotifications)
        {
            foreach (var entity in saved)
            {
                UpdateIndex(entity);
            }
        }

        return saved;
    }

    private async ValueTask RebuildIndexesAsync(
        IAsyncEnumerable<IObjectEnvelope<TContract>> envelopes,
        CancellationToken cancellationToken)
    {
        _searchIndexes.Clear();
        foreach (var runtimeIndex in _runtimeSearchIndexes.Values)
        {
            runtimeIndex.Dispose();
        }

        _runtimeSearchIndexes.Clear();
        _runtimeTypesByPhysicalKey.Clear();
        if (!_searchIndexes.HasIndexes)
        {
            // Runtime implementation types can still have their own index attributes.
        }

        using var contractBulkLoad = _searchIndexes.BeginBulkLoad();
        var runtimeBulkLoads = new Dictionary<Type, IDisposable>();
        try
        {
            await foreach (var envelope in envelopes.ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var entity) && entity is not null)
                {
                    var runtimeType = entity.GetType();
                    if (!runtimeBulkLoads.ContainsKey(runtimeType))
                    {
                        runtimeBulkLoads.Add(
                            runtimeType,
                            GetRuntimeSearchIndexStore(runtimeType).BeginBulkLoad());
                    }

                    UpdateIndexes(envelope.EnvelopeCacheKey, entity);
                }
            }
        }
        finally
        {
            DisposeBulkLoads(runtimeBulkLoads.Values);
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
                QueueOrApplyUpsert(envelope.EnvelopeCacheKey, entity);
            }

            return ValueTask.CompletedTask;
        });
        _deletedSubscription = _envelopeRepository.SubscribeToDeleted(envelope =>
        {
            QueueOrApplyDelete(envelope.EnvelopeCacheKey, EnvelopeCommitOrder.Get(envelope));
            return ValueTask.CompletedTask;
        });
    }

    private void UpdateIndex(TContract entity)
    {
        var envelope = entity.Envelope;
        if (EnvelopeWriteOutcome.IsNoChange(envelope)) return;
        if (envelope is not null)
        {
            QueueOrApplyUpsert(envelope.EnvelopeCacheKey, entity);
        }
    }

    private void QueueOrApplyUpsert(string physicalKey, TContract entity)
    {
        lock (_indexMutationGate)
        {
            var order = EnvelopeCommitOrder.Get(entity.Envelope);
            if (order > 0 && _lastMutationOrder.TryGetValue(physicalKey, out var prior) && prior > order) return;
            if (order > 0) _lastMutationOrder[physicalKey] = order;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_bufferIndexMutations != 0)
            {
                _pendingIndexMutations[physicalKey] = new PendingIndexMutation(entity, IsDelete: false);
                return;
            }

            UpdateIndexes(physicalKey, entity);
        }
    }

    private void QueueOrApplyDelete(string physicalKey, long order = 0)
    {
        lock (_indexMutationGate)
        {
            if (order > 0 && _lastMutationOrder.TryGetValue(physicalKey, out var prior) && prior > order) return;
            if (order > 0) _lastMutationOrder[physicalKey] = order;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_bufferIndexMutations != 0)
            {
                _pendingIndexMutations[physicalKey] = new PendingIndexMutation(null, IsDelete: true);
                return;
            }

            ApplyDelete(physicalKey);
        }
    }

    private void CompleteIndexBootstrap()
    {
        lock (_indexMutationGate)
        {
            foreach (var mutation in _pendingIndexMutations)
            {
                if (mutation.Value.IsDelete)
                {
                    ApplyDelete(mutation.Key);
                }
                else if (mutation.Value.Entity is not null)
                {
                    UpdateIndexes(mutation.Key, mutation.Value.Entity);
                }
            }

            _pendingIndexMutations.Clear();
            _bufferIndexMutations = 0;
        }
    }

    private void ApplyDelete(string physicalKey)
    {
        _searchIndexes.Remove(physicalKey);
        if (_runtimeTypesByPhysicalKey.TryRemove(physicalKey, out var runtimeType)
            && _runtimeSearchIndexes.TryGetValue(runtimeType, out var runtimeStore))
        {
            runtimeStore.Remove(physicalKey);
        }
        Volatile.Write(ref _persistentCatalogDirty, 1);
    }

    private void UpdateIndexes(string physicalKey, TContract entity)
    {
        _searchIndexes.Upsert(physicalKey, entity);
        var runtimeType = entity.GetType();
        if (_runtimeTypesByPhysicalKey.TryGetValue(physicalKey, out var previousRuntimeType)
            && previousRuntimeType != runtimeType
            && _runtimeSearchIndexes.TryGetValue(previousRuntimeType, out var previousStore))
        {
            previousStore.Remove(physicalKey);
        }

        _runtimeTypesByPhysicalKey[physicalKey] = runtimeType;
        var runtimeStore = GetRuntimeSearchIndexStore(runtimeType);
        if (runtimeStore.HasIndexes)
        {
            runtimeStore.Upsert(physicalKey, entity);
        }

        Volatile.Write(ref _persistentCatalogDirty, 1);
    }

    private IRuntimeSearchIndexStore GetRuntimeSearchIndexStore(Type runtimeType)
        => _runtimeSearchIndexes.GetOrAdd(
            runtimeType,
            type => new RuntimeSearchIndexStore(type, _plannerOptions));

    private static Func<TInput, bool> GetCompiledPredicate<TInput>(Expression<Func<TInput, bool>> predicate)
        => SearchPredicateCompiler<TInput>.Compile(predicate);

    private bool TryLoadPersistentCatalog(
        SearchIndexStorageIdentity storageIdentity,
        CancellationToken cancellationToken)
    {
        var path = _persistentCatalogPath;
        if (path is null)
        {
            return false;
        }

        var records = PersistentSearchIndexCatalog.TryLoad(
            path,
            storageIdentity,
            _searchIndexes.SchemaFingerprint,
            _searchIndexes.ValueTypes,
            ResolveRuntimeCatalogSchema);
        if (records is null)
        {
            return false;
        }

        _searchIndexes.Clear();
        _runtimeTypesByPhysicalKey.Clear();
        using var contractBulkLoad = _searchIndexes.BeginBulkLoad();
        var runtimeBulkLoads = new Dictionary<Type, IDisposable>();
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtimeType = record.RuntimeType;
                if (!typeof(TContract).IsAssignableFrom(runtimeType))
                {
                    _searchIndexes.Clear();
                    _runtimeTypesByPhysicalKey.Clear();
                    return false;
                }

                _searchIndexes.UpsertValues(record.PhysicalKey, record.ContractValues);
                var runtimeStore = GetRuntimeSearchIndexStore(runtimeType);
                if (!runtimeBulkLoads.ContainsKey(runtimeType))
                {
                    runtimeBulkLoads.Add(runtimeType, runtimeStore.BeginBulkLoad());
                }

                runtimeStore.UpsertValues(record.PhysicalKey, record.RuntimeValues);
                _runtimeTypesByPhysicalKey[record.PhysicalKey] = runtimeType;
            }
        }
        finally
        {
            DisposeBulkLoads(runtimeBulkLoads.Values);
        }

        Volatile.Write(ref _persistentCatalogDirty, 0);
        return true;
    }

    private static void DisposeBulkLoads(IEnumerable<IDisposable> bulkLoads)
    {
        Exception? completionException = null;
        foreach (var bulkLoad in bulkLoads)
        {
            try
            {
                bulkLoad.Dispose();
            }
            catch (Exception exception)
            {
                completionException ??= exception;
            }
        }

        if (completionException is not null)
        {
            ExceptionDispatchInfo.Capture(completionException).Throw();
        }
    }

    private RuntimeSearchIndexCatalogSchema? ResolveRuntimeCatalogSchema(string runtimeTypeName)
    {
        var runtimeType = Type.GetType(runtimeTypeName, throwOnError: false);
        if (runtimeType is null || !typeof(TContract).IsAssignableFrom(runtimeType))
        {
            return null;
        }

        var store = GetRuntimeSearchIndexStore(runtimeType);
        return new RuntimeSearchIndexCatalogSchema(runtimeType, store.SchemaFingerprint, store.ValueTypes);
    }

    private async ValueTask TryPersistCatalogAsync(
        CancellationToken cancellationToken,
        SearchIndexStorageIdentity? expectedStorageIdentity = null)
    {
        var repository = _persistentCatalogRepository;
        var path = _persistentCatalogPath;
        if (repository is null || path is null)
        {
            return;
        }

        try
        {
            var initialStorageIdentity = expectedStorageIdentity ?? await repository
                .GetSearchIndexStorageIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            var contractValues = _searchIndexes.GetValuesSnapshot();
            var records = new List<SearchIndexCatalogRecord>(_runtimeTypesByPhysicalKey.Count);
            foreach (var pair in _runtimeTypesByPhysicalKey.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtimeStore = GetRuntimeSearchIndexStore(pair.Value);
                var baseValues = contractValues.TryGetValue(pair.Key, out var values)
                    ? values
                    : Array.Empty<object?>();
                var runtimeValues = runtimeStore.GetValues(pair.Key) ?? Array.Empty<object?>();
                records.Add(new SearchIndexCatalogRecord(
                    pair.Key,
                    pair.Value.AssemblyQualifiedName ?? pair.Value.FullName ?? pair.Value.Name,
                    baseValues,
                    runtimeStore.SchemaFingerprint.ToArray(),
                    runtimeStore.ValueTypes,
                    runtimeValues));
            }

            var finalStorageIdentity = await repository
                .GetSearchIndexStorageIdentityAsync(cancellationToken)
                .ConfigureAwait(false);
            if (initialStorageIdentity.EntryCount != records.Count
                || finalStorageIdentity.EntryCount != records.Count
                || initialStorageIdentity.EntryCount != finalStorageIdentity.EntryCount
                || !initialStorageIdentity.Fingerprint.Span.SequenceEqual(
                    finalStorageIdentity.Fingerprint.Span))
            {
                return;
            }

            await PersistentSearchIndexCatalog.WriteAsync(
                path,
                finalStorageIdentity,
                _searchIndexes.SchemaFingerprint,
                _searchIndexes.ValueTypes,
                records,
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _persistentCatalogDirty, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or OverflowException
                                          or System.Security.Cryptography.CryptographicException
                                          or System.Text.Json.JsonException)
        {
            // The catalog is a rebuildable acceleration structure. Storage remains authoritative.
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_initializationGate)
        {
            if (_disposalTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposalTask = DisposeCoreAsync(_initializationTask);
            }

            disposal = _disposalTask;
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(Task? initialization)
    {
        if (initialization is not null)
        {
            try
            {
                await initialization.ConfigureAwait(false);
            }
            catch
            {
                // Initialization failure remains observable to its callers; disposal still owns cleanup.
            }
        }

        if (Volatile.Read(ref _initialized) != 0
            && Volatile.Read(ref _persistentCatalogDirty) != 0)
        {
            await TryPersistCatalogAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _storedSubscription?.Dispose();
        _deletedSubscription?.Dispose();

        lock (_indexMutationGate)
        {
            _pendingIndexMutations.Clear();
            _searchIndexes.Dispose();
            foreach (var runtimeIndex in _runtimeSearchIndexes.Values)
            {
                runtimeIndex.Dispose();
            }

            _runtimeSearchIndexes.Clear();
            _runtimeTypesByPhysicalKey.Clear();
        }
    }

    private readonly record struct PendingIndexMutation(TContract? Entity, bool IsDelete);

    private interface IRuntimeSearchIndexStore : IDisposable
    {
        bool HasIndexes { get; }

        ReadOnlyMemory<byte> SchemaFingerprint { get; }

        IReadOnlyList<Type> ValueTypes { get; }

        IDisposable BeginBulkLoad();

        void Upsert(string physicalKey, object entity);

        void Remove(string physicalKey);

        void UpsertValues(string physicalKey, IReadOnlyList<object?> values);

        object?[]? GetValues(string physicalKey);

        HashSet<string>? TryEvaluate(LambdaExpression expression);
    }

    private sealed class RuntimeSearchIndexStore : IRuntimeSearchIndexStore
    {
        private readonly object _store;
        private readonly Action<string, object> _upsert;
        private readonly Action<string> _remove;
        private readonly Func<IDisposable> _beginBulkLoad;
        private readonly Action<string, IReadOnlyList<object?>> _upsertValues;
        private readonly Func<string, object?[]?> _getValues;
        private readonly Func<LambdaExpression, HashSet<string>?> _tryEvaluate;

        public bool HasIndexes { get; }

        public ReadOnlyMemory<byte> SchemaFingerprint { get; }

        public IReadOnlyList<Type> ValueTypes { get; }

        public RuntimeSearchIndexStore(Type runtimeType, SearchQueryPlannerOptions plannerOptions)
        {
            var storeType = typeof(SearchIndexStore<>).MakeGenericType(runtimeType);
            _store = Activator.CreateInstance(storeType, plannerOptions)
                ?? throw new InvalidOperationException($"Unable to create search index store for '{runtimeType.FullName}'.");

            const System.Reflection.BindingFlags methodFlags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            var upsert = storeType.GetMethod(nameof(SearchIndexStore<TContract>.Upsert), methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose Upsert.");
            var remove = storeType.GetMethod(nameof(SearchIndexStore<TContract>.Remove), methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose Remove.");
            var tryEvaluate = storeType.GetMethod(nameof(SearchIndexStore<TContract>.TryEvaluate), methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose TryEvaluate.");
            var hasIndexes = storeType.GetProperty(nameof(SearchIndexStore<TContract>.HasIndexes))
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' does not expose HasIndexes.");

            var schemaFingerprint = storeType.GetProperty(
                nameof(SearchIndexStore<TContract>.SchemaFingerprint),
                methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' has no schema fingerprint.");
            var valueTypes = storeType.GetProperty(
                nameof(SearchIndexStore<TContract>.ValueTypes),
                methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' has no value types.");
            var upsertValues = storeType.GetMethod(
                nameof(SearchIndexStore<TContract>.UpsertValues),
                methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' has no persisted-value import.");
            var beginBulkLoad = storeType.GetMethod(
                nameof(SearchIndexStore<TContract>.BeginBulkLoad),
                methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' has no bulk-load scope.");
            var getValues = storeType.GetMethod(
                nameof(SearchIndexStore<TContract>.GetValues),
                methodFlags)
                ?? throw new InvalidOperationException($"Search index store for '{runtimeType.FullName}' has no persisted-value export.");

            HasIndexes = (bool)(hasIndexes.GetValue(_store) ?? false);
            SchemaFingerprint = (ReadOnlyMemory<byte>)(schemaFingerprint.GetValue(_store)
                ?? ReadOnlyMemory<byte>.Empty);
            ValueTypes = (IReadOnlyList<Type>)(valueTypes.GetValue(_store)
                ?? Array.Empty<Type>());
            _upsert = CompileUpsert(storeType, runtimeType, upsert, _store);
            _remove = CompileRemove(storeType, remove, _store);
            _tryEvaluate = CompileTryEvaluate(storeType, tryEvaluate, _store);
            _beginBulkLoad = CompileBeginBulkLoad(storeType, beginBulkLoad, _store);
            _upsertValues = CompileUpsertValues(storeType, upsertValues, _store);
            _getValues = CompileGetValues(storeType, getValues, _store);
        }

        public void Upsert(string physicalKey, object entity)
            => _upsert(physicalKey, entity);

        public void Remove(string physicalKey)
            => _remove(physicalKey);

        public IDisposable BeginBulkLoad()
            => _beginBulkLoad();

        public void UpsertValues(string physicalKey, IReadOnlyList<object?> values)
            => _upsertValues(physicalKey, values);

        public object?[]? GetValues(string physicalKey) => _getValues(physicalKey);

        public HashSet<string>? TryEvaluate(LambdaExpression expression)
            => _tryEvaluate(expression);

        public void Dispose()
        {
            if (_store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private static Action<string, object> CompileUpsert(
            Type storeType,
            Type runtimeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var key = Expression.Parameter(typeof(string), "key");
            var entity = Expression.Parameter(typeof(object), "entity");
            var call = Expression.Call(
                Expression.Constant(store, storeType),
                method,
                key,
                Expression.Convert(entity, runtimeType));
            return Expression.Lambda<Action<string, object>>(call, key, entity).Compile();
        }

        private static Action<string> CompileRemove(
            Type storeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var key = Expression.Parameter(typeof(string), "key");
            var call = Expression.Call(Expression.Constant(store, storeType), method, key);
            return Expression.Lambda<Action<string>>(call, key).Compile();
        }

        private static Func<LambdaExpression, HashSet<string>?> CompileTryEvaluate(
            Type storeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var expression = Expression.Parameter(typeof(LambdaExpression), "expression");
            var parameterType = method.GetParameters()[0].ParameterType;
            var call = Expression.Call(
                Expression.Constant(store, storeType),
                method,
                Expression.Convert(expression, parameterType));
            return Expression.Lambda<Func<LambdaExpression, HashSet<string>?>>(call, expression).Compile();
        }

        private static Action<string, IReadOnlyList<object?>> CompileUpsertValues(
            Type storeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var key = Expression.Parameter(typeof(string), "key");
            var values = Expression.Parameter(typeof(IReadOnlyList<object?>), "values");
            var call = Expression.Call(Expression.Constant(store, storeType), method, key, values);
            return Expression.Lambda<Action<string, IReadOnlyList<object?>>>(call, key, values).Compile();
        }

        private static Func<IDisposable> CompileBeginBulkLoad(
            Type storeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var call = Expression.Call(Expression.Constant(store, storeType), method);
            return Expression.Lambda<Func<IDisposable>>(call).Compile();
        }

        private static Func<string, object?[]?> CompileGetValues(
            Type storeType,
            System.Reflection.MethodInfo method,
            object store)
        {
            var key = Expression.Parameter(typeof(string), "key");
            var call = Expression.Call(Expression.Constant(store, storeType), method, key);
            return Expression.Lambda<Func<string, object?[]?>>(call, key).Compile();
        }
    }
}
