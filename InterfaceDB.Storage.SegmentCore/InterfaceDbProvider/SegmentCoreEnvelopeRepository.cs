using Idb.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// Stores serialized InterfaceDB envelopes in SegmentCore while preserving the repository,
/// indexing, encryption, polymorphism, and notification abstractions used by InterfaceDB.
/// </summary>
public sealed class SegmentCoreEnvelopeRepository<TContract> :
    IEnvelopeRepository<TContract>,
    IPhysicalEnvelopeLookup,
    ICachedEnvelopeSource<TContract>,
    ICommittedEnvelopeSnapshotSource<TContract>,
    IRetainedVersionStore<TContract>,
    IBatchedEnvelopeRepository<TContract>,
    IEnvelopeRepositoryInitializationStream<TContract>,
    IPersistentSearchIndexCatalogRepository,
    ISegmentCoreRepositoryMaintenance<TContract>,
    ICompactionMaintenance<TContract>,
    IAsyncDisposable
    where TContract : class, IStoredContract<TContract>
{
    public IEnumerable<KeyValuePair<string, TContract>> EnumerateCachedObjects()
        => CachedEnvelopeEnumeration.Enumerate(_envelopes)
            .Select(item => new KeyValuePair<string, TContract>(item.Key,
                CommittedCacheCopies<TContract>.Copy(item.Value.Envelope!, pinnedInstance: item.Value).ObjectInstance!));

    bool ICachedEnvelopeSource<TContract>.IsolatesCachedReads => true;

    private const int CacheMutationGateCount = 64;
    private readonly IDbContractConfigDto<TContract> _contractConfig;
    private readonly StoragePolicy _storagePolicy;
    private readonly SegmentCoreRepositoryOptions _options;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly bool _serializerConsumesContext;
    private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
    private readonly IEnvelopeStorageIdentityFactory<TContract> _identityFactory;
    private readonly IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> _envelopes;
    private readonly Func<IObjectEnvelope<TContract>> _envelopeFactory;
    private readonly IEncryptor _encryptor;
    private readonly IReadOnlyList<IMigration> _migrations;
    private readonly ContractPubSubHub<TContract> _pubSub;
    private readonly ConcurrentDictionary<string, byte> _reservedKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SuffixAllocator> _suffixAllocators = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private readonly object _initializationGate = new();
    private readonly object[] _cacheMutationGates = CreateCacheMutationGates();
    private readonly CancellationTokenSource _maintenanceCancellation = new();
    private readonly string _storeRoot;
    private readonly IReaderAndWriter<TContract> _readerAndWriter;
    private Task? _initializationTask;
    private Task? _hydrationTask;
    private Task? _maintenanceTask;
    private CompactionScheduler? _compactionScheduler;
    private Task? _disposalTask;
    private SegmentCoreStore? _store;
    private readonly EnvelopeWriteCoordinator<TContract> _revisions;
    private readonly RetainedEditingSupport<TContract>? _editing;
    private Exception? _lastMaintenanceError;
    private long _totalMutations;
    private long _checkpointMutationWatermark;
    private long _compactionMutationWatermark;
    private long _lastCheckpointUtcTicks;
    private long _lastCompactionUtcTicks;
    private int _initialized;
    private int _hydrated;
    private int _disposed;

    public SegmentCoreEnvelopeRepository(
        IDbContractConfigDto<TContract> contractConfig,
        StoragePolicy storagePolicy,
        SegmentCoreRepositoryOptions options,
        IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> envelopeLifecycle,
        IEnvelopeStorageIdentityFactory<TContract> identityFactory,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
        Func<IObjectEnvelope<TContract>> envelopeFactory,
        IEncryptor encryptor,
        IEnumerable<IMigration> migrations,
        IWriteConflictStrategy<TContract>? writeConflictStrategy = null)
    {
        _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
        _pubSub = new(storagePolicy.Telemetry);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _serializerConsumesContext = _serializer is IContextualObjectSerializer<TContract>;
        _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
        _identityFactory = identityFactory ?? throw new ArgumentNullException(nameof(identityFactory));
        _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _migrations = migrations?.ToArray() ?? [];
        if (string.IsNullOrWhiteSpace(contractConfig.DataRoot))
        {
            throw new InvalidOperationException("Contract configuration must supply a DataRoot directory.");
        }

        _storeRoot = Path.Combine(
            Path.GetFullPath(contractConfig.DataRoot),
            options.DirectoryName,
            string.IsNullOrWhiteSpace(contractConfig.ActiveVersion) ? "1.0.0" : contractConfig.ActiveVersion);
        _readerAndWriter = new SegmentCoreReaderAndWriter(this);
        if (storagePolicy.RetainedVersions is { } retention)
        {
            retention.Validate();
            _editing = new(serializer, envelopeLifecycle, CreateSerializerContext);
        }
        _revisions = new(
            _storeRoot,
            serializer,
            envelopeLifecycle,
            writeConflictStrategy,
            _editing,
            CreateSerializerContext);
    }

    public IReaderAndWriter<TContract> ReaderAndWriter => _readerAndWriter;

    public RetainedVersionCapability RetainedVersions => new(_editing is not null, _editing is null ? "Enable StoragePolicy.RetainedVersions before opening the repository." : null);
    public RetainedVersionStatistics RetentionStatistics => _store?.RetentionStatistics ?? new(0, 0, 0, 0, 0, 0, 0);
    public async ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey,
        Func<TContract, CancellationToken, ValueTask<TContract>> saveAsync, CancellationToken cancellationToken = default)
    {
        EnvelopeResolverScope.ThrowIfActive();
        var editing = _editing ?? throw new NotSupportedException(RetainedVersions.Limitation);
        await EnsureInitializedAsync(false, cancellationToken).ConfigureAwait(false);
        var lease = await Store.AcquireRetainedAsync(physicalKey, cancellationToken).ConfigureAwait(false);
        return await editing.BeginAsync(lease, physicalKey, saveAsync, cancellationToken).ConfigureAwait(false);
    }

    public string SearchIndexCatalogDirectory => Path.Combine(_storeRoot, "search-indexes");

    public bool PersistentSearchIndexCatalogEnabled => _encryptor is EncryptionNone;

    public StoragePolicy StoragePolicy => _storagePolicy;

    public SegmentCoreRepositoryOptions Options => _options;

    public SegmentCoreProviderStatus Status
    {
        get
        {
            var total = Volatile.Read(ref _totalMutations);
            var store = Volatile.Read(ref _store);
            return new SegmentCoreProviderStatus(
                store is not null,
                Math.Max(0, total - Volatile.Read(ref _checkpointMutationWatermark)),
                Math.Max(0, total - Volatile.Read(ref _compactionMutationWatermark)),
                ToTimestamp(Volatile.Read(ref _lastCheckpointUtcTicks)),
                ToTimestamp(Volatile.Read(ref _lastCompactionUtcTicks)),
                Volatile.Read(ref _lastMaintenanceError),
                store?.Statistics)
            {
                HardwareProfile = _options.HardwareProfile,
                CompactionObservationFailures = _compactionScheduler?.ObservationFailureCount ?? 0
            };
        }
    }

    public async ValueTask<bool> ExistsPhysicalAsync(string physicalKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return _envelopes.ContainsKey(physicalKey);
    }

    public bool Exists(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        EnsureInitializedSynchronously();
        return _envelopes.TryGetGroup(storageKey, out _)
            || (_envelopes.ContainsKey(storageKey) && _envelopes.TryGetGroup(ToLogicalKey(storageKey), out _));
    }

    public bool TryGet(string storageKey, out IReadOnlyList<IObjectEnvelope<TContract>> envelopes)
    {
        var found = TryGetCachedGroup(storageKey, out var group);
        envelopes = group.Select(e => CommittedCacheCopies<TContract>.Copy(e)).ToArray();
        return found;
    }

    private bool TryGetCachedGroup(string storageKey, out IReadOnlyList<IObjectEnvelope<TContract>> envelopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        EnsureInitializedSynchronously();
        if (_envelopes.TryGetGroup(storageKey, out envelopes)) return true;
        if (_envelopes.ContainsKey(storageKey))
            return _envelopes.TryGetGroup(ToLogicalKey(storageKey), out envelopes);
        return false;
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => EnsureInitializedAsync(_storagePolicy.HydrateOnInitialize, cancellationToken);

    public ValueTask InitializeForSearchIndexCatalogAsync(CancellationToken cancellationToken = default)
        => EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken);

    public async ValueTask<SearchIndexStorageIdentity> GetSearchIndexStorageIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var identity = Store.GetSearchIndexStorageIdentity(cancellationToken);
        return new SearchIndexStorageIdentity(identity.EntryCount, identity.Fingerprint);
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        await foreach (var envelope in EnumerateInitializedAsync(cancellationToken).ConfigureAwait(false))
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

        return new ValueTask(initialization.WaitAsync(cancellationToken));
    }

    public async ValueTask<TContract> WriteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstance);
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var item = await PrepareWriteAsync(contractInstance, cancellationToken).ConfigureAwait(false);
        var reservationReleased = false;
        try
        {
            var key = item.Envelope.EnvelopeCacheKey;
            using var writeGuard = await _revisions.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
            var current = await _revisions.ReadCommittedRevisionAsync(key, ct => Store.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
            var candidate = await _revisions.PrepareAsync(item.Envelope, item.IsNew, current,
                ct => Store.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
            if (EnvelopeWriteOutcome.IsNoChange(candidate)) return _revisions.Publish(contractInstance, candidate);
            item = item with
            {
                Envelope = candidate,
                Payload = SerializePayload(candidate, key)
            };
            // One durable frame is atomic; explicit batches retain the coordinated transaction path.
            await Store.SaveOwnedAsync(key, item.Payload, transaction: null, cancellationToken).ConfigureAwait(false);
            var saved = _revisions.Publish(contractInstance, item.Envelope);
            CommittedWriteCapture<TContract>.Remember(
                item.Envelope,
                item.Payload,
                _serializer,
                _envelopeLifecycle,
                CreateSerializerContext(key));
            CacheEnvelope(item.Envelope, saved);
            ReleaseReservation(item);
            reservationReleased = true;
            writeGuard.Dispose();
            Interlocked.Increment(ref _totalMutations);
            if (_storagePolicy.RaiseNotifications)
            {
                await ContractStored(item.Envelope, CancellationToken.None).ConfigureAwait(false);
                if (item.IsNew) await ContractAdded(item.Envelope, CancellationToken.None).ConfigureAwait(false);
            }
            return saved;
        }
        finally { if (!reservationReleased) ReleaseReservation(item); }
    }

    public async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
        IEnumerable<TContract> contractInstances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstances);
        var materialized = contractInstances as IReadOnlyList<TContract> ?? contractInstances.ToArray();
        if (materialized.Count == 0)
        {
            return Array.Empty<TContract>();
        }

        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var prepared = new List<PreparedWrite>(materialized.Count);
        var batchKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var instance in materialized)
            {
                ArgumentNullException.ThrowIfNull(instance);
                var item = await PrepareWriteAsync(instance, cancellationToken).ConfigureAwait(false);
                prepared.Add(item);
                if (!batchKeys.Add(item.Envelope.EnvelopeCacheKey))
                {
                    throw new InvalidOperationException(
                        $"Atomic SegmentCore batch contains duplicate physical key '{item.Envelope.EnvelopeCacheKey}'.");
                }
            }
            using var writeGuard = await _revisions.AcquireAsync(batchKeys, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                var key = item.Envelope.EnvelopeCacheKey;
                var current = await _revisions.ReadCommittedRevisionAsync(key, ct => Store.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
                var candidate = await _revisions.PrepareAsync(item.Envelope, item.IsNew, current,
                    ct => Store.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
                prepared[index] = item with
                {
                    Envelope = candidate,
                    Payload = EnvelopeWriteOutcome.IsNoChange(candidate)
                        ? []
                        : SerializePayload(candidate, key)
                };
            }
            if (CountCommitted(prepared) != 0)
            await using (var transaction = await Store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var item in prepared)
                {
                    if (EnvelopeWriteOutcome.IsNoChange(item.Envelope)) continue;
                    await Store.SaveOwnedAsync(item.Envelope.EnvelopeCacheKey, item.Payload, transaction, cancellationToken)
                        .ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var results = new TContract[prepared.Count];
            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                var saved = _revisions.Publish(materialized[index], item.Envelope);
                if (EnvelopeWriteOutcome.IsNoChange(item.Envelope)) { results[index] = saved; ReleaseReservation(item); continue; }
                CommittedWriteCapture<TContract>.Remember(
                    item.Envelope,
                    item.Payload,
                    _serializer,
                    _envelopeLifecycle,
                    CreateSerializerContext(item.Envelope.EnvelopeCacheKey));
                CacheEnvelope(item.Envelope, saved);
                ReleaseReservation(item);
                results[index] = saved;
            }

            writeGuard.Dispose(); // Publication is complete; user callbacks must not run under entity writer guards.
            Interlocked.Add(ref _totalMutations, CountCommitted(prepared));
            if (_storagePolicy.RaiseNotifications)
            {
                for (var index = 0; index < prepared.Count; index++)
                {
                    if (EnvelopeWriteOutcome.IsNoChange(prepared[index].Envelope)) continue;
                    await ContractStored(prepared[index].Envelope, CancellationToken.None).ConfigureAwait(false);
                    if (prepared[index].IsNew)
                    {
                        await ContractAdded(prepared[index].Envelope, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            return results;
        }
        catch
        {
            foreach (var item in prepared)
            {
                ReleaseReservation(item);
            }

            throw;
        }
    }

    public async ValueTask<bool> DeleteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contractInstance);
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var envelope = contractInstance.Envelope
            ?? throw new InvalidOperationException("The object has not been persisted by InterfaceDB.");
        if (envelope.State == StorageObjectState.Deleted)
        {
            return false;
        }

        var key = envelope.EnvelopeCacheKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var writeGuard = await _revisions.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        var current = await _revisions.ReadCommittedRevisionAsync(key, ct => Store.GetAsync(key, ct), cancellationToken).ConfigureAwait(false);
        await _revisions.PrepareAsync(envelope, false, current, ct => Store.GetAsync(key, ct), cancellationToken, deleting: true).ConfigureAwait(false);
        if (!await Store.DeleteAsync(key, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _revisions.Committed(key, null);
        EnvelopeCommitOrder.Stamp(envelope);
        envelope.State = StorageObjectState.Deleted;
        RemoveCachedEnvelope(key);
        writeGuard.Dispose();
        Interlocked.Increment(ref _totalMutations);
        if (_storagePolicy.RaiseNotifications)
        {
            await ContractDeleted(envelope, CancellationToken.None).ConfigureAwait(false);
        }

        return true;
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(
        string storageKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        if (!TryGetCachedGroup(storageKey, out var group))
        {
            yield break;
        }

        foreach (var envelope in group)
        {
            var hydrated = await GetPhysicalAsync(envelope.EnvelopeCacheKey, cancellationToken).ConfigureAwait(false);
            if (hydrated is not null)
            {
                yield return hydrated;
            }
        }
    }

    public async ValueTask<ICommittedEnvelopeSnapshot<TContract>?> CaptureCommittedAsync(
        IObjectEnvelope<TContract> identity, int maximumBytes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var payload = await Store.GetAsync(identity.EnvelopeCacheKey, cancellationToken).ConfigureAwait(false);
        if (payload is null) return null;
        ValidateStoredPayload(payload, "Notification snapshot");
        if (payload.Length > maximumBytes)
            throw new InvalidDataException("Notification snapshot exceeds the configured byte limit.");
        return new CommittedEnvelopeSnapshot<TContract>(
            payload,
            _serializer,
            _envelopeLifecycle,
            identity.EnvelopeCacheKey,
            identity.EnvelopeCacheKey,
            CreateSerializerContext(identity.EnvelopeCacheKey));
    }

    public async ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalStorageKey);
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        if (_envelopes.TryGetValue(physicalStorageKey, out var cached)
            && StorageObjectCachePolicy.TryGetObjectInstance(cached, out var instance))
        {
            return CommittedCacheCopies<TContract>.Copy(cached, pinnedInstance: instance);
        }

        var payload = await Store.GetAsync(physicalStorageKey, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            RemoveCachedEnvelopeIfUnchanged(physicalStorageKey, cached);
            return null;
        }

        return await MaterializeAndCacheAsync(
            physicalStorageKey,
            payload,
            cached,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IObjectEnvelope<TContract>>> GetPhysicalManyAsync(
        IReadOnlyList<string> physicalStorageKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(physicalStorageKeys);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        if (physicalStorageKeys.Count == 0)
        {
            return Array.Empty<IObjectEnvelope<TContract>>();
        }
        var batch = new OrderedReadBatch<IObjectEnvelope<TContract>>(physicalStorageKeys, cancellationToken);
        List<string>? missing = null;
        Dictionary<string, IObjectEnvelope<TContract>?>? expectedEntries = null;
        foreach (var entry in batch.UniqueKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = entry.Key;
            if (_envelopes.TryGetValue(key, out var cached)
                && StorageObjectCachePolicy.TryGetObjectInstance(cached, out var instance)
                && instance is not null)
            {
                batch.Set(entry.Value, CommittedCacheCopies<TContract>.Copy(cached, pinnedInstance: instance));
            }
            else
            {
                (missing ??= new List<string>()).Add(key);
                (expectedEntries ??= new(StringComparer.Ordinal)).Add(key, cached);
            }
        }

        if (missing is not null)
        {
            var records = await Store.ReadManyAsync(missing, cancellationToken).ConfigureAwait(false);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batch.Set(
                    record.Key,
                    await MaterializeAndCacheAsync(
                        record.Key,
                        record.Payload,
                        expectedEntries![record.Key],
                        cancellationToken).ConfigureAwait(false));
            }
        }

        if (missing is not null)
        {
            foreach (var key in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!batch.HasValue(key))
                {
                    RemoveCachedEnvelopeIfUnchanged(key, expectedEntries![key]);
                }
            }
        }
        return batch.Complete(cancellationToken);
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumeratePartiallyCachedAsync(
        IReadOnlyList<string> keys,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var start = 0; start < keys.Count;)
        {
            var count = Math.Min(start == 0 ? 1 : 256, keys.Count - start);
            var window = new string[count];
            for (var index = 0; index < count; index++)
            {
                window[index] = keys[start + index];
            }
            var envelopes = await GetPhysicalManyAsync(window, cancellationToken).ConfigureAwait(false);
            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
            start += count;
        }
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        await foreach (var envelope in EnumerateInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    private async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateInitializedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        // A complete cache avoids rereading the object log. Materialize independent caller
        // copies from committed cached bytes for warm scans and index rebuilds.
        var cachedEnvelopes = _envelopes.Values;
        var cacheIsCompleteAndStrong = cachedEnvelopes.Count == Store.Count;
        if (cacheIsCompleteAndStrong)
        {
            foreach (var envelope in cachedEnvelopes)
            {
                if (envelope.ObjectInstance is null)
                {
                    cacheIsCompleteAndStrong = false;
                    break;
                }
            }
        }

        if (cacheIsCompleteAndStrong)
        {
            foreach (var envelope in cachedEnvelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CommittedCacheCopies<TContract>.Copy(envelope);
            }

            yield break;
        }

        var cachedCount = cachedEnvelopes.Count(static envelope =>
            StorageObjectCachePolicy.TryGetObjectInstance(envelope, out _));
        if (cachedCount > 0 && cachedCount >= cachedEnvelopes.Count / 2)
        {
            var keys = Store.GetKeysSnapshot();
            await foreach (var envelope in EnumeratePartiallyCachedAsync(keys, cancellationToken).ConfigureAwait(false))
            {
                yield return envelope;
            }
            yield break;
        }

        // Capture identities before the physical snapshot. A save, acknowledgement or delete
        // replaces/removes its entry; a delayed read must not publish over that newer state.
        var expectedEntries = cachedEnvelopes.ToDictionary(
            static envelope => envelope.EnvelopeCacheKey, StringComparer.Ordinal);
        await foreach (var record in Store.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_envelopes.TryGetValue(record.Key, out var cached)
                && StorageObjectCachePolicy.TryGetObjectInstance(cached, out var cachedInstance))
            {
                yield return CommittedCacheCopies<TContract>.Copy(cached, pinnedInstance: cachedInstance);
                GC.KeepAlive(cachedInstance);
                continue;
            }

            expectedEntries.TryGetValue(record.Key, out var expected);
            yield return await MaterializeAndCacheAsync(
                record.Key,
                record.Payload,
                expected,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TImplementation : class, TContract
    {
        await foreach (var envelope in EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var instance)
                && ContractInstance<TContract>.Is<TImplementation>(instance))
            {
                yield return envelope;
            }
        }
    }

    public async ValueTask<SegmentCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var mutationWatermark = Volatile.Read(ref _totalMutations);
        var result = await Store.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _checkpointMutationWatermark, mutationWatermark);
        Volatile.Write(ref _lastCheckpointUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastMaintenanceError, null);
        return result;
    }

    public async ValueTask<SegmentCoreCompactionResult> CompactAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var scheduler = Volatile.Read(ref _compactionScheduler);
        return scheduler is null
            ? await CompactCoreAsync(cancellationToken).ConfigureAwait(false)
            : await scheduler.ObserveAsync(CompactCoreAsync, SummarizeCompaction, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SegmentCoreCompactionResult> CompactCoreAsync(CancellationToken cancellationToken)
    {
        var mutationWatermark = Volatile.Read(ref _totalMutations);
        var result = await Store.CompactAsync(_options.CompactionOptions, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _compactionMutationWatermark, mutationWatermark);
        Volatile.Write(ref _checkpointMutationWatermark, mutationWatermark);
        Volatile.Write(ref _lastCompactionUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastCheckpointUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _lastMaintenanceError, null);
        return result;
    }

    private CompactionWorkResult SummarizeCompaction(SegmentCoreCompactionResult result) =>
        new(result.FramesCopied, result.BytesCopied, result.BytesReclaimed,
            RetentionStatistics.RetainedBytes, result.IsPartial);

    public IMaintenanceSubscription SubscribeMaintenance(
        Func<MaintenanceNotification, CancellationToken, ValueTask> handler, CancellationToken lifetime = default) =>
        GetCompactionScheduler().SubscribeMaintenance(handler, lifetime);

    public async ValueTask<CompactionEstimate> EstimateCompactionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        return await Store.GetCompactionEstimateAsync(_options.CompactionOptions, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CompactionRunResult> RunCompactionAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        return await GetCompactionScheduler().RunAsync(manual, cancellationToken).ConfigureAwait(false);
    }

    private CompactionScheduler GetCompactionScheduler()
    {
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _compactionScheduler ??= new CompactionScheduler(
                token => Store.GetCompactionEstimateAsync(_options.CompactionOptions, token),
                async token => SummarizeCompaction(await CompactCoreAsync(token).ConfigureAwait(false)),
                _options.CompactionSchedule);
        }
    }

    public async ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        return await Store.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SegmentCoreBackupResult> BackupAsync(
        string destinationDirectory,
        SegmentCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        using var writeGuard = await _revisions.AcquireMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        return await Store.BackupAsync(destinationDirectory, options, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SegmentCoreMigrationResult> MigrateFromAsync(
        string sourceStoreDirectory,
        string sourceVersion,
        SegmentCoreMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStoreDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        options ??= new SegmentCoreMigrationOptions();
        options.Validate();
        await EnsureInitializedAsync(hydrateOnInitialize: false, cancellationToken).ConfigureAwait(false);
        var sourceRoot = Path.GetFullPath(sourceStoreDirectory);
        if (IsPathWithin(sourceRoot, _storeRoot) || IsPathWithin(_storeRoot, sourceRoot))
        {
            throw new ArgumentException(
                "The source migration store and active SegmentCore store must be separate trees.",
                nameof(sourceStoreDirectory));
        }

        var activeVersion = _contractConfig.ActiveVersion ?? "1.0.0";
        var stopwatch = Stopwatch.StartNew();
        var engine = new SegmentCoreMigrationEngine<TContract>(
            activeVersion,
            _encryptor,
            _migrations,
            CreateEncryptionContext);
        var failures = new List<SegmentCoreMigrationFailure>();
        var pending = new List<TContract>(options.BatchSize);
        var examined = 0;
        var migrated = 0;
        var alreadyPresent = 0;
        await using var source = await SegmentCoreStore.OpenAsync(
            sourceRoot,
            options.SourceStoreOptions ?? _options.StoreOptions,
            cancellationToken).ConfigureAwait(false);
        await foreach (var record in source.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            examined++;
            if (await Store.ContainsKeyAsync(record.Key, cancellationToken).ConfigureAwait(false))
            {
                alreadyPresent++;
                continue;
            }

            try
            {
                ValidateStoredPayload(record.Payload, "Migration source payload");
                var migrationResult = engine.Migrate(record.Key, sourceVersion, record.Payload);
                var migratedContract = migrationResult.Contract;
                var envelope = await _envelopeLifecycle.WrapAsync(migratedContract).ConfigureAwait(false);
                envelope.EnvelopeCacheKey = record.Key;
                envelope.VersionTag = activeVersion;
                envelope.Location = record.Key;
                envelope.ImplementationTypeName = migratedContract.GetType().AssemblyQualifiedName
                    ?? migratedContract.GetType().FullName
                    ?? migratedContract.GetType().Name;
                envelope.ExtensionMetadata = migrationResult.ExtensionMetadata?.ToArray();
                var sourceRevision = ContextualObjectSerialization.ReadRevision(
                    _serializer,
                    record.Payload,
                    CreateSerializerContext(record.Key));
                EnvelopeWriteCoordinator<TContract>.SetCondition(envelope, new EnvelopeWriteCondition(0,
                    sourceRevision > 0 ? EnvelopeRevisionAction.Import : EnvelopeRevisionAction.Advance,
                    sourceRevision, ExpectedMissing: true));
                envelope.State = StorageObjectState.Active;
                if (_envelopeLifecycle is IEnvelopeFieldBinding<TContract> fieldBinding) fieldBinding.BindFields(envelope);
                pending.Add(_envelopeLifecycle.Unwrap(envelope));
                if (pending.Count == options.BatchSize)
                {
                    _ = await WriteManyAsync(pending, cancellationToken).ConfigureAwait(false);
                    migrated += pending.Count;
                    pending.Clear();
                }
            }
            catch (Exception exception) when (
                options.ContinueOnError && exception is not OperationCanceledException)
            {
                failures.Add(new SegmentCoreMigrationFailure(record.Key, exception.Message));
            }
        }

        if (pending.Count > 0)
        {
            _ = await WriteManyAsync(pending, cancellationToken).ConfigureAwait(false);
            migrated += pending.Count;
        }

        SegmentCoreCheckpointResult? checkpoint = null;
        if (migrated > 0)
        {
            checkpoint = await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.VerifyAfterMigration)
        {
            var integrity = await Store.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
            if (!integrity.IsValid)
            {
                throw new InvalidDataException(
                    "SegmentCore migration completed but integrity verification failed: "
                    + string.Join("; ", integrity.Failures));
            }
        }

        stopwatch.Stop();
        return new SegmentCoreMigrationResult(
            sourceRoot,
            sourceVersion,
            activeVersion,
            examined,
            migrated,
            alreadyPresent,
            failures,
            checkpoint,
            stopwatch.Elapsed);
    }

    public IContractSubscription SubscribeToAdded(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) => _pubSub.Subscribe(ContractEvent.Added, handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) => _pubSub.Subscribe(ContractEvent.Removed, handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) => _pubSub.Subscribe(ContractEvent.Stored, handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) => _pubSub.Subscribe(ContractEvent.Deleted, handler, keepAlive);

    public ValueTask ContractAdded(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default) =>
        _pubSub.PublishAsync(ContractEvent.Added, @event, cancellationToken);

    public ValueTask ContractRemoved(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default) =>
        _pubSub.PublishAsync(ContractEvent.Removed, @event, cancellationToken);

    public ValueTask ContractStored(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default) =>
        _pubSub.PublishAsync(ContractEvent.Stored, @event, cancellationToken);

    public ValueTask ContractDeleted(IObjectEnvelope<TContract> @event, CancellationToken cancellationToken = default) =>
        _pubSub.PublishAsync(ContractEvent.Deleted, @event, cancellationToken);

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
        List<Exception>? failures = null;
        try
        {
            await _maintenanceCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

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

        if (hydration is not null && !ReferenceEquals(hydration, initialization))
        {
            try
            {
                await hydration.ConfigureAwait(false);
            }
            catch
            {
                // Hydration failure remains observable to its callers; disposal still owns cleanup.
            }
        }

        var maintenance = Volatile.Read(ref _maintenanceTask);
        if (maintenance is not null)
        {
            try
            {
                await maintenance.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_maintenanceCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (_compactionScheduler is { } scheduler)
        {
            try { await scheduler.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        var store = Interlocked.Exchange(ref _store, null);
        if (store is not null)
        {
            if (_options.CheckpointOnDispose
                && Volatile.Read(ref _totalMutations) > Volatile.Read(ref _checkpointMutationWatermark))
            {
                try
                {
                    await store.CheckpointAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            _maintenanceCancellation.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _identityGate.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new AggregateException("SegmentCore repository disposal encountered one or more failures.", failures);
        }
    }

    private SegmentCoreStore Store => Volatile.Read(ref _store)
        ?? throw new InvalidOperationException("SegmentCore repository has not been initialized.");

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(_storeRoot);
        if (!PersistentSearchIndexCatalogEnabled)
        {
            PurgePlaintextSearchIndexCatalogs();
        }
        var store = await SegmentCoreStore.OpenAsync(_storeRoot,
            _options.StoreOptions with { Telemetry = _storagePolicy.Telemetry ?? _options.StoreOptions.Telemetry,
                RetainedVersions = _storagePolicy.RetainedVersions ?? _options.StoreOptions.RetainedVersions }).ConfigureAwait(false);
        try
        {
            foreach (var key in store.GetKeysSnapshot())
            {
                CacheEnvelopeSkeleton(key);
            }

            Volatile.Write(ref _store, store);
            if (_options.EnableBackgroundMaintenance)
            {
                _maintenanceTask = RunMaintenanceAsync(_maintenanceCancellation.Token);
            }

            Volatile.Write(ref _initialized, 1);
        }
        catch
        {
            Interlocked.CompareExchange(ref _store, null, store);
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void PurgePlaintextSearchIndexCatalogs()
    {
        var directory = SearchIndexCatalogDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("catalog-", StringComparison.Ordinal)
                && (name.EndsWith(".idbsidx", StringComparison.Ordinal)
                    || name.Contains(".idbsidx.creating-", StringComparison.Ordinal)))
            {
                File.Delete(path);
            }
        }
    }

    private async Task HydrateAfterInitializationAsync(Task initialization)
    {
        await initialization.ConfigureAwait(false);
        var hydrationCandidates = CaptureHydrationCandidates();
        if (hydrationCandidates.Count == 0)
        {
            Volatile.Write(ref _hydrated, 1);
            return;
        }

        var records = await Store.ReadManyAsync(hydrationCandidates.Keys.ToArray(), CancellationToken.None).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            records,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(_storagePolicy.MaxConcurrency, 1, 256)
            },
            async (record, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hydrationCandidates.TryGetValue(record.Key, out var expectedCacheEntry)
                    && IsCurrentCacheEntry(record.Key, expectedCacheEntry))
                {
                    await MaterializeAndCacheIfUnchangedAsync(
                        record.Key,
                        record.Payload,
                        expectedCacheEntry,
                        cancellationToken).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        Volatile.Write(ref _hydrated, 1);
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.CheckpointInterval);
        var lastCompaction = Stopwatch.GetTimestamp();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var total = Volatile.Read(ref _totalMutations);
                if (total - Volatile.Read(ref _checkpointMutationWatermark)
                    >= _options.MinimumMutationsPerCheckpoint)
                {
                    await CheckpointAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_options.CompactionSchedule is not null)
                {
                    await RunCompactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else if (Stopwatch.GetElapsedTime(lastCompaction) >= _options.CompactionInterval
                    && total - Volatile.Read(ref _compactionMutationWatermark)
                    >= _options.MinimumMutationsPerCompaction)
                {
                    await CompactAsync(cancellationToken).ConfigureAwait(false);
                    lastCompaction = Stopwatch.GetTimestamp();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastMaintenanceError, exception);
                Idb.Diagnostics.DatabaseTelemetryScope.Start(_storagePolicy.Telemetry ?? _options.StoreOptions.Telemetry,
                    Idb.Diagnostics.DatabaseComponent.SegmentCore, Idb.Diagnostics.DatabaseOperation.Execute, log: true).Fail(exception);
            }
        }
    }

    private async ValueTask<PreparedWrite> PrepareWriteAsync(
        TContract instance,
        CancellationToken cancellationToken)
    {
        if (instance.Envelope?.State == StorageObjectState.Deleted)
        {
            throw new InvalidOperationException("A deleted storage object cannot be written again.");
        }

        var isNew = instance.Envelope is null
            || string.IsNullOrWhiteSpace(instance.Envelope.EnvelopeCacheKey);
        var existingPhysicalKey = instance.Envelope?.EnvelopeCacheKey;
        var envelope = await _envelopeLifecycle.WrapAsync(instance).ConfigureAwait(false);
        var reserved = false;
        try
        {
            if (isNew)
            {
                reserved = await ResolveAndReserveNewIdentityAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(existingPhysicalKey))
                {
                    throw new InvalidDataException("A persisted SegmentCore envelope has no physical storage key.");
                }

                envelope.EnvelopeCacheKey = existingPhysicalKey;
            }

            envelope.Location = envelope.EnvelopeCacheKey;
            envelope.ImplementationTypeName = instance.GetType().AssemblyQualifiedName
                ?? instance.GetType().FullName
                ?? instance.GetType().Name;
            envelope.State = StorageObjectState.Active;
            return new PreparedWrite(
                envelope,
                [],
                isNew,
                reserved);
        }
        catch
        {
            if (reserved)
            {
                _reservedKeys.TryRemove(envelope.EnvelopeCacheKey, out _);
            }

            if (envelope.ObjectInstance is not null)
            {
                _ = _envelopeLifecycle.Unwrap(envelope);
            }

            throw;
        }
    }

    private async ValueTask<bool> ResolveAndReserveNewIdentityAsync(
        IObjectEnvelope<TContract> envelope,
        CancellationToken cancellationToken)
    {
        var logicalKey = envelope.EnvelopeCacheKey;
        if (_identityFactory.ObjectCardinality == StorageObjectCardinality.Multiple)
        {
            if (!_suffixAllocators.TryGetValue(logicalKey, out var allocator))
            {
                var firstSuffix = _envelopes.GetGroupCount(logicalKey);
                var firstPhysicalKey = ToPhysicalKey(logicalKey, firstSuffix);
                if (!_envelopes.ContainsKey(firstPhysicalKey)
                    && _reservedKeys.TryAdd(firstPhysicalKey, 0))
                {
                    envelope.EnvelopeCacheKey = firstPhysicalKey;
                    return true;
                }

                if (firstSuffix == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The logical storage key has exhausted its physical suffix range.");
                }

                allocator = _suffixAllocators.GetOrAdd(
                    logicalKey,
                    static (_, nextSuffix) => new SuffixAllocator(nextSuffix),
                    firstSuffix + 1);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var physicalKey = ToPhysicalKey(logicalKey, allocator.ReserveNext());
                if (!_envelopes.ContainsKey(physicalKey) && _reservedKeys.TryAdd(physicalKey, 0))
                {
                    envelope.EnvelopeCacheKey = physicalKey;
                    return true;
                }
            }
        }

        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_envelopes.GetGroupCount(logicalKey) != 0
                || !_reservedKeys.TryAdd(logicalKey, 0))
            {
                throw new InvalidOperationException(
                    $"A storage object already exists for single-object key '{logicalKey}'.");
            }

            envelope.EnvelopeCacheKey = logicalKey;
            return true;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private async ValueTask<IObjectEnvelope<TContract>> MaterializeAndCacheAsync(
        string key,
        byte[] payload,
        IObjectEnvelope<TContract>? expectedCacheEntry,
        CancellationToken cancellationToken)
    {
        var (envelope, instance, replacementPayload) = Materialize(key, payload);
        if (replacementPayload is not null)
        {
            await ReplaceEncryptedPayloadIfUnchangedAsync(
                key,
                payload,
                replacementPayload,
                cancellationToken).ConfigureAwait(false);
        }
        CacheReadIfUnchanged(key, envelope, instance, expectedCacheEntry);
        return envelope;
    }

    private async ValueTask MaterializeAndCacheIfUnchangedAsync(
        string key,
        byte[] payload,
        IObjectEnvelope<TContract> expectedCacheEntry,
        CancellationToken cancellationToken)
    {
        var (envelope, instance, replacementPayload) = Materialize(key, payload);
        if (replacementPayload is not null)
        {
            await ReplaceEncryptedPayloadIfUnchangedAsync(
                key,
                payload,
                replacementPayload,
                cancellationToken).ConfigureAwait(false);
        }
        CacheReadIfUnchanged(key, envelope, instance, expectedCacheEntry, takeOwnership: true);
    }

    private void CacheReadIfUnchanged(
        string key,
        IObjectEnvelope<TContract> envelope,
        TContract instance,
        IObjectEnvelope<TContract>? expectedCacheEntry, bool takeOwnership = false)
    {
        lock (GetCacheMutationGate(key))
        {
            // Missing entries are not read-admission tokens: never resurrect a removed entity.
            if (expectedCacheEntry is null
                || !_envelopes.TryGetValue(key, out var current)
                || !ReferenceEquals(current, expectedCacheEntry))
            {
                return;
            }

            if (StorageObjectCachePolicy.CanReuseMetadata(current, envelope, instance, _storagePolicy))
            {
                return;
            }

            var cached = CreateCachedEnvelope(envelope, instance, takeOwnership);
            _envelopes.AddOrUpdate(key, ToLogicalKey(key), cached);
        }

        ObservePhysicalKey(key);
    }

    private void RemoveCachedEnvelopeIfUnchanged(string key, IObjectEnvelope<TContract>? expected)
    {
        lock (GetCacheMutationGate(key))
        {
            if (expected is not null && _envelopes.TryGetValue(key, out var current)
                && ReferenceEquals(current, expected))
            {
                _envelopes.Remove(key);
            }
        }
    }

    private (IObjectEnvelope<TContract> Envelope, TContract Instance, byte[]? ReplacementPayload) Materialize(
        string key,
        byte[] payload)
    {
        ValidateStoredPayload(payload, "Stored payload");
        var context = CreateSerializerContext(key);
        var result = ContextualObjectSerialization.DeserializeForRead(
            _serializer,
            payload,
            context);
        var envelope = result.Envelope;
        if (!string.Equals(envelope.EnvelopeCacheKey, key, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SegmentCore key '{key}' does not match serialized envelope key '{envelope.EnvelopeCacheKey}'.");
        }

        _revisions.ObserveCommittedRevision(key, envelope.RevisionTimestampUtcMicroseconds);
        envelope.Location = key;
        envelope.State = StorageObjectState.Active;
        var instance = _envelopeLifecycle.Unwrap(envelope);
        envelope.ImplementationTypeName ??= instance.GetType().AssemblyQualifiedName
            ?? instance.GetType().FullName
            ?? instance.GetType().Name;
        CommittedCacheCopies<TContract>.Remember(envelope, result.ReplacementPayload ?? payload,
            _serializer, _envelopeLifecycle, _storagePolicy, context);
        return (envelope, instance, result.ReplacementPayload);
    }

    private byte[] SerializePayload(IObjectEnvelope<TContract> envelope, string physicalKey)
    {
        var context = CreateSerializerContext(physicalKey);
        var payload = ValidateStoredPayload(ContextualObjectSerialization.Serialize(_serializer, envelope, context), "Serialized payload");
        CommittedCacheCopies<TContract>.Remember(envelope, payload, _serializer, _envelopeLifecycle, _storagePolicy, context, prepareCacheCopy: true);
        return payload;
    }

    private byte[] ValidateStoredPayload(byte[] payload, string description)
    {
        if (payload.Length > _storagePolicy.NormalizedMaximumStoredObjectBytes)
        {
            throw new InvalidDataException(
                $"{description} is {payload.Length} bytes, exceeding the configured " +
                $"{_storagePolicy.NormalizedMaximumStoredObjectBytes}-byte limit.");
        }

        return payload;
    }

    private async ValueTask ReplaceEncryptedPayloadIfUnchangedAsync(
        string key,
        byte[] expectedPayload,
        byte[] replacementPayload,
        CancellationToken cancellationToken)
    {
        ValidateStoredPayload(replacementPayload, "Rotated payload");
        using var writeGuard = await _revisions.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        var current = await Store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.AsSpan().SequenceEqual(expectedPayload))
        {
            return;
        }

        await Store.SaveOwnedAsync(key, replacementPayload, transaction: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private byte[] CreateEncryptionContext(string physicalKey)
        => StorageEncryptionContext.Create(_contractConfig, "segment-core", physicalKey);

    // Only contextual serializers consume associated data. Migration decrypts directly
    // and retains CreateEncryptionContext independently of the serializer capability.
    private byte[] CreateSerializerContext(string physicalKey)
        => _serializerConsumesContext ? CreateEncryptionContext(physicalKey) : Array.Empty<byte>();

    private void CacheEnvelopeSkeleton(string physicalKey)
    {
        var envelope = _envelopeFactory();
        envelope.EnvelopeCacheKey = physicalKey;
        envelope.VersionTag = string.IsNullOrWhiteSpace(_contractConfig.ActiveVersion)
            ? "1.0.0"
            : _contractConfig.ActiveVersion;
        envelope.Location = physicalKey;
        envelope.ObjectInstance = null;
        envelope.ObjectInstanceWeakReference = null;
        envelope.State = StorageObjectState.Active;
        lock (GetCacheMutationGate(physicalKey))
        {
            _envelopes.AddOrUpdate(physicalKey, ToLogicalKey(physicalKey), envelope);
        }

        ObservePhysicalKey(physicalKey);
    }

    private void CacheEnvelope(IObjectEnvelope<TContract> hydrated, TContract instance)
    {
        var cached = CreateCachedEnvelope(hydrated, instance);
        lock (GetCacheMutationGate(cached.EnvelopeCacheKey))
        {
            _envelopes.AddOrUpdate(
                cached.EnvelopeCacheKey,
                ToLogicalKey(cached.EnvelopeCacheKey),
                cached);
        }

        ObservePhysicalKey(cached.EnvelopeCacheKey);
    }

    private IObjectEnvelope<TContract> CreateCachedEnvelope(
        IObjectEnvelope<TContract> hydrated,
        TContract instance, bool takeOwnership = false)
    {
        var caller = instance;
        if (!takeOwnership && StorageObjectCachePolicy.ResolveObjectCacheMode(instance, _storagePolicy) != StorageObjectCacheMode.MetadataOnly)
        {
            hydrated = CommittedCacheCopies<TContract>.Copy(hydrated, retainCache: false);
            instance = hydrated.ObjectInstance!;
            CommittedCacheCopies<TContract>.KeepAlive(caller, instance);
        }
        IObjectEnvelope<TContract> cached;
        if (StorageObjectCachePolicy.ResolveObjectCacheMode(instance, _storagePolicy)
            == StorageObjectCacheMode.StrongObject)
        {
            cached = hydrated;
            StorageObjectCachePolicy.StoreObjectReference(cached, instance, _storagePolicy);
        }
        else
        {
            cached = _envelopeFactory();
            cached.EnvelopeCacheKey = hydrated.EnvelopeCacheKey;
            cached.VersionTag = hydrated.VersionTag;
            cached.Location = hydrated.Location;
            cached.ImplementationTypeName = hydrated.ImplementationTypeName;
            cached.CacheObjectInstance = hydrated.CacheObjectInstance;
            cached.EncryptedFields = new Dictionary<string, EncryptedFieldValue>(
                hydrated.EncryptedFields,
                StringComparer.Ordinal);
            cached.ExtensionMetadata = hydrated.ExtensionMetadata?.ToArray();
            cached.RevisionTimestampUtcMicroseconds = hydrated.RevisionTimestampUtcMicroseconds;
            cached.State = hydrated.State;
            StorageObjectCachePolicy.StoreObjectReference(cached, instance, _storagePolicy);
        }

        return cached;
    }

    private Dictionary<string, IObjectEnvelope<TContract>> CaptureHydrationCandidates()
    {
        var candidates = new Dictionary<string, IObjectEnvelope<TContract>>(StringComparer.Ordinal);
        foreach (var pair in _envelopes)
        {
            lock (GetCacheMutationGate(pair.Key))
            {
                if (_envelopes.TryGetValue(pair.Key, out var current)
                    && ReferenceEquals(current, pair.Value)
                    && !StorageObjectCachePolicy.TryGetObjectInstance(current, out _))
                {
                    candidates.Add(pair.Key, current);
                }
            }
        }

        return candidates;
    }

    private bool IsCurrentCacheEntry(string key, IObjectEnvelope<TContract> expected)
    {
        lock (GetCacheMutationGate(key))
        {
            return _envelopes.TryGetValue(key, out var current)
                && ReferenceEquals(current, expected);
        }
    }

    private void RemoveCachedEnvelope(string key)
    {
        lock (GetCacheMutationGate(key))
        {
            _envelopes.Remove(key);
        }
    }

    private object GetCacheMutationGate(string key)
    {
        var hashCode = StringComparer.Ordinal.GetHashCode(key);
        return _cacheMutationGates[(int)((uint)hashCode % CacheMutationGateCount)];
    }

    private static object[] CreateCacheMutationGates()
    {
        var gates = new object[CacheMutationGateCount];
        for (var index = 0; index < gates.Length; index++)
        {
            gates[index] = new object();
        }

        return gates;
    }

    private void ObservePhysicalKey(string physicalKey)
    {
        if (_identityFactory.ObjectCardinality != StorageObjectCardinality.Multiple
            || !TrySplitPhysicalKey(physicalKey, out var logicalKey, out var suffix))
        {
            return;
        }

        var nextSuffix = suffix == int.MaxValue ? int.MaxValue : suffix + 1;
        if (suffix == 0 && !_suffixAllocators.TryGetValue(logicalKey, out _))
        {
            // The one-object-per-logical-key case can derive its next suffix from the group count.
            // Avoid one allocator object and dictionary entry for every default unique identity.
            return;
        }

        var allocator = _suffixAllocators.GetOrAdd(
            logicalKey,
            static (_, firstSuffix) => new SuffixAllocator(firstSuffix),
            nextSuffix);
        allocator.AdvanceToAtLeast(nextSuffix);
    }

    private void ReleaseReservation(PreparedWrite item)
    {
        if (item.Reserved)
        {
            _reservedKeys.TryRemove(item.Envelope.EnvelopeCacheKey, out _);
        }
    }

    private void EnsureInitializedSynchronously() =>
        EnsureInitializedAsync(hydrateOnInitialize: false, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private static DateTimeOffset? ToTimestamp(long utcTicks) =>
        utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);

    private static string ToPhysicalKey(string logicalKey, int index) =>
        EnvelopeCacheKey.ToPhysicalKey(logicalKey, index);

    private string ToLogicalKey(string physicalKey)
    {
        return _identityFactory.ObjectCardinality == StorageObjectCardinality.Multiple
            && TrySplitPhysicalKey(physicalKey, out var logicalKey, out _)
            ? logicalKey
            : physicalKey;
    }

    private static bool TrySplitPhysicalKey(string physicalKey, out string logicalKey, out int suffix)
        => EnvelopeCacheKey.TrySplitPhysicalKey(physicalKey, out logicalKey, out suffix);

    private static bool IsPathWithin(string candidatePath, string parentPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var parent = Path.GetFullPath(parentPath);
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountCommitted(List<PreparedWrite> prepared)
    {
        var count = 0;
        for (var index = 0; index < prepared.Count; index++)
            if (!EnvelopeWriteOutcome.IsNoChange(prepared[index].Envelope)) count++;
        return count;
    }
    private sealed record PreparedWrite(
        IObjectEnvelope<TContract> Envelope,
        byte[] Payload,
        bool IsNew,
        bool Reserved);

    private sealed class SuffixAllocator
    {
        private int _nextSuffix;

        public SuffixAllocator(int nextSuffix)
        {
            _nextSuffix = Math.Max(0, nextSuffix);
        }

        public int ReserveNext()
        {
            while (true)
            {
                var current = Volatile.Read(ref _nextSuffix);
                if (current == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The logical storage key has exhausted its physical suffix range.");
                }

                if (Interlocked.CompareExchange(ref _nextSuffix, current + 1, current) == current)
                {
                    return current;
                }
            }
        }

        public void AdvanceToAtLeast(int nextSuffix)
        {
            while (true)
            {
                var current = Volatile.Read(ref _nextSuffix);
                if (current >= nextSuffix
                    || Interlocked.CompareExchange(ref _nextSuffix, nextSuffix, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class SegmentCoreReaderAndWriter : IReaderAndWriter<TContract>
    {
        public SegmentCoreReaderAndWriter(SegmentCoreEnvelopeRepository<TContract> owner)
        {
            Reader = new SegmentCoreObjectReader(owner);
            Writer = new SegmentCoreObjectWriter(owner);
        }

        public IObjectReader<TContract> Reader { get; }

        public IObjectWriter<TContract> Writer { get; }
    }

    private sealed class SegmentCoreObjectReader(SegmentCoreEnvelopeRepository<TContract> owner)
        : IObjectReader<TContract>
    {
        public async Task<TContract> ReadAsync(
            FileInfo file,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            var envelope = await owner.GetPhysicalAsync(
                Path.GetFileNameWithoutExtension(file.Name),
                cancellationToken).ConfigureAwait(false);
            return envelope?.ObjectInstance
                ?? throw new FileNotFoundException("SegmentCore object was not found.", file.FullName);
        }
    }

    private sealed class SegmentCoreObjectWriter(SegmentCoreEnvelopeRepository<TContract> owner)
        : IObjectWriter<TContract>
    {
        public ValueTask<TContract> WriteAsync(
            TContract envalope,
            CancellationToken cancellationToken = default) => owner.WriteAsync(envalope, cancellationToken);

        public ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
            IEnumerable<TContract> envalopes,
            CancellationToken cancellationToken = default) => owner.WriteManyAsync(envalopes, cancellationToken);
    }
}

internal enum ContractEvent
{
    Added,
    Removed,
    Stored,
    Deleted
}

internal sealed class ContractPubSubHub<TContract>(IDatabaseTelemetry? telemetry)
    where TContract : class, IStoredContract<TContract>
{
    private readonly ConcurrentDictionary<ContractEvent, ConcurrentDictionary<Guid, Subscription>> _subscriptions = new();

    public IContractSubscription Subscribe(
        ContractEvent eventType,
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var bucket = _subscriptions.GetOrAdd(eventType, static _ => new());
        var subscription = new Subscription(bucket, handler, keepAlive);
        bucket[subscription.Id] = subscription;
        return subscription;
    }

    public async ValueTask PublishAsync(
        ContractEvent eventType,
        IObjectEnvelope<TContract> payload,
        CancellationToken cancellationToken)
    {
        if (!_subscriptions.TryGetValue(eventType, out var bucket))
        {
            return;
        }

        foreach (var subscription in bucket.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (subscription.KeepAlive is not null
                    && !await subscription.KeepAlive().ConfigureAwait(false))
                {
                    subscription.Dispose();
                    continue;
                }

                await subscription.Handler(payload).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                subscription.Dispose();
                DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Publish, log: true).Fail(error);
            }
        }
    }

    private sealed class Subscription(
        ConcurrentDictionary<Guid, Subscription> bucket,
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive) : IContractSubscription
    {
        private int _disposed;

        public Guid Id { get; } = Guid.NewGuid();

        public Func<IObjectEnvelope<TContract>, ValueTask> Handler { get; } = handler;

        public Func<ValueTask<bool>>? KeepAlive { get; } = keepAlive;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bucket.TryRemove(Id, out _);
            }
        }
    }
}
