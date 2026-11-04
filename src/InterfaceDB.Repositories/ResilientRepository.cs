using System.Collections.Concurrent;
using System.Threading.Channels;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Combines two provider-neutral repositories behind a preferred-first, offline-resilient API.
/// </summary>
/// <remarks>
/// The resilient metadata exclusively occupies each endpoint envelope's extension-metadata field.
/// Local/remote composition requires one owner of each local cache. Remote writes use conditional
/// revisions; pending local edits retain their original remote base and report concurrent conflicts.
/// The legacy preferred/peer policy retains its documented single-writer assumptions.
/// </remarks>
public sealed partial class ResilientRepository<TContract> : IResilientRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private const int KeyGateCount = 64;
    private const int MaximumDatasetIdLength = 256;
    private const int MaximumStorageKeyLength = 1024;

    private readonly IRepository<TContract> _preferred;
    private readonly IRepository<TContract> _peer;
    private readonly IEnvelopeMetadataRepository<TContract> _preferredMetadataRepository;
    private readonly IEnvelopeMetadataRepository<TContract> _peerMetadataRepository;
    private readonly IResilientEntityMapper<TContract> _mapper;
    private readonly IResilientRepositoryPolicy<TContract> _policy;
    private readonly IResilientSynchronizationStateStore _synchronizationStateStore;
    private readonly SemaphoreSlim[] _keyGates;
    private readonly SemaphoreSlim[] _synchronizationGates;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly ConcurrentDictionary<string, byte> _queuedSynchronizationKeys =
        new(StringComparer.Ordinal);
    private readonly Channel<byte> _synchronizationWakeSignal =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private Task? _backgroundWorker;
    private bool _initialized;
    private bool _preferredInitialized;
    private bool _peerInitialized;
    private int _synchronizeAllRequested;
    private int _disposed;

    public ResilientRepository(
        IRepository<TContract> preferred,
        IRepository<TContract> peer,
        IResilientEntityMapper<TContract> mapper,
        IResilientRepositoryPolicy<TContract>? policy = null,
        IResilientSynchronizationStateStore? synchronizationStateStore = null)
    {
        _preferred = preferred ?? throw new ArgumentNullException(nameof(preferred));
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));

        if (ReferenceEquals(preferred, peer))
        {
            throw new ArgumentException("Preferred and peer must be different repository instances.", nameof(peer));
        }

        _preferredMetadataRepository = preferred as IEnvelopeMetadataRepository<TContract>
            ?? throw new ArgumentException(
                "The preferred repository must support atomic extension-metadata saves.",
                nameof(preferred));
        _peerMetadataRepository = peer as IEnvelopeMetadataRepository<TContract>
            ?? throw new ArgumentException(
                "The peer repository must support atomic extension-metadata saves.",
                nameof(peer));
        EnsureDurableEndpoint(_preferredMetadataRepository, ResilientEndpoint.Preferred, nameof(preferred));
        EnsureDurableEndpoint(_peerMetadataRepository, ResilientEndpoint.Peer, nameof(peer));

        _policy = policy switch
        {
            null => new ResilientRepositoryOptions<TContract>().Snapshot(),
            ResilientRepositoryOptions<TContract> options => options.Snapshot(),
            _ => policy
        };
        ValidatePolicy(_policy);
        _synchronizationStateStore = synchronizationStateStore
            ?? new InMemoryResilientSynchronizationStateStore(_policy.MaximumPendingSynchronizationKeys);
        _keyGates = Enumerable.Range(0, KeyGateCount)
            .Select(static _ => new SemaphoreSlim(1, 1))
            .ToArray();
        _synchronizationGates = Enumerable.Range(0, KeyGateCount)
            .Select(static _ => new SemaphoreSlim(1, 1))
            .ToArray();
    }

    /// <summary>
    /// Reports the preferred provider type without exposing either endpoint.
    /// </summary>
    public ContractRepositoryType RepositoryType => _preferred.RepositoryType;
    public string? RunningVersion => CompositionOptions is null ? _preferred.RunningVersion : _peer.RunningVersion;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (CompositionOptions is not null) { await InitializeCompositionAsync(cancellationToken).ConfigureAwait(false); return; }
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_preferredInitialized && _peerInitialized)
            {
                return;
            }

            Exception? preferredFailure = null;
            if (!_preferredInitialized)
            {
                preferredFailure = await InitializeEndpointAsync(
                        _preferred,
                        ResilientEndpoint.Preferred,
                        cancellationToken)
                    .ConfigureAwait(false);
                _preferredInitialized = preferredFailure is null;
            }

            Exception? peerFailure = null;
            if (!_peerInitialized)
            {
                peerFailure = await InitializeEndpointAsync(
                        _peer,
                        ResilientEndpoint.Peer,
                        cancellationToken)
                    .ConfigureAwait(false);
                _peerInitialized = peerFailure is null;
            }

            if (!_preferredInitialized && !_peerInitialized)
            {
                throw new ResilientRepositoryAvailabilityException(
                    ResilientRepositoryOperation.Initialize,
                    storageKey: null,
                    isPartialResult: false,
                    preferredFailure,
                    peerFailure);
            }

            if (!_initialized)
            {
                _initialized = true;
                if (_policy.EnableBackgroundSynchronization)
                {
                    _backgroundWorker = RunBackgroundSynchronizationAsync(_disposeCancellation.Token);
                    QueueSynchronization(storageKey: null);
                }
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask<TContract> SaveAsync(
        TContract entityToSave,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entityToSave);

        if (CompositionOptions is not null) return (await WriteCompositionAsync(entityToSave, false, cancellationToken).ConfigureAwait(false)).Entity;
        var storageKey = GetInputStorageKey(entityToSave);
        EnsurePreferredWritable(storageKey);
        var keyGate = GetKeyGate(storageKey);
        await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var preferredRead = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preferredRead.State == EndpointReadState.Unavailable)
            {
                throw new ResilientRepositoryAvailabilityException(
                    ResilientRepositoryOperation.Save,
                    storageKey,
                    isPartialResult: false,
                    preferredRead.Failure,
                    peerException: null);
            }

            var existingPreferred = preferredRead.Entity;
            var existingMetadata = ReadMetadata(existingPreferred);

            var metadata = new ResilientEnvelopeMetadata(
                ResilientEnvelopeMetadata.CurrentFormatVersion,
                0,
                IsTombstone: false,
                PendingMutationId: Guid.NewGuid(),
                PendingMutationKind.Upsert,
                existingMetadata.LastAppliedMutationId);
            var candidate = PreparePreferredMutationCandidate(entityToSave, existingPreferred, storageKey);
            var canonical = await SaveWithMetadataAsync(
                    _preferredMetadataRepository,
                    candidate,
                    GetExistingEnvelopeCacheKey(existingPreferred, ResilientEndpoint.Preferred, storageKey),
                    metadata,
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Save,
                    storageKey,
                    cancellationToken,
                    new EnvelopeWriteCondition(entityToSave.Envelope?.RevisionTimestampUtcMicroseconds ?? 0))
                .ConfigureAwait(false);

            QueueSynchronization(storageKey);
            return CreateApplicationCopy(canonical, storageKey);
        }
        finally
        {
            keyGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TContract>> SaveManyAsync(
        IEnumerable<TContract> entitiesToSave,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entitiesToSave);

        var entities = CompositionOptions is null
            ? entitiesToSave as IReadOnlyList<TContract> ?? entitiesToSave.ToArray()
            : entitiesToSave.Take(checked(_policy.MaximumSynchronizationScanItems + 1)).ToArray();
        if (CompositionOptions is not null && entities.Count > _policy.MaximumSynchronizationScanItems)
            throw new ArgumentException("The batch exceeds the composition record limit.", nameof(entitiesToSave));
        if (entities.Count == 0)
        {
            return Array.Empty<TContract>();
        }

        var saved = new List<TContract>(entities.Count);
        foreach (var entity in entities)
        {
            ArgumentNullException.ThrowIfNull(entity);
            saved.Add(await SaveAsync(entity, cancellationToken).ConfigureAwait(false));
        }

        return saved;
    }

    /// <summary>
    /// Accepts a logical delete by durably storing a tombstone. The return value retains the
    /// <see cref="IRepository{TContract}"/> contract: true only when a live logical entity was
    /// confirmed. A missing key is still tombstoned to prevent a stale peer from resurrecting it.
    /// </summary>
    public async ValueTask<bool> DeleteAsync(
        TContract entityToDelete,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entityToDelete);

        if (CompositionOptions is not null) return (await WriteCompositionAsync(entityToDelete, true, cancellationToken).ConfigureAwait(false)).Existed;
        var storageKey = GetInputStorageKey(entityToDelete);
        EnsurePreferredWritable(storageKey);
        var keyGate = GetKeyGate(storageKey);
        await keyGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var preferredRead = await ReadEndpointByStorageKeyAsync(
                    _preferred,
                    ResilientEndpoint.Preferred,
                    storageKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (preferredRead.State == EndpointReadState.Unavailable)
            {
                throw new ResilientRepositoryAvailabilityException(
                    ResilientRepositoryOperation.Delete,
                    storageKey,
                    isPartialResult: false,
                    preferredRead.Failure,
                    peerException: null);
            }

            var existingMetadata = ReadMetadata(preferredRead.Entity);
            if (preferredRead.State == EndpointReadState.Found && existingMetadata.IsTombstone)
            {
                return false;
            }

            var activeStorageExisted = preferredRead.State == EndpointReadState.Found;
            var observedRevision = existingMetadata.RevisionTimestampUtcMicroseconds;
            TContract? peerTombstoneSource = null;
            if (preferredRead.State == EndpointReadState.Missing)
            {
                var peerRead = await ReadEndpointByStorageKeyAsync(
                        _peer,
                        ResilientEndpoint.Peer,
                        storageKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (peerRead.State == EndpointReadState.Found)
                {
                    peerTombstoneSource = peerRead.Entity;
                    var peerMetadata = ReadMetadata(peerRead.Entity);
                    activeStorageExisted = !peerMetadata.IsTombstone;
                    observedRevision = Math.Max(
                        observedRevision,
                        peerMetadata.RevisionTimestampUtcMicroseconds);
                }
            }

            var metadata = new ResilientEnvelopeMetadata(
                ResilientEnvelopeMetadata.CurrentFormatVersion,
                0,
                IsTombstone: true,
                PendingMutationId: Guid.NewGuid(),
                PendingMutationKind.Delete,
                existingMetadata.LastAppliedMutationId);
            // Delete identity comes from the repository-issued logical envelope when present.
            // Retain persisted values only as an opaque tombstone payload; caller-side edits must
            // neither retarget the delete nor leak into either provider cache.
            var candidate = CreateDetachedCopy(
                preferredRead.Entity ?? peerTombstoneSource ?? entityToDelete,
                storageKey);

            await SaveWithMetadataAsync(
                    _preferredMetadataRepository,
                    candidate,
                    GetExistingEnvelopeCacheKey(
                        preferredRead.Entity,
                        ResilientEndpoint.Preferred,
                        storageKey),
                    metadata,
                    ResilientEndpoint.Preferred,
                    ResilientRepositoryOperation.Delete,
                    storageKey,
                    cancellationToken,
                    new EnvelopeWriteCondition(entityToDelete.Envelope?.RevisionTimestampUtcMicroseconds ?? 0))
                .ConfigureAwait(false);

            QueueSynchronization(storageKey);
            return activeStorageExisted;
        }
        finally
        {
            keyGate.Release();
        }
    }

    public async ValueTask<bool> ExistsAsync(
        TContract entityToCheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityToCheck);
        var storageKey = GetInputStorageKey(entityToCheck);
        return await QueryFirstByStorageKeyAsync(storageKey, cancellationToken).ConfigureAwait(false) is not null;
    }

    public async ValueTask NotifyChangedAsync(
        RepositoryChangeSignal signal,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signal);
        ValidateChangeSignal(signal);
        if (CompositionOptions is not null) Interlocked.Exchange(ref _compositionDirty, 1);

        try
        {
            await _synchronizationStateStore.MarkChangedAsync(signal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportFailure(
                endpoint: null,
                ResilientRepositoryOperation.NotifyChanged,
                signal.StorageKey,
                exception);
            throw;
        }

        QueueSynchronization(signal.StorageKey);
    }

    public ValueTask<ResilientSynchronizationState> GetSynchronizationStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _synchronizationStateStore.GetAsync(_policy.DatasetId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _compositionLocalSubscription?.Dispose();
        Connection.Dispose();
        _compositionChanges.Dispose();
        _synchronizationWakeSignal.Writer.TryComplete();
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        if (_backgroundWorker is not null)
        {
            try
            {
                await _backgroundWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
            }
        }

        await _compositionGate.WaitAsync().ConfigureAwait(false);
        _compositionGate.Release();
        _disposeCancellation.Dispose();
        _initializationGate.Dispose();
        foreach (var keyGate in _keyGates)
        {
            keyGate.Dispose();
        }

        foreach (var synchronizationGate in _synchronizationGates)
        {
            synchronizationGate.Dispose();
        }
    }

    private TContract PreparePreferredMutationCandidate(
        TContract requestedEntity,
        TContract? existingPreferred,
        string storageKey)
    {
        // Never attach pending metadata to a store-owned cached envelope before the provider
        // confirms its durable commit. Stage all mutations on an envelope-free copy.
        return PrepareStagedCopy(requestedEntity, existingPreferred, storageKey);
    }

    private TContract PrepareStagedCopy(
        TContract source,
        TContract? target,
        string storageKey)
    {
        if (target is null)
        {
            return CreateDetachedCopy(source, storageKey);
        }

        var stagedTarget = CreateDetachedCopy(target, storageKey);
        _mapper.CopyValues(source, stagedTarget);
        if (stagedTarget.Envelope is not null)
        {
            throw new InvalidOperationException(
                "The resilient entity mapper attached an envelope while copying values.");
        }

        EnsureExpectedStorageKey(stagedTarget, storageKey);
        return stagedTarget;
    }

    private TContract CreateDetachedCopy(TContract source, string expectedStorageKey)
    {
        var detached = _mapper.CreateDetachedCopy(source)
            ?? throw new InvalidOperationException("The resilient entity mapper returned a null detached copy.");
        if (ReferenceEquals(source, detached))
        {
            throw new InvalidOperationException("The resilient entity mapper returned the source instance.");
        }

        if (detached.Envelope is not null)
        {
            throw new InvalidOperationException("A detached resilient copy must not contain an envelope.");
        }

        EnsureExpectedStorageKey(detached, expectedStorageKey);
        return detached;
    }

    private TItem CreateApplicationCopy<TItem>(TItem source, string storageKey)
        where TItem : class, TContract
    {
        var detached = CreateDetachedCopy(source, storageKey);
        if (detached is not TItem applicationCopy)
        {
            throw new InvalidOperationException(
                "The resilient entity mapper changed the runtime type of an application copy.");
        }

        var logicalEnvelope = new ResilientApplicationEnvelope<TContract>(
            storageKey,
            source.Envelope?.VersionTag ?? string.Empty,
            applicationCopy)
        {
            RevisionTimestampUtcMicroseconds = ReadMetadata(source).RevisionTimestampUtcMicroseconds
        };
        ResilientStoredContractEnvelopeAccessor<TContract>.SetEnvelope(
            applicationCopy,
            logicalEnvelope);
        if (!ReferenceEquals(applicationCopy.Envelope, logicalEnvelope))
        {
            throw new InvalidOperationException(
                "The stored contract did not retain its resilient logical envelope.");
        }

        return applicationCopy;
    }

    private string GetInputStorageKey(TContract entity)
        => entity.Envelope is ResilientApplicationEnvelope<TContract> logicalEnvelope
            ? logicalEnvelope.OriginalStorageKey
            : GetRequiredStorageKey(entity);

    private void ReportPeerAuthorityInvariant(
        string storageKey,
        ResilientEnvelopeMetadata preferredMetadata,
        EndpointReadState peerState,
        ResilientEnvelopeMetadata peerMetadata,
        ResilientRepositoryOperation operation)
    {
        var peerDescription = peerState == EndpointReadState.Missing
            ? "is missing"
            : $"has revision {peerMetadata.RevisionTimestampUtcMicroseconds}";
        ReportFailure(
            ResilientEndpoint.Peer,
            operation,
            storageKey,
            new InvalidOperationException(
                $"Peer-authoritative key '{storageKey}' has preferred revision "
                + $"{preferredMetadata.RevisionTimestampUtcMicroseconds}, but the peer {peerDescription}. "
                + "The preferred value was preserved but cannot be certified current."));
    }

    private string GetRequiredStorageKey(TContract entity)
    {
        var storageKey = _mapper.GetStorageKey(entity);
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaximumStorageKeyLength)
        {
            throw new InvalidOperationException(
                $"The resilient entity mapper must return a storage key between 1 and {MaximumStorageKeyLength} characters.");
        }

        return storageKey;
    }

    private void EnsureExpectedStorageKey(TContract entity, string expectedStorageKey)
    {
        var actualStorageKey = GetRequiredStorageKey(entity);
        if (!string.Equals(actualStorageKey, expectedStorageKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The resilient entity mapper changed storage identity from '{expectedStorageKey}' to '{actualStorageKey}'.");
        }
    }

    private static ResilientEnvelopeMetadata ReadMetadata(TContract? entity)
        => entity?.Envelope is null
            ? ResilientEnvelopeMetadata.Empty
            : ResilientEnvelopeMetadataCodec.Decode(entity.Envelope.ExtensionMetadata) with
            {
                RevisionTimestampUtcMicroseconds = entity.Envelope.RevisionTimestampUtcMicroseconds > 0
                    ? entity.Envelope.RevisionTimestampUtcMicroseconds
                    : ResilientEnvelopeMetadataCodec.Decode(entity.Envelope.ExtensionMetadata).RevisionTimestampUtcMicroseconds
            };

    private async ValueTask<TContract> SaveWithMetadataAsync(
        IEnvelopeMetadataRepository<TContract> repository,
        TContract detachedEntity,
        string? existingEnvelopeCacheKey,
        ResilientEnvelopeMetadata metadata,
        ResilientEndpoint endpoint,
        ResilientRepositoryOperation operation,
        string storageKey,
        CancellationToken cancellationToken,
        EnvelopeWriteCondition condition)
    {
        // Version 2 retains field positions for v1 reads; the revision is authoritative in the free envelope.
        var storedMetadata = metadata with { FormatVersion = 2, RevisionTimestampUtcMicroseconds = 0 };
        var encodedMetadata = ResilientEnvelopeMetadataCodec.Encode(storedMetadata);
        var canonical = await repository
            .SaveWithExtensionMetadataAsync(
                detachedEntity,
                existingEnvelopeCacheKey,
                encodedMetadata,
                condition,
                cancellationToken)
            .ConfigureAwait(false);
        var committedMetadata = ReadMetadata(canonical);
        if ((committedMetadata with { RevisionTimestampUtcMicroseconds = 0 }) != storedMetadata)
        {
            throw new InvalidOperationException(
                $"The {endpoint} endpoint did not return the extension metadata committed during {operation} for '{storageKey}'.");
        }

        if (string.IsNullOrWhiteSpace(canonical.Envelope?.EnvelopeCacheKey))
        {
            throw new InvalidOperationException(
                $"The {endpoint} endpoint did not return a provider-owned envelope cache key "
                + $"after committing '{storageKey}'.");
        }

        EnsureExpectedStorageKey(canonical, storageKey);

        return canonical;
    }

    private static string? GetExistingEnvelopeCacheKey(
        TContract? existingEntity,
        ResilientEndpoint endpoint,
        string storageKey)
    {
        if (existingEntity is null)
        {
            return null;
        }

        var envelopeCacheKey = existingEntity.Envelope?.EnvelopeCacheKey;
        if (string.IsNullOrWhiteSpace(envelopeCacheKey))
        {
            throw new InvalidDataException(
                $"The existing {endpoint} entity for '{storageKey}' has no provider envelope cache key.");
        }

        return envelopeCacheKey;
    }

    private static void EnsureDurableEndpoint(
        IEnvelopeMetadataRepository<TContract> repository,
        ResilientEndpoint endpoint,
        string parameterName)
    {
        if (repository.CommitDurability != StorageDurabilityMode.Durable)
        {
            throw new ArgumentException(
                $"The {endpoint} repository must provide durable commit semantics for resilient synchronization.",
                parameterName);
        }
    }

    private SemaphoreSlim GetKeyGate(string storageKey)
    {
        var hash = StringComparer.Ordinal.GetHashCode(storageKey) & int.MaxValue;
        return _keyGates[hash % _keyGates.Length];
    }

    private SemaphoreSlim GetSynchronizationGate(string storageKey)
    {
        var hash = StringComparer.Ordinal.GetHashCode(storageKey) & int.MaxValue;
        return _synchronizationGates[hash % _synchronizationGates.Length];
    }

    private void EnsurePreferredWritable(string storageKey)
    {
        var authority = GetMutationAuthority(storageKey);
        if (authority == MutationAuthority.PeerAuthoritativeReadOnly)
        {
            throw new InvalidOperationException(
                $"Storage key '{storageKey}' is peer-authoritative and cannot be mutated locally.");
        }
    }

    private MutationAuthority GetMutationAuthority(string storageKey)
    {
        var authority = _policy.GetMutationAuthority(storageKey);
        return Enum.IsDefined(authority)
            ? authority
            : throw new InvalidOperationException(
                "The resilient repository policy returned an invalid mutation authority.");
    }

    private void ValidateChangeSignal(RepositoryChangeSignal signal)
    {
        if (!string.Equals(signal.DatasetId, _policy.DatasetId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Change signal dataset '{signal.DatasetId}' does not match '{_policy.DatasetId}'.",
                nameof(signal));
        }

        if (signal.DatasetId.Length > MaximumDatasetIdLength)
        {
            throw new ArgumentException(
                $"A change signal dataset identifier cannot exceed {MaximumDatasetIdLength} characters.",
                nameof(signal));
        }

        if (signal.StorageKey is not null
            && (string.IsNullOrWhiteSpace(signal.StorageKey)
                || signal.StorageKey.Length > MaximumStorageKeyLength))
        {
            throw new ArgumentException(
                $"A change signal storage key must be between 1 and {MaximumStorageKeyLength} characters.",
                nameof(signal));
        }

        if (signal.RevisionTimestampUtcMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signal), "A revision timestamp cannot be negative.");
        }

        if (signal.Checkpoint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signal), "A checkpoint cannot be negative.");
        }
    }

    private async ValueTask<Exception?> InitializeEndpointAsync(
        IRepository<TContract> repository,
        ResilientEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            RepositoryDataAvailability.RequireComplete(repository);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            ReportFailure(endpoint, ResilientRepositoryOperation.Initialize, storageKey: null, exception);
            return exception;
        }
    }

    private bool IsUnavailable(Exception exception)
    {
        return _policy.IsEndpointUnavailable(exception);
    }

    private bool IsEndpointWideFailure(Exception exception)
    {
        return _policy.IsEndpointWideFailure(exception);
    }

    private void ReportFailure(
        ResilientEndpoint? endpoint,
        ResilientRepositoryOperation operation,
        string? storageKey,
        Exception exception)
    {
        var isTransient = false;
        try
        {
            isTransient = IsUnavailable(exception);
        }
        catch
        {
            // A diagnostics path must not call a failing policy classifier into the operation.
        }

        try
        {
            Idb.Diagnostics.DatabaseTelemetryScope.Start(_policy.Telemetry,
                Idb.Diagnostics.DatabaseComponent.LocalRemote, Idb.Diagnostics.DatabaseOperation.Refresh, log: true)
                .Fail(exception, Idb.Diagnostics.DatabaseTelemetryScope.Classify(exception), recoverable: isTransient);
        }
        catch { /* A custom diagnostics policy cannot change repository behavior. */ }

        var failure = new ResilientRepositoryFailure(
            endpoint,
            operation,
            storageKey,
            exception,
            isTransient);
        ThreadPool.QueueUserWorkItem(
            static notification =>
            {
                try
                {
                    notification.Policy.ReportFailure(notification.Failure);
                }
                catch
                {
                    // Diagnostics cannot alter repository behavior.
                }
            },
            new FailureNotification(_policy, failure),
            preferLocal: false);
    }

    private void QueueSynchronization(string? storageKey)
    {
        if (_initialized && _policy.EnableBackgroundSynchronization && Volatile.Read(ref _disposed) == 0)
        {
            if (storageKey is null
                || _queuedSynchronizationKeys.Count >= _policy.MaximumPendingSynchronizationKeys)
            {
                Interlocked.Exchange(ref _synchronizeAllRequested, 1);
                _queuedSynchronizationKeys.Clear();
            }
            else
            {
                _queuedSynchronizationKeys[storageKey] = 0;
            }

            _synchronizationWakeSignal.Writer.TryWrite(0);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static void ValidatePolicy(IResilientRepositoryPolicy<TContract> policy)
    {
        if (string.IsNullOrWhiteSpace(policy.DatasetId)
            || policy.DatasetId.Length > MaximumDatasetIdLength)
        {
            throw new ArgumentException(
                $"The resilient repository policy must provide a dataset identifier no longer than {MaximumDatasetIdLength} characters.",
                nameof(policy));
        }

        if (policy.BackgroundRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException("The background retry delay must be positive.", nameof(policy));
        }

        if (policy.MaximumPendingSynchronizationKeys <= 0)
        {
            throw new ArgumentException("The maximum pending synchronization-key count must be positive.", nameof(policy));
        }

        if (policy.MaximumSynchronizationScanItems <= 0)
        {
            throw new ArgumentException("The maximum synchronization scan size must be positive.", nameof(policy));
        }

        var broadReadMode = policy.GetRevisionCheckMode(storageKey: null);
        if (!Enum.IsDefined(broadReadMode))
        {
            throw new ArgumentException("The resilient repository policy returned an invalid revision-check mode.", nameof(policy));
        }
    }

    private readonly record struct FailureNotification(
        IResilientRepositoryPolicy<TContract> Policy,
        ResilientRepositoryFailure Failure);

}
