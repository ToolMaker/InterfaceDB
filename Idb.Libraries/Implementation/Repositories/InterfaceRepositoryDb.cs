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
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Repository facade that delegates persistence operations to the injected <see cref="IIndexManager{TContract}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface serviced by this repository.</typeparam>
public sealed class InterfaceRepositoryDb<TContract> : IEnvelopeMetadataRepository<TContract>, IRepositoryNotifications<TContract>, IRevisionEditingRepository<TContract>, IDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IIndexManager<TContract> _indexManager;
    private readonly ContractRepositoryType _repositoryType;
    private readonly StoragePolicy _storagePolicy;
    private readonly IEnvelopeMetadataStager<TContract> _envelopeMetadataStager;
    private readonly IEnvelopeRepository<TContract> _envelopeRepository;
    private readonly RepositoryNotificationHub<TContract>? _notifications;
    private readonly Func<TContract, CancellationToken, ValueTask<TContract>>? _observedSave;
    private readonly Func<IEnumerable<TContract>, CancellationToken, ValueTask<IReadOnlyList<TContract>>>? _observedBatch;
    private readonly Func<TContract, CancellationToken, ValueTask<bool>>? _observedDelete;

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

        RunningVersion = contractConfig.ActiveVersion;
        _repositoryType = contractConfig.RepositoryType is ContractRepositoryType.None
            ? ContractRepositoryType.FileSystem
            : contractConfig.RepositoryType;
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        if (Idb.Diagnostics.DatabaseTelemetryOperation.IsEnabled(storagePolicy.Telemetry))
            _indexManager = new Idb.Diagnostics.TelemetryIndexManager<TContract>(_indexManager, storagePolicy.Telemetry!);
        ArgumentNullException.ThrowIfNull(envelopeLifecycle);
        _envelopeMetadataStager = envelopeLifecycle as IEnvelopeMetadataStager<TContract>
            ?? throw new InvalidOperationException(
                $"The configured envelope lifecycle for '{typeof(TContract).FullName}' does not support internal metadata staging.");
        _envelopeRepository = envelopeRepository ?? throw new ArgumentNullException(nameof(envelopeRepository));
        if (storagePolicy.RepositoryNotifications is { } notificationOptions)
        {
            _notifications = new(envelopeRepository, notificationOptions, _indexManager.InitializeAsync, storagePolicy.Telemetry);
            _observedSave = _indexManager.SaveAsync;
            _observedBatch = _indexManager.SaveManyAsync;
            _observedDelete = _indexManager.DeleteAsync;
        }
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
    public string? RunningVersion { get; }

    public RetainedVersionCapability RetainedVersions => (_envelopeRepository as IRetainedVersionStore<TContract>)?.RetainedVersions
        ?? new(false, "This provider does not support retained editing.");
    public RetainedVersionStatistics RetentionStatistics => (_envelopeRepository as IRetainedVersionStore<TContract>)?.RetentionStatistics
        ?? new(0, 0, 0, 0, 0, 0, 0);
    public async ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey, CancellationToken cancellationToken = default)
    {
        if (_envelopeRepository is not IRetainedVersionStore<TContract> editing || !editing.RetainedVersions.Supported)
            throw new NotSupportedException(RetainedVersions.Limitation);
        await _indexManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await editing.BeginEditAsync(physicalKey, SaveAsync, cancellationToken).ConfigureAwait(false);
    }

    public StorageDurabilityMode CommitDurability { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _indexManager.InitializeAsync(cancellationToken);

    public ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
    {
        return _notifications is null
            ? _indexManager.SaveAsync(entityToSave, cancellationToken)
            : _notifications.SaveAsync(entityToSave, _observedSave!, cancellationToken);
    }

    public ValueTask<TContract> SaveWithExtensionMetadataAsync(
        TContract detachedEntity,
        string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata,
        CancellationToken cancellationToken = default)
        => existingEnvelopeCacheKey is not null
            ? throw new NotSupportedException("Detached updates require an explicit expected revision. Use the EnvelopeWriteCondition overload.")
            : SaveWithExtensionMetadataAsync(detachedEntity, null, extensionMetadata, new EnvelopeWriteCondition(0), cancellationToken);

    public async ValueTask<TContract> SaveWithExtensionMetadataAsync(TContract detachedEntity, string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata, EnvelopeWriteCondition condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(detachedEntity);
        cancellationToken.ThrowIfCancellationRequested();
        if (detachedEntity.Envelope is not null) throw new InvalidOperationException("Metadata-aware saves require a detached entity.");
        IObjectEnvelope<TContract>? existing = null;
        if (existingEnvelopeCacheKey is not null && !condition.ExpectedMissing)
        {
            await _envelopeRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (_envelopeRepository.TryGet(existingEnvelopeCacheKey, out var group))
                existing = group.FirstOrDefault(item => item.EnvelopeCacheKey == existingEnvelopeCacheKey);
            existing ??= await _envelopeRepository.GetPhysicalAsync(existingEnvelopeCacheKey, cancellationToken).ConfigureAwait(false);
            if (existing is null) throw new InvalidOperationException("No active object exists for the supplied physical key.");
        }
        var staged = _envelopeMetadataStager.Stage(detachedEntity, existingEnvelopeCacheKey, extensionMetadata);
        if (existing is not null) staged.Location = existing.Location;
        EnvelopeWriteCoordinator<TContract>.SetCondition(staged, condition);
        return await SaveAsync(detachedEntity, cancellationToken).ConfigureAwait(false);
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

        return _notifications is null
            ? await _indexManager.SaveManyAsync(materialized, cancellationToken).ConfigureAwait(false)
            : await _notifications.SaveManyAsync(materialized, _observedBatch!, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> DeleteAsync(
        TContract entityToDelete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityToDelete);
        return _notifications is null
            ? _indexManager.DeleteAsync(entityToDelete, cancellationToken)
            : _notifications.DeleteAsync(entityToDelete, _observedDelete!, cancellationToken);
    }

    public async ValueTask<TContract?> QueryAsync(
        Func<TContract, bool> filterFunction,
        CancellationToken cancellationToken = default)
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ;

    public async ValueTask<TImplementation?> QueryAsync<TImplementation>(
        Func<TImplementation, bool> filterFunction,
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
        => await QueryFirstAsync(filterFunction, cancellationToken).ConfigureAwait(false) ;

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

    public IRepositorySubscription SubscribeCreated(Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RequiredNotifications.Subscribe(RepositoryEventKind.Created, (entity, _) => handler(entity), filter, lifetime, keepAlive, receiveOwnWrites);
    }

    public IRepositorySubscription SubscribeChanged(Func<RepositoryChange<TContract>, ValueTask> handler,
        Expression<Func<TContract, bool>> filter, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
    {
        ArgumentNullException.ThrowIfNull(handler); ArgumentNullException.ThrowIfNull(filter);
        return RequiredNotifications.Subscribe(RepositoryEventKind.Changed,
            (entity, names) => handler(new(entity, names)), filter, lifetime, keepAlive, receiveOwnWrites);
    }

    public IRepositorySubscription SubscribeDeleted(Func<TContract, ValueTask> handler,
        Expression<Func<TContract, bool>>? filter = null, CancellationToken lifetime = default,
        Func<ValueTask<bool>>? keepAlive = null, bool receiveOwnWrites = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RequiredNotifications.Subscribe(RepositoryEventKind.Deleted, (entity, _) => handler(entity), filter, lifetime, keepAlive, receiveOwnWrites);
    }

    private RepositoryNotificationHub<TContract> RequiredNotifications => _notifications
        ?? throw new InvalidOperationException("Enable StoragePolicy.RepositoryNotifications before registering repository subscribers.");

    public void Dispose() => _notifications?.Dispose();

    private static Exception CreateException(
        Func<Exception>? exceptionFactory,
        Func<Exception> defaultFactory)
    {
        var exception = (exceptionFactory ?? defaultFactory)();
        return exception
            ?? throw new InvalidOperationException("A repository query exception factory returned null.");
    }
}
