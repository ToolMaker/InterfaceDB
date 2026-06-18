using System;
using System.Threading;
using System.Threading.Tasks;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Repository facade that delegates persistence operations to the injected <see cref="IIndexManager{TContract}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class InterfaceRepositryDb<TContract> : IRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IIndexManager<TContract> _indexManager;
    private readonly ContractRepositoryType _repositoryType;
    private readonly StoragePolicy _storagePolicy;

    public InterfaceRepositryDb(
        IIndexManager<TContract> indexManager,
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy)
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

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _indexManager.InitializeAsync(cancellationToken);

    public ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
    {
        return _indexManager.SaveAsync(entityToSave, cancellationToken);
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

    public IAsyncEnumerable<TContract> QueryManyAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filterFunction);
        return _indexManager.QueryAsync(filterFunction, cancellationToken);
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
