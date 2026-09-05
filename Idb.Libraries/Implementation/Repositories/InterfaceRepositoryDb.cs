using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Repository facade that delegates persistence operations to the injected <see cref="IIndexManager{TContract}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class InterfaceRepositoryDb<TContract> : IEnvelopeMetadataRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IIndexManager<TContract> _indexManager;
    private readonly ContractRepositoryType _repositoryType;
    private readonly StoragePolicy _storagePolicy;
    private readonly IEnvelopeMetadataStager<TContract> _envelopeMetadataStager;
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;

    public InterfaceRepositoryDb(
        IIndexManager<TContract> indexManager,
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy,
        IEnvelopeLifecycle<TContract> envelopeLifecycle,
        IEnvelopeRepository<TContract> envelopeRepository,
        StorageDurabilityMode commitDurability)
    {
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        if (contractConfig is null)
        {
            throw new ArgumentNullException(nameof(contractConfig));
        }

        _repositoryType = contractConfig.RepositoryType is ContractRepositoryType.None
            ? ContractRepositoryType.FileSystem
            : contractConfig.RepositoryType;
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        ArgumentNullException.ThrowIfNull(envelopeLifecycle);
        _envelopeMetadataStager = envelopeLifecycle as IEnvelopeMetadataStager<TContract>
            ?? throw new InvalidOperationException(
                $"The configured envelope lifecycle for '{typeof(TContract).FullName}' does not support internal metadata staging.");
        _envelopeRepository = envelopeRepository ?? throw new ArgumentNullException(nameof(envelopeRepository));
        if (!Enum.IsDefined(commitDurability))
        {
            throw new ArgumentOutOfRangeException(nameof(commitDurability));
        }

        CommitDurability = commitDurability;

        var contractType = typeof(TContract);
        if (!contractType.IsInterface)
        {
            throw new InvalidOperationException($"InterfaceDb requires TContract to be an interface. Found '{contractType.FullName}'.");
        }

        if (!Attribute.IsDefined(contractType, typeof(StorageScopeAttribute), inherit: false))
        {
            throw new InvalidOperationException($"Contract '{contractType.FullName}' must be decorated with [StorageScope].");
        }
    }

    public ContractRepositoryType RepositoryType => _repositoryType;

    public StorageDurabilityMode CommitDurability { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _indexManager.InitializeAsync(cancellationToken);

    public ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
    {
        return _indexManager.SaveAsync(entityToSave, cancellationToken);
    }

    public async ValueTask<TContract> SaveWithExtensionMetadataAsync(
        TContract detachedEntity,
        string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detachedEntity);
        if (detachedEntity.Envelope is not null)
        {
            throw new InvalidOperationException(
                "Metadata-aware saves require a detached entity without an envelope.");
        }

        if (extensionMetadata?.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength)
        {
            throw new ArgumentException(
                $"Envelope extension metadata cannot exceed {EnvelopeExtensionMetadataLimits.MaximumByteLength} bytes.",
                nameof(extensionMetadata));
        }

        if (existingEnvelopeCacheKey is not null)
        {
            if (string.IsNullOrWhiteSpace(existingEnvelopeCacheKey))
            {
                throw new ArgumentException(
                    "An existing envelope cache key cannot be empty or whitespace.",
                    nameof(existingEnvelopeCacheKey));
            }

            await _envelopeRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var existingEnvelope = await _envelopeRepository
                .GetPhysicalAsync(existingEnvelopeCacheKey, cancellationToken)
                .ConfigureAwait(false);
            if (existingEnvelope is null
                || existingEnvelope.State != StorageObjectState.Active
                || !string.Equals(
                    existingEnvelope.EnvelopeCacheKey,
                    existingEnvelopeCacheKey,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"No active storage object exists for envelope cache key '{existingEnvelopeCacheKey}'.");
            }
        }

        var stagedEnvelope = _envelopeMetadataStager.Stage(
            detachedEntity,
            existingEnvelopeCacheKey,
            extensionMetadata);
        try
        {
            return await _indexManager.SaveAsync(detachedEntity, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Wrapping detaches the candidate before persistence. Reattach the private staged
            // identity on failure so an ambiguous insert/update outcome cannot be retried as a
            // fresh object accidentally.
            if (detachedEntity.Envelope is null)
            {
                StoredContractEnvelopeAccessor<TContract>.SetEnvelope(detachedEntity, stagedEnvelope);
            }

            throw;
        }
    }

    public async ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entitiesToSave,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitiesToSave);

        var materialized = entitiesToSave as IReadOnlyList<TContract> ?? entitiesToSave.ToArray();
        if (materialized.Count == 0)
        {
            return Array.Empty<TContract>();
        }

        return await _indexManager.SaveManyAsync(materialized, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DeleteAsync(
        TContract entityToDelete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityToDelete);
        return _indexManager.DeleteAsync(entityToDelete, cancellationToken);
    }

    public async ValueTask<TContract> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ?? null!;

    public async ValueTask<TImplementation> QueryAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ?? null!;

    public IAsyncEnumerable<TContract> QueryManyAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return _indexManager.QueryAsync(filterFunction, cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> QueryManyAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return _indexManager.QueryAsync(filterFunction, cancellationToken);
    }

    public IAsyncEnumerable<TContract> SearchManyAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return _indexManager.SearchAsync(filterExpression, cancellationToken);
    }

    public IAsyncEnumerable<TImplementation> SearchManyAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(filterExpression);
        return _indexManager.SearchAsync(filterExpression, cancellationToken);
    }

    public IAsyncEnumerable<TContract> QueryManyByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        return _indexManager.QueryByStorageKeyAsync(storageKey, cancellationToken);
    }

    public async ValueTask<TContract?> QueryFirstAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        await foreach (var match in QueryManyAsync(filterFunction, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null;
    }

    public async ValueTask<TImplementation?> QueryFirstAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        await foreach (var match in QueryManyAsync(filterFunction, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null;
    }

    public async ValueTask<TContract?> SearchFirstAsync(
        Expression<Func<TContract, bool>> filterExpression,
        CancellationToken cancellationToken = default)
    {
        await foreach (var match in SearchManyAsync(filterExpression, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null;
    }

    public async ValueTask<TImplementation?> SearchFirstAsync<TImplementation>(
        Expression<Func<TImplementation, bool>> filterExpression,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        await foreach (var match in SearchManyAsync(filterExpression, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null;
    }

    public async ValueTask<TContract?> QueryFirstByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        await foreach (var match in QueryManyByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false))
        {
            return match;
        }

        return null;
    }

    public async ValueTask<TContract> QuerySingleAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null)
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                noneExceptionFactory,
                RepositoryQueryCardinalityException.NoMatches);
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TImplementation> QuerySingleAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? noneExceptionFactory = null,
        Func<Exception>? multipleExceptionFactory = null)
        where TImplementation : class, TContract
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                noneExceptionFactory,
                RepositoryQueryCardinalityException.NoMatches);
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TContract?> QuerySingleOrDefaultAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public async ValueTask<TImplementation?> QuerySingleOrDefaultAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default,
        Func<Exception>? multipleExceptionFactory = null)
        where TImplementation : class, TContract
    {
        await using var enumerator = QueryManyAsync(filterFunction, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }

        var single = enumerator.Current;
        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw CreateException(
                multipleExceptionFactory,
                RepositoryQueryCardinalityException.MultipleMatches);
        }

        return single;
    }

    public ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
    {
        if (entityToCheck is null)
        {
            throw new ArgumentNullException(nameof(entityToCheck));
        }

        return _indexManager.ExistsAsync(entityToCheck, cancellationToken);
    }

    private static Exception CreateException(
        Func<Exception>? exceptionFactory,
        Func<Exception> defaultFactory)
    {
        var exception = (exceptionFactory ?? defaultFactory)();
        return exception
            ?? throw new InvalidOperationException("A repository query exception factory returned null.");
    }
}
