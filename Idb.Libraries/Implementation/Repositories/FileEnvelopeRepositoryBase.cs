using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extentions;
using Idb.Libraries.Implementation.Serialization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Shared file-system backed envelope repository implementation used by both flat and sharded layouts.
/// </summary>
public abstract class FileEnvelopeRepositoryBase<TContract> : IEnvelopeRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly Func<IObjectEnvelope<TContract>> _envelopeFactory;
    private readonly string _dataRoot;
    private readonly IMigrationStrategy<TContract>? _migrationStrategy;
    private readonly IReaderAndWriter<TContract> _readerAndWriter;
    private readonly IEnvelopeStorageChangeSource<TContract>? _storageChangeSource;
    private readonly IEnvelopeStorageChangeHandler<TContract>? _storageChangeHandler;
    private readonly IFileWriteCoordinator _fileWriteCoordinator;
    private readonly StoragePolicy _storagePolicy;
    private Func<EnvelopeStorageChange, ValueTask>? _storageChangeSubscription;
    private int _storageChangeSourceStarted;

    private readonly IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> _envelopes;
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _addedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _removedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _storedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _deletedSubscriptions = new();

    protected FileEnvelopeRepositoryBase(
        IDbContractConfigDto<TContract> contractConfig,
        IReaderAndWriter<TContract> readerAndWriter,
        IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes,
        IFileWriteCoordinator fileWriteCoordinator,
        StoragePolicy storagePolicy,
        Func<IObjectEnvelope<TContract>> envelopeFactory,
        IMigrationStrategy<TContract>? migrationStrategy = null,
        IEnvelopeStorageChangeSource<TContract>? storageChangeSource = null,
        IEnvelopeStorageChangeHandler<TContract>? storageChangeHandler = null)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _migrationStrategy = migrationStrategy;
        _storageChangeSource = storageChangeSource;
        _storageChangeHandler = storageChangeHandler;
        _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _readerAndWriter = readerAndWriter ?? throw new ArgumentNullException(nameof(readerAndWriter));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));

        if (string.IsNullOrWhiteSpace(_contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _dataRoot = Path.GetFullPath(_contractConfig.DataRoot!);
        Directory.CreateDirectory(_dataRoot);

        var activeVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        var activeVersionRoot = Path.Combine(_dataRoot, activeVersion);
        Directory.CreateDirectory(activeVersionRoot);
    }

    public IReaderAndWriter<TContract> ReaderAndWriter => _readerAndWriter;

    public bool Exists(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        var logicalKey = EnvelopeCacheKey.ToLogicalKey(storageKey);
        return _envelopes.GetGroupCount(logicalKey) > 0;
    }

    public bool TryGet(string storageKey, out IReadOnlyList<IObjectEnvelope<TContract>> envelopes)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        var logicalKey = EnvelopeCacheKey.ToLogicalKey(storageKey);
        return _envelopes.TryGetGroup(logicalKey, out envelopes);
    }

    public async ValueTask<TContract> WriteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectInstance);

        var isNewStorageObject = objectInstance.Envelope is null;
        var existingLocator = objectInstance.Envelope?.Location;
        if (!string.IsNullOrWhiteSpace(existingLocator))
        {
            _storageChangeSource?.AcknowledgeLocalChange(existingLocator);
        }

        objectInstance = await ReaderAndWriter.Writer.WriteAsync(objectInstance, cancellationToken).ConfigureAwait(false);
        var envelope = objectInstance.Envelope ?? throw new InvalidOperationException("Object instance must have a valid envelope after writing.");
        _storageChangeSource?.AcknowledgeLocalChange(envelope.Location ?? string.Empty);

        var notify = _storagePolicy.RaiseNotifications;
        if (notify)
        {
            await ContractStored(envelope, cancellationToken).ConfigureAwait(false);
            if (isNewStorageObject)
            {
                await ContractAdded(envelope, cancellationToken).ConfigureAwait(false);
            }
        }

        return objectInstance;
    }

    public async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
        IEnumerable<TContract> objectInstances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectInstances);

        var materialized = objectInstances as IReadOnlyList<TContract> ?? objectInstances.ToArray();
        if (materialized.Count == 0)
        {
            return Array.Empty<TContract>();
        }

        var isNew = new bool[materialized.Count];
        for (var index = 0; index < materialized.Count; index++)
        {
            var instance = materialized[index] ?? throw new ArgumentNullException(nameof(objectInstances));
            isNew[index] = instance.Envelope is null;

            var existingLocator = instance.Envelope?.Location;
            if (!string.IsNullOrWhiteSpace(existingLocator))
            {
                _storageChangeSource?.AcknowledgeLocalChange(existingLocator);
            }
        }

        try
        {
            var saved = await ReaderAndWriter.Writer
                .WriteManyAsync(materialized, cancellationToken)
                .ConfigureAwait(false);

            await PublishBatchNotificationsAsync(saved, isNew, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch (StorageBulkOperationException ex)
        {
            await PublishBatchNotificationsForPartialSuccessAsync(materialized, isNew, ex, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> DeleteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectInstance);

        var envelope = objectInstance.Envelope
            ?? throw new StorageObjectNotPersistedException(typeof(TContract));

        if (envelope.State == StorageObjectState.Deleted)
        {
            return false;
        }

        ValidateEnvelopeForDelete(envelope);
        var storageKey = envelope.EnvelopeCacheKey;
        var providerLocator = envelope.Location!;
        _storageChangeSource?.AcknowledgeLocalChange(providerLocator);

        bool removedActiveStorage;
        try
        {
            removedActiveStorage = _storagePolicy.DurabilityMode == StorageDurabilityMode.Performance
                ? DeletePerformance(providerLocator)
                : await _fileWriteCoordinator
                    .DeleteAndCommitAsync(providerLocator, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new StorageConcurrencyException(
                $"Delete could not safely acquire storage coordination for object '{storageKey}'.",
                storageKey,
                providerLocator,
                ex);
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageOperationException(
                StorageOperation.Delete,
                storageKey,
                providerLocator,
                ex);
        }

        if (File.Exists(providerLocator))
        {
            return false;
        }

        envelope.State = StorageObjectState.Deleted;
        _envelopes.Remove(storageKey);

        var notify = _storagePolicy.RaiseNotifications;
        if (removedActiveStorage && notify)
        {
            await ContractDeleted(envelope, cancellationToken).ConfigureAwait(false);
        }

        return removedActiveStorage;
    }

    private async ValueTask PublishBatchNotificationsAsync(
        IReadOnlyList<TContract> saved,
        IReadOnlyList<bool> isNew,
        CancellationToken cancellationToken)
    {
        if (!_storagePolicy.RaiseNotifications)
        {
            return;
        }

        for (var index = 0; index < saved.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = saved[index].Envelope;
            if (envelope is null)
            {
                continue;
            }

            _storageChangeSource?.AcknowledgeLocalChange(envelope.Location ?? string.Empty);
            await ContractStored(envelope, cancellationToken).ConfigureAwait(false);
            if (isNew[index])
            {
                await ContractAdded(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishBatchNotificationsForPartialSuccessAsync(
        IReadOnlyList<TContract> attempted,
        IReadOnlyList<bool> isNew,
        StorageBulkOperationException exception,
        CancellationToken cancellationToken)
    {
        if (!_storagePolicy.RaiseNotifications || exception.SucceededIndexes.Count == 0)
        {
            return;
        }

        foreach (var index in exception.SucceededIndexes.OrderBy(static item => item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < 0 || index >= attempted.Count)
            {
                continue;
            }

            var envelope = attempted[index].Envelope;
            if (envelope is null)
            {
                continue;
            }

            _storageChangeSource?.AcknowledgeLocalChange(envelope.Location ?? string.Empty);
            await ContractStored(envelope, cancellationToken).ConfigureAwait(false);
            if (isNew[index])
            {
                await ContractAdded(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ValidateEnvelopeForDelete(IObjectEnvelope<TContract> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
        {
            throw new InvalidStorageEnvelopeException(
                "The provider storage key is missing.",
                envelope.EnvelopeCacheKey,
                envelope.Location);
        }

        if (string.IsNullOrWhiteSpace(envelope.Location))
        {
            throw new InvalidStorageEnvelopeException(
                "The provider locator is missing.",
                envelope.EnvelopeCacheKey,
                envelope.Location);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(envelope.Location);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidStorageEnvelopeException(
                $"The file provider locator is invalid: {ex.Message}",
                envelope.EnvelopeCacheKey,
                envelope.Location);
        }

        var relativePath = Path.GetRelativePath(_dataRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidStorageEnvelopeException(
                "The file provider locator is outside the configured storage root.",
                envelope.EnvelopeCacheKey,
                envelope.Location);
        }
    }

    private static bool DeletePerformance(string providerLocator)
    {
        if (!File.Exists(providerLocator))
        {
            return false;
        }

        File.Delete(providerLocator);
        return true;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(storageKey));
        }

        if (!TryGet(storageKey, out var envelopes))
        {
            yield break;
        }

        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentEnvelope = await MaterializeEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (currentEnvelope?.ObjectInstance is null)
            {
                continue;
            }

            yield return currentEnvelope;
        }
    }

    public async ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(physicalStorageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(physicalStorageKey));
        }

        return _envelopes.TryGetValue(physicalStorageKey, out var envelope)
            ? await MaterializeEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var envelope in _envelopes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentEnvelope = await MaterializeEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (currentEnvelope?.ObjectInstance is null)
            {
                continue;
            }

            yield return currentEnvelope;
        }
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        if (typeof(TImplementation) == typeof(TContract))
        {
            await foreach (var envelope in EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }

            yield break;
        }

        var expectedTypeName = GetImplementationTypeName(typeof(TImplementation));
        foreach (var envelope in _envelopes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(envelope.ImplementationTypeName)
                && !string.Equals(envelope.ImplementationTypeName, expectedTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            var currentEnvelope = await MaterializeEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (currentEnvelope?.ObjectInstance is not TImplementation)
            {
                continue;
            }

            currentEnvelope.ImplementationTypeName ??= expectedTypeName;
            yield return currentEnvelope;
        }
    }

    private async ValueTask<IObjectEnvelope<TContract>?> MaterializeEnvelopeAsync(
        IObjectEnvelope<TContract>? envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is null)
        {
            return null;
        }

        var currentEnvelope = envelope;
        var instance = currentEnvelope.ObjectInstance;
        if (instance is null
            && _migrationStrategy?.StrategyType == MigrationStrategyType.Lazy
            && !string.Equals(
                currentEnvelope.VersionTag,
                _contractConfig.ActiveVersion,
                StringComparison.OrdinalIgnoreCase))
        {
            instance = await _migrationStrategy
                .MigrateAsync(currentEnvelope, cancellationToken)
                .ConfigureAwait(false);
            if (instance?.Envelope is not null)
            {
                currentEnvelope = instance.Envelope;
            }
        }

        if (instance is null && !string.IsNullOrWhiteSpace(currentEnvelope.Location))
        {
            var file = new FileInfo(currentEnvelope.Location);
            var hydrated = await ReaderAndWriter.Reader.ReadAsync(file, cancellationToken).ConfigureAwait(false);
            instance = hydrated;
            var hydratedEnvelope = hydrated.Envelope;
            if (hydratedEnvelope is not null)
            {
                currentEnvelope.ImplementationTypeName = hydratedEnvelope.ImplementationTypeName;
                currentEnvelope.EncryptedFields = hydratedEnvelope.EncryptedFields;
                currentEnvelope.CacheObjectInstance |= hydratedEnvelope.CacheObjectInstance;
                if (!ReferenceEquals(hydratedEnvelope, currentEnvelope))
                {
                    currentEnvelope = hydratedEnvelope;
                }
            }

            if (StorageObjectCachePolicy.ShouldRetainObjectInstance(hydrated, _storagePolicy))
            {
                envelope.ObjectInstance = hydrated;
                envelope.ImplementationTypeName = hydrated.Envelope?.ImplementationTypeName;
                envelope.EncryptedFields = hydrated.Envelope?.EncryptedFields
                    ?? envelope.EncryptedFields;
                envelope.CacheObjectInstance |= hydrated.Envelope?.CacheObjectInstance == true;
                currentEnvelope = envelope;
            }
        }

        if (instance is not null && string.IsNullOrWhiteSpace(currentEnvelope.ImplementationTypeName))
        {
            currentEnvelope.ImplementationTypeName = GetImplementationTypeName(instance.GetType());
        }

        return instance is null ? null : currentEnvelope;
    }

    private static string GetImplementationTypeName(Type type)
        => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_migrationStrategy is not null && IsEagerStrategy(_migrationStrategy.StrategyType))
        {
            await using var migrationScope = await _fileWriteCoordinator
                .AcquireMigrationScopeAsync(cancellationToken)
                .ConfigureAwait(false);
            await _fileWriteCoordinator
                .DrainPendingWritesAsync(_dataRoot, _contractConfig.FileExtension, cancellationToken)
                .ConfigureAwait(false);

            var tolerant = _migrationStrategy.StrategyType == MigrationStrategyType.EagerTolerant;
            try
            {
                await foreach (var migrated in _migrationStrategy.OnInitializeAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (migrated is not null && migrated.Envelope is not null)
                    {
                        _envelopes.AddOrUpdate(
                            migrated.Envelope.EnvelopeCacheKey,
                            EnvelopeCacheKey.ToLogicalKey(migrated.Envelope.EnvelopeCacheKey),
                            migrated.Envelope);
                    }
                }
            }
            catch when (tolerant)
            {
                // Tolerant mode keeps legacy payloads accessible when eager migration encounters issues.
            }
        }
        else
        {
            await _fileWriteCoordinator
                .DrainPendingWritesAsync(_dataRoot, _contractConfig.FileExtension, cancellationToken)
                .ConfigureAwait(false);
        }

        await foreach (var _ in LoadAsync(_dataRoot, cancellationToken).ConfigureAwait(false))
        {
        }

        var effectivePolicy = _storagePolicy;
        if (effectivePolicy.HydrateOnInitialize)
        {
            await HydrateCacheAsync(cancellationToken).ConfigureAwait(false);
        }

        await StartStorageChangeSourceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract>> LoadAsync(
        string rootLocationOfContract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootLocationOfContract))
        {
            throw new ArgumentNullException(nameof(rootLocationOfContract));
        }

        await foreach (var envelope in EnumerateSkeletonsAsync(rootLocationOfContract, cancellationToken).ConfigureAwait(false))
        {
            if (envelope is null)
            {
                continue;
            }

            _envelopes.AddOrUpdate(
                envelope.EnvelopeCacheKey,
                EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey),
                envelope);
            yield return envelope;
        }
    }

    private async ValueTask HydrateCacheAsync(CancellationToken cancellationToken)
    {
        var policy = _storagePolicy;
        var dop = policy.NormalizedConcurrency;
        var snapshot = _envelopes.Values.ToArray();

        await Parallel.ForEachAsync(snapshot, new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        }, async (envelope, ct) =>
        {
            try
            {
                if (envelope is null || envelope.ObjectInstance is not null || string.IsNullOrWhiteSpace(envelope.Location))
                {
                    return;
                }

                if (_migrationStrategy?.StrategyType == MigrationStrategyType.Lazy
                    && !string.Equals(
                        envelope.VersionTag,
                        _contractConfig.ActiveVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var file = new FileInfo(envelope.Location);
                if (!file.Exists)
                {
                    return;
                }

                await ReaderAndWriter.Reader.ReadAsync(file, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort hydration; continue on failures
            }
        }).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateSkeletonsAsync(
        string directory,
        [EnumeratorCancellation] CancellationToken token)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            yield break;
        }

        var expectedExtension = _contractConfig.FileExtension.NormalizeExtension();

        IObjectEnvelope<TContract> CreateSkeleton(FileInfo file, string? version)
        {
            var env = _envelopeFactory()
                ?? throw new InvalidOperationException("Envelope factory returned null.");
            env.EnvelopeCacheKey = Path.GetFileNameWithoutExtension(file.Name);
            env.Location = file.FullName;
            env.VersionTag = string.IsNullOrWhiteSpace(version) ? (_contractConfig.ActiveVersion ?? "1.0.0") : version!;
            env.ObjectInstance = null;
            env.ImplementationTypeName = null;
            env.EncryptedFields = new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
            return env;
        }

        foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return CreateSkeleton(file, _contractConfig.ActiveVersion);
            await Task.Yield();
        }

        foreach (var versionDir in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            var version = versionDir.Name;
            foreach (var file in versionDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(file.Extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return CreateSkeleton(file, version);
                await Task.Yield();
            }
        }
    }

    private static bool IsEagerStrategy(MigrationStrategyType strategy)
        => strategy is MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant;

    private async ValueTask StartStorageChangeSourceAsync(CancellationToken cancellationToken)
    {
        if (_storageChangeSource is null
            || _storageChangeHandler is null
            || Interlocked.Exchange(ref _storageChangeSourceStarted, 1) != 0)
        {
            return;
        }

        _storageChangeSubscription = HandleStorageChangeAsync;
        _storageChangeSource.Changed += _storageChangeSubscription;
        await _storageChangeSource.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask HandleStorageChangeAsync(EnvelopeStorageChange change)
    {
        if (_storageChangeHandler is null)
        {
            return;
        }

        var result = await _storageChangeHandler.HandleAsync(change).ConfigureAwait(false);
        switch (result.ChangeType)
        {
            case EnvelopeStorageChangeType.Created:
                if (result.NewEnvelope is not null)
                {
                    await ContractStored(result.NewEnvelope).ConfigureAwait(false);
                    await ContractAdded(result.NewEnvelope).ConfigureAwait(false);
                }
                break;

            case EnvelopeStorageChangeType.Changed:
                if (result.NewEnvelope is not null)
                {
                    await ContractStored(result.NewEnvelope).ConfigureAwait(false);
                }
                break;

            case EnvelopeStorageChangeType.Deleted:
                if (result.OldEnvelope is not null)
                {
                    await ContractDeleted(result.OldEnvelope).ConfigureAwait(false);
                }
                break;

            case EnvelopeStorageChangeType.Renamed:
                if (result.OldEnvelope is not null)
                {
                    await ContractRemoved(result.OldEnvelope).ConfigureAwait(false);
                }

                if (result.NewEnvelope is not null)
                {
                    await ContractStored(result.NewEnvelope).ConfigureAwait(false);
                    await ContractAdded(result.NewEnvelope).ConfigureAwait(false);
                }
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_storageChangeSource is not null)
        {
            if (_storageChangeSubscription is not null)
            {
                _storageChangeSource.Changed -= _storageChangeSubscription;
            }

            await _storageChangeSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    #region PubSub Area
    public IContractSubscription SubscribeToAdded(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_addedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_removedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_storedSubscriptions, handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null)
        => RegisterSubscription(_deletedSubscriptions, handler, keepAlive);

    public ValueTask ContractAdded(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_addedSubscriptions, @event, cancellationToken);

    public ValueTask ContractRemoved(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_removedSubscriptions, @event, cancellationToken);

    public ValueTask ContractStored(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_storedSubscriptions, @event, cancellationToken);

    public ValueTask ContractDeleted(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default)
        => PublishAsync(_deletedSubscriptions, @event, cancellationToken);

    private IContractSubscription RegisterSubscription(
        ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive)
    {
        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var entry = new SubscriptionEntry(this, bucket, handler, keepAlive);
        bucket[entry.Id] = entry;
        return entry;
    }

    private async ValueTask PublishAsync(
        ConcurrentDictionary<Guid, SubscriptionEntry> subscriptions,
        IObjectEnvelope<TContract> payload,
        CancellationToken cancellationToken)
    {
        var snapshot = subscriptions.ToArray();
        foreach (var kvp in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subscription = kvp.Value;
            if (subscription.IsDisposed)
            {
                subscriptions.TryRemove(subscription.Id, out _);
                continue;
            }

            try
            {
                if (subscription.KeepAlive is not null)
                {
                    var alive = await subscription.KeepAlive().ConfigureAwait(false);
                    if (!alive)
                    {
                        subscription.Dispose();
                        continue;
                    }
                }

                await subscription.Handler(payload).ConfigureAwait(false);
            }
            catch
            {
                subscription.Dispose();
            }
        }
    }

    private void RemoveSubscription(
        ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
        Guid id) => bucket.TryRemove(id, out _);

    private sealed class SubscriptionEntry : IContractSubscription
    {
        private readonly FileEnvelopeRepositoryBase<TContract> _owner;
        private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _bucket;
        private readonly Func<IObjectEnvelope<TContract>, ValueTask> _handler;
        private readonly Func<ValueTask<bool>>? _keepAlive;
        private int _disposed;

        internal SubscriptionEntry(
            FileEnvelopeRepositoryBase<TContract> owner,
            ConcurrentDictionary<Guid, SubscriptionEntry> bucket,
            Func<IObjectEnvelope<TContract>, ValueTask> handler,
            Func<ValueTask<bool>>? keepAlive)
        {
            _owner = owner;
            _bucket = bucket;
            _handler = handler;
            _keepAlive = keepAlive;
            Id = Guid.NewGuid();
        }

        internal Guid Id { get; }
        internal Func<IObjectEnvelope<TContract>, ValueTask> Handler => _handler;
        internal Func<ValueTask<bool>>? KeepAlive => _keepAlive;

        public bool IsDisposed => Interlocked.CompareExchange(ref _disposed, 0, 0) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.RemoveSubscription(_bucket, Id);
            }
        }
    }
    #endregion
}
