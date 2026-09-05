using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Shared implementation for file-system backed envelope repositories.
/// </summary>
public abstract class FileEnvelopeRepositoryBase<TContract> :
    IEnvelopeRepository<TContract>,
    IBatchedEnvelopeRepository<TContract>,
    IEnvelopeRepositoryInitializationStream<TContract>,
    IDisposable,
    IAsyncDisposable
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
    private readonly object _initializationGate = new();
    private Func<EnvelopeStorageChange, ValueTask>? _storageChangeSubscription;
    private Task? _initializationTask;
    private Task? _hydrationTask;
    private Task? _disposalTask;
    private int _storageChangeSourceStarted;
    private int _initialized;
    private int _hydrated;
    private int _disposed;

    private readonly IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> _envelopes;
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _addedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _removedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _storedSubscriptions = new();
    private readonly ConcurrentDictionary<Guid, SubscriptionEntry> _deletedSubscriptions = new();

    protected FileEnvelopeRepositoryBase(
        IDbContractConfigDto<TContract> contractConfig,
        IReaderAndWriter<TContract> readerAndWriter,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
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

        var isNewStorageObject = objectInstance.Envelope is null
            || string.IsNullOrWhiteSpace(objectInstance.Envelope.EnvelopeCacheKey);
        objectInstance = await ReaderAndWriter.Writer.WriteAsync(objectInstance, cancellationToken).ConfigureAwait(false);
        var envelope = objectInstance.Envelope ?? throw new InvalidOperationException("Object instance must have a valid envelope after writing.");

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
            isNew[index] = instance.Envelope is null
                || string.IsNullOrWhiteSpace(instance.Envelope.EnvelopeCacheKey);

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
            if (currentEnvelope is null
                || !StorageObjectCachePolicy.TryGetObjectInstance(currentEnvelope, out _))
            {
                continue;
            }

            yield return currentEnvelope;
        }
    }

    public ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default)
        => GetPhysicalCoreAsync(physicalStorageKey, pinInstance: false, cancellationToken);

    private async ValueTask<IObjectEnvelope<TContract>?> GetPhysicalCoreAsync(
        string physicalStorageKey,
        bool pinInstance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(physicalStorageKey))
        {
            throw new ArgumentException("Storage key cannot be null or whitespace.", nameof(physicalStorageKey));
        }

        return _envelopes.TryGetValue(physicalStorageKey, out var envelope)
            ? await MaterializeEnvelopeAsync(envelope, cancellationToken, pinInstance).ConfigureAwait(false)
            : null;
    }

    public async ValueTask<IReadOnlyList<IObjectEnvelope<TContract>>> GetPhysicalManyAsync(
        IReadOnlyList<string> physicalStorageKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalStorageKeys);
        cancellationToken.ThrowIfCancellationRequested();
        if (physicalStorageKeys.Count == 0)
        {
            return Array.Empty<IObjectEnvelope<TContract>>();
        }
        var batch = new OrderedReadBatch<IObjectEnvelope<TContract>>(physicalStorageKeys, cancellationToken);
        List<int>? missingIndexes = null;
        foreach (var entry in batch.UniqueKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_envelopes.TryGetValue(entry.Key, out var cached)
                && StorageObjectCachePolicy.TryGetObjectInstance(cached, out var instance)
                && instance is not null)
            {
                batch.Set(entry.Value, PinReadEnvelope(cached, instance));
            }
            else
            {
                (missingIndexes ??= new List<int>()).Add(entry.Value);
            }
        }
        if (missingIndexes is not null)
        {
            await Parallel.ForEachAsync(
            missingIndexes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _storagePolicy.NormalizedReadConcurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                batch.Set(index, await GetPhysicalCoreAsync(physicalStorageKeys[index], pinInstance: true, token)
                    .ConfigureAwait(false));
            }).ConfigureAwait(false);
        }
        return batch.Complete(cancellationToken);
    }

    public IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        CancellationToken cancellationToken = default)
        => EnumerateCachedOrBatchedAsync(cached: null, cancellationToken);

    private async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateCachedOrBatchedAsync(
        IEnumerable<IObjectEnvelope<TContract>>? cached,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Defer the full-store snapshot until enumeration starts, without an extra
        // forwarding async iterator on every ordinary contract scan.
        cached ??= _envelopes.Values;
        if (cached.All(static envelope => envelope.ObjectInstance is not null))
        {
            foreach (var envelope in cached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
            yield break;
        }

        if (cached.All(static envelope => StorageObjectCachePolicy.TryGetObjectInstance(envelope, out _)))
        {
            // Live weak-cache scans need neither disk prefetch nor per-window lookup maps.
            // Recheck each entry while reading: a target may be collected after this test.
            foreach (var envelope in cached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await MaterializeEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    yield return current;
                }
            }
            yield break;
        }

        await foreach (var currentEnvelope in EnvelopeBatchEnumeration.ReadAsync(
            this,
            cached.Select(static envelope => envelope.EnvelopeCacheKey),
            cancellationToken).ConfigureAwait(false))
        {
            if (!StorageObjectCachePolicy.TryGetObjectInstance(currentEnvelope, out _))
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
        var candidates = _envelopes.Values.Where(envelope =>
            string.IsNullOrWhiteSpace(envelope.ImplementationTypeName)
            || string.Equals(envelope.ImplementationTypeName, expectedTypeName, StringComparison.Ordinal));
        await foreach (var currentEnvelope in EnumerateCachedOrBatchedAsync(candidates, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StorageObjectCachePolicy.TryGetObjectInstance(currentEnvelope, out var instance)
                || instance is not TImplementation)
            {
                continue;
            }

            currentEnvelope.ImplementationTypeName ??= expectedTypeName;
            yield return currentEnvelope;
        }
    }

    private async ValueTask<IObjectEnvelope<TContract>?> MaterializeEnvelopeAsync(
        IObjectEnvelope<TContract>? envelope,
        CancellationToken cancellationToken,
        bool pinInstance = false)
    {
        if (envelope is null)
        {
            return null;
        }

        var currentEnvelope = envelope;
        StorageObjectCachePolicy.TryGetObjectInstance(currentEnvelope, out var instance);
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
                currentEnvelope.ExtensionMetadata = hydratedEnvelope.ExtensionMetadata?.ToArray();
                currentEnvelope.CacheObjectInstance |= hydratedEnvelope.CacheObjectInstance;
                if (!ReferenceEquals(hydratedEnvelope, currentEnvelope))
                {
                    currentEnvelope = hydratedEnvelope;
                }
            }

            var cacheMode = StorageObjectCachePolicy.ResolveObjectCacheMode(hydrated, _storagePolicy);
            if (cacheMode is not StorageObjectCacheMode.MetadataOnly)
            {
                envelope.ImplementationTypeName = hydrated.Envelope?.ImplementationTypeName;
                envelope.EncryptedFields = hydrated.Envelope?.EncryptedFields
                    ?? envelope.EncryptedFields;
                envelope.ExtensionMetadata = hydrated.Envelope?.ExtensionMetadata?.ToArray();
                envelope.CacheObjectInstance |= hydrated.Envelope?.CacheObjectInstance == true;
                StorageObjectCachePolicy.StoreObjectReference(envelope, hydrated, _storagePolicy);
                currentEnvelope = envelope;
            }
        }

        if (instance is not null && string.IsNullOrWhiteSpace(currentEnvelope.ImplementationTypeName))
        {
            currentEnvelope.ImplementationTypeName = GetImplementationTypeName(instance.GetType());
        }

        if (pinInstance && instance is not null && currentEnvelope.ObjectInstance is null)
        {
            // A batch owns its materialized results until the consumer releases it. Keep a weak
            // cache target alive there without promoting the repository's weak cache to strong.
            return PinReadEnvelope(currentEnvelope, instance);
        }

        return instance is null ? null : currentEnvelope;
    }

    private IObjectEnvelope<TContract> PinReadEnvelope(IObjectEnvelope<TContract> envelope, TContract instance)
    {
        if (envelope.ObjectInstance is not null)
        {
            return envelope;
        }
        var readEnvelope = _envelopeFactory();
        readEnvelope.EnvelopeCacheKey = envelope.EnvelopeCacheKey;
        readEnvelope.VersionTag = envelope.VersionTag;
        readEnvelope.Location = envelope.Location;
        readEnvelope.State = envelope.State;
        readEnvelope.ImplementationTypeName = envelope.ImplementationTypeName;
        readEnvelope.CacheObjectInstance = envelope.CacheObjectInstance;
        readEnvelope.EncryptedFields = envelope.EncryptedFields;
        readEnvelope.ExtensionMetadata = envelope.ExtensionMetadata;
        readEnvelope.ObjectInstance = instance;
        return readEnvelope;
    }

    private static string GetImplementationTypeName(Type type)
        => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => EnsureInitializedAsync(_storagePolicy.HydrateOnInitialize, cancellationToken);

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Index construction materializes every live envelope below. Avoid a separate eager
        // hydration pass, which would deserialize every payload twice for metadata-only caches.
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        await foreach (var envelope in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    private ValueTask EnsureInitializedAsync(
        bool hydrateOnInitialize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _initialized) != 0
            && (!hydrateOnInitialize || Volatile.Read(ref _hydrated) != 0))
        {
            return ValueTask.CompletedTask;
        }

        Task initialization;
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var storageInitialization = _initializationTask ??= InitializeCoreAsync();
            if (hydrateOnInitialize)
            {
                _hydrationTask ??= HydrateAfterInitializationAsync(storageInitialization);
            }

            initialization = hydrateOnInitialize ? _hydrationTask! : storageInitialization;
        }

        // Cancellation belongs to this waiter. Repository activation is shared by every caller
        // and must not be left partially initialized when the first caller abandons its wait.
        return new ValueTask(initialization.WaitAsync(cancellationToken));
    }

    private async Task InitializeCoreAsync()
    {
        var cancellationToken = CancellationToken.None;
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

        // Arm the watcher before walking the files, but buffer its publications until the cache
        // snapshot is installed. This closes both sides of the classic bootstrap race: changes
        // before watcher activation are observed by the subsequent walk, while changes during
        // the walk are replayed after it and therefore cannot be overwritten by a stale skeleton.
        var bootstrapChangeSource = _storageChangeHandler is not null
            ? _storageChangeSource as IFileEnvelopeStorageChangeBootstrap
            : null;
        if (bootstrapChangeSource is not null)
        {
            await StartStorageChangeSourceAsync(
                bufferBootstrapChanges: true,
                cancellationToken).ConfigureAwait(false);
        }

        List<string>? loadedProviderLocators = bootstrapChangeSource is null ? null : [];
        await foreach (var envelope in LoadAsync(_dataRoot, cancellationToken).ConfigureAwait(false))
        {
            if (loadedProviderLocators is not null
                && !string.IsNullOrWhiteSpace(envelope.Location))
            {
                loadedProviderLocators.Add(envelope.Location!);
            }
        }

        if (bootstrapChangeSource is not null)
        {
            // Completion takes the final snapshot, replays every dirty path against current disk
            // state, and switches to live delivery under the same callback gate.
            await bootstrapChangeSource
                .CompleteBootstrapAsync(loadedProviderLocators!, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await StartStorageChangeSourceAsync(
                bufferBootstrapChanges: false,
                cancellationToken).ConfigureAwait(false);
        }

        Volatile.Write(ref _initialized, 1);
    }

    private async Task HydrateAfterInitializationAsync(Task initialization)
    {
        await initialization.ConfigureAwait(false);
        await HydrateCacheAsync(CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref _hydrated, 1);
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
        var dop = policy.NormalizedReadConcurrency;
        var snapshot = _envelopes.Values.ToArray();

        await Parallel.ForEachAsync(snapshot, new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        }, async (envelope, ct) =>
        {
            try
            {
                if (envelope is null
                    || StorageObjectCachePolicy.TryGetObjectInstance(envelope, out _)
                    || string.IsNullOrWhiteSpace(envelope.Location))
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
            env.ObjectInstanceWeakReference = null;
            env.ImplementationTypeName = null;
            env.EncryptedFields = new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
            env.ExtensionMetadata = null;
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
            }
        }
    }

    private static bool IsEagerStrategy(MigrationStrategyType strategy)
        => strategy is MigrationStrategyType.EagerStrict or MigrationStrategyType.EagerTolerant;

    private async ValueTask StartStorageChangeSourceAsync(
        bool bufferBootstrapChanges,
        CancellationToken cancellationToken)
    {
        if (_storageChangeSource is null
            || _storageChangeHandler is null
            || Interlocked.Exchange(ref _storageChangeSourceStarted, 1) != 0)
        {
            return;
        }

        _storageChangeSubscription = HandleStorageChangeAsync;
        _storageChangeSource.Changed += _storageChangeSubscription;
        if (bufferBootstrapChanges
            && _storageChangeSource is IFileEnvelopeStorageChangeBootstrap bootstrapChangeSource)
        {
            await bootstrapChangeSource.StartBufferingAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _storageChangeSource.StartAsync(cancellationToken).ConfigureAwait(false);
        }
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

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_initializationGate)
        {
            if (_disposalTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposalTask = DisposeCoreAsync(_initializationTask, _hydrationTask);
            }

            disposal = _disposalTask;
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(Task? initialization, Task? hydration)
    {
        await ObserveInitializationForDisposalAsync(initialization).ConfigureAwait(false);
        if (!ReferenceEquals(hydration, initialization))
        {
            await ObserveInitializationForDisposalAsync(hydration).ConfigureAwait(false);
        }

        if (_storageChangeSource is null)
        {
            return;
        }

        if (_storageChangeSubscription is not null)
        {
            _storageChangeSource.Changed -= _storageChangeSubscription;
        }

        await _storageChangeSource.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task ObserveInitializationForDisposalAsync(Task? initialization)
    {
        if (initialization is null)
        {
            return;
        }

        try
        {
            await initialization.ConfigureAwait(false);
        }
        catch
        {
            // The initializing caller observes activation failures. Disposal still owns cleanup.
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
