using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Pass-through implementation that forwards operations directly to the envelope repository.
/// Responsible for wrapping contracts into envelopes when persisting (clearing the contract's envelope reference) and unwrapping them on reads (restoring the reference and clearing the envelope) to avoid circular references.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the manager.</typeparam>
public sealed class PassthroughIndexManager<TContract> : IIndexManager<TContract>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    private readonly IEnvelopeRepository<TContract, FileInfo> _envelopeRepository;

    public PassthroughIndexManager(IEnvelopeRepository<TContract, FileInfo> envelopeRepository)
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
        if (envelope is null)
        {
            return new ValueTask<bool>(false);
        }

        return new ValueTask<bool>(_envelopeRepository.ContainsEnvelope(envelope.CacheKey));
    }

    public bool TryGetExisting(TContract entity, out TContract? existing)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        existing = null;
        var envelope = entity.Envelope;
        if (envelope is null)
        {
            return false;
        }

        if (!_envelopeRepository.TryGetEnvelope(envelope.CacheKey, out var cachedEnvelope) ||
            cachedEnvelope is null)
        {
            return false;
        }

        existing = EnvelopeLifecycle.Unwrap(cachedEnvelope);
        return true;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _envelopeRepository.InitializeAsync(cancellationToken);

    public IAsyncEnumerable<TContract> QueryAsync(
        Func<TContract, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        return QueryInternalAsync(predicate, cancellationToken);
    }

    private async IAsyncEnumerable<TContract> QueryInternalAsync(
        Func<TContract, bool> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var envelope in _envelopeRepository.QueryAsync(predicate, cancellationToken).ConfigureAwait(false))
        {
            yield return EnvelopeLifecycle.Unwrap(envelope);
        }
    }

    public ValueTask<TContract> SaveAsync(TContract entity, CancellationToken cancellationToken = default)
    {
        if (entity is null)
        {
            throw new ArgumentNullException(nameof(entity));
        }

        return SaveInternalAsync(entity, cancellationToken);
    }

    private async ValueTask<TContract> SaveInternalAsync(
        TContract entity,
        CancellationToken cancellationToken)
    {
        var envelope = await EnvelopeLifecycle
            .WrapAsync(entity, wrapped => _envelopeRepository.WriteAsync(wrapped, cancellationToken))
            .ConfigureAwait(false);

        var persisted = EnvelopeLifecycle.Unwrap(envelope);
        return persisted;
    }
}



