using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// No-index implementation that forwards persistence to the envelope repository and scans for queries.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the manager.</typeparam>
public sealed class PassthroughIndexManager<TContract> : IIndexManager<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;

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

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _envelopeRepository.InitializeAsync(cancellationToken);

    public ValueTask<bool> DeleteAsync(
        TContract entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _envelopeRepository.DeleteAsync(entity, cancellationToken);
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
        return QueryInternalAsync(predicate.Compile(), cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> SearchAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> predicate,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryInternalAsync(predicate.Compile(), cancellationToken);
    }

    public SearchQueryPlanExplanation ExplainSearch(Expression<Func<TContract, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new SearchQueryPlanExplanation(
            "FullScan: no search indexes are configured.",
            UsesIndexes: false,
            EstimatedCandidateCount: null,
            EstimatedCost: double.PositiveInfinity,
            ResidualPredicateCount: 1);
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
        ArgumentNullException.ThrowIfNull(entity);
        return _envelopeRepository.WriteAsync(entity, cancellationToken);
    }

    public ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return _envelopeRepository.WriteManyAsync(entities, cancellationToken);
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
}
