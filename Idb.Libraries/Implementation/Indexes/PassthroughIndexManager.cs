using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Pass-through implementation that forwards persistence to the envelope repository and owns in-memory search indexes.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the manager.</typeparam>
public sealed class PassthroughIndexManager<TContract> : IIndexManager<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;
    private readonly SearchIndexStore<TContract> _searchIndexes = new();
    private IContractSubscription? _storedSubscription;
    private IContractSubscription? _deletedSubscription;
    private int _subscriptionsStarted;

    public PassthroughIndexManager(IEnvelopeRepository<TContract> envelopeRepository)
    {
        _envelopeRepository = envelopeRepository ?? throw new ArgumentNullException(nameof(envelopeRepository));
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

    public IAsyncEnumerable<TContract> SearchAsync(
        Expression<Func<TContract, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryExpressionInternalAsync(predicate, cancellationToken);
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
            var entity = envelope.ObjectInstance
                ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
            if (!predicate(entity))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
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
            var entity = envelope?.ObjectInstance;
            if (entity is null || !compiled(entity))
            {
                continue;
            }

            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
        }
    }

    private async IAsyncEnumerable<TContract> QueryByStorageKeyInternalAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in _envelopeRepository.GetAsync(storageKey, cancellationToken).ConfigureAwait(false))
        {
            var entity = envelope.ObjectInstance
                ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            yield return entity;
        }
    }

    private async ValueTask<TContract> SaveInternalAsync(
        TContract entity,
        CancellationToken cancellationToken)
    {
        var saved = await _envelopeRepository.WriteAsync(entity, cancellationToken).ConfigureAwait(false);
        UpdateIndex(saved);
        return saved;
    }

    private async ValueTask<IReadOnlyList<TContract>> SaveManyInternalAsync(
        IEnumerable<TContract> entities,
        CancellationToken cancellationToken)
    {
        var saved = await _envelopeRepository.WriteManyAsync(entities, cancellationToken).ConfigureAwait(false);
        foreach (var entity in saved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateIndex(entity);
        }

        return saved;
    }

    private async ValueTask RebuildIndexesAsync(CancellationToken cancellationToken)
    {
        _searchIndexes.Clear();
        if (!_searchIndexes.HasIndexes)
        {
            return;
        }

        await foreach (var envelope in _envelopeRepository.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (envelope.ObjectInstance is not null)
            {
                _searchIndexes.Upsert(envelope.EnvelopeCacheKey, envelope.ObjectInstance);
            }
        }
    }

    private void StartIndexSubscriptions()
    {
        if (!_searchIndexes.HasIndexes || Interlocked.Exchange(ref _subscriptionsStarted, 1) != 0)
        {
            return;
        }

        _storedSubscription = _envelopeRepository.SubscribeToStored(envelope =>
        {
            if (envelope.ObjectInstance is not null)
            {
                _searchIndexes.Upsert(envelope.EnvelopeCacheKey, envelope.ObjectInstance);
            }

            return ValueTask.CompletedTask;
        });
        _deletedSubscription = _envelopeRepository.SubscribeToDeleted(envelope =>
        {
            _searchIndexes.Remove(envelope.EnvelopeCacheKey);
            return ValueTask.CompletedTask;
        });
    }

    private void UpdateIndex(TContract entity)
    {
        var envelope = entity.Envelope;
        if (envelope is not null)
        {
            _searchIndexes.Upsert(envelope.EnvelopeCacheKey, entity);
        }
    }
}
