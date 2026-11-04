using Idb.Diagnostics;
using System.Collections.Concurrent;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Idb.Storage.AtlasCore.Internal;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore;

/// <summary>
/// Native AtlasCore physical store used by the InterfaceDB AtlasCore repository provider.
/// </summary>
public sealed partial class AtlasCoreStore : IAsyncDisposable
{
    // Keep this lexical guard ordinal. Windows can host case-sensitive directories,
    // while the FileShare.None lease supplies filesystem-identity contention.
    private static readonly ConcurrentDictionary<string, byte> ActiveRoots =
        new(StringComparer.Ordinal);

    private readonly string _rootDirectory;
    private readonly AtlasCoreOptions _options;
    private readonly HybridPlacementDirectory _placement;
    private readonly AtlasGroupSetDirectory _groupSets;
    private readonly AtlasTransactionLog _transactionLog;
    private readonly FileStream _writerLease;
    private readonly SegmentReadHandleCache _readHandleCache;
    private readonly Task<AtlasGroupStore>?[] _groups;
    private readonly object _groupSync = new();
    private readonly object _disposalSync = new();
    private readonly ConcurrentDictionary<string, ResolvedLocation> _resolvedGroups = new(StringComparer.Ordinal);
    private int _resolvedGroupAdmissions;
    private ResolvedSnapshot? _resolvedSnapshot;
    private long _resolvedSnapshotBuilds;
    private long _routeCacheEpoch;
    private readonly ReaderWriterLockSlim _indexVisibility = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _exclusiveMutationGate = new(1, 1);
    private readonly SemaphoreSlim _compactionGate = new(1, 1);
    private long _saves;
    private long _deletes;
    private long _pointReads;
    private long _bulkReadFrames;
    private long _bulkReadWindows;
    private long _bulkReadBytes;
    private long _committedTransactions;
    private long _rolledBackTransactions;
    private long _faultedTransactions;
    private int _activeOperations;
    private int _activeMutations;
    private int _activeReads;
    private int _exclusiveMutation;
    private int _readerRetirement;
    private int _compacting;
    private int _faulted;
    private int _disposed;
    private Task? _disposalTask;

    private AtlasCoreStore(
        string rootDirectory,
        AtlasCoreOptions options,
        HybridPlacementDirectory placement,
        AtlasGroupSetDirectory groupSets,
        AtlasTransactionLog transactionLog,
        FileStream writerLease)
    {
        _rootDirectory = rootDirectory;
        _options = options;
        _placement = placement;
        _groupSets = groupSets;
        _transactionLog = transactionLog;
        _writerLease = writerLease;
        _readHandleCache = new SegmentReadHandleCache(options.MaximumOpenReadHandles);
        _groups = new Task<AtlasGroupStore>?[options.Partitioning.MaximumPartitions];
        _partitionMutationCounts = new int[options.Partitioning.MaximumPartitions];
        _nextPartitionAttempt = new long[options.Partitioning.MaximumPartitions];
    }

    public AtlasCoreStatistics Statistics
    {
        get
        {
            var groups = GetOpenGroupsSnapshot();
            var readHandles = _readHandleCache.Statistics;
            return new AtlasCoreStatistics(
                _options.Partitioning.MaximumPartitions,
                groups.Length,
                _placement.Generation,
                _placement.SplitCount,
                _placement.PublicationFailures,
                Volatile.Read(ref _saves),
                Volatile.Read(ref _deletes),
                Volatile.Read(ref _pointReads))
            {
                LogicalPartitionCount = _placement.KnownGroupCount,
                MaximumConcurrentPartitionWrites = _options.WriteScheduler?.Maximum ?? _options.MaximumConcurrentPartitionWrites,
                PeakConcurrentPartitionWrites = _options.WriteScheduler?.Peak ?? 0,
                CommittedTransactions = Volatile.Read(ref _committedTransactions),
                RolledBackTransactions = Volatile.Read(ref _rolledBackTransactions),
                FaultedTransactions = Volatile.Read(ref _faultedTransactions),
                RetainedTransactionCommits = _transactionLog.RetainedCommitCount,
                CompactionInProgress = Volatile.Read(ref _compacting) != 0,
                GroupSetGeneration = _groupSets.Generation,
                TransactionProtocol = _options.TransactionProtocol,
                DurabilityStrategy = _options.DurabilityStrategy,
                TransactionLogBytes = _transactionLog.LogBytes,
                TransactionLogRecycledThrough = _transactionLog.RecycledThrough,
                OpenGroupEntries = groups.Sum(static group => (long)group.Count),
                OpenGroupSegmentFiles = groups.Sum(static group => group.SegmentFileCount),
                OpenGroupPhysicalBytes = groups.Sum(static group => group.PhysicalBytes),
                RecoveryScannedFrames = groups.Sum(static group => group.ScannedFrames),
                RepairedTailBytes = groups.Sum(static group => group.RepairedTailBytes),
                ReplayedWalMutations = groups.Sum(static group => group.ReplayedWalMutations),
                MinimumOpenGroupCheckpointTransactionWatermark = groups.Length == 0
                    ? 0
                    : groups.Min(static group => group.CheckpointTransactionWatermark),
                CurrentOpenReadHandles = readHandles.CurrentOpenHandles,
                PeakOpenReadHandles = readHandles.PeakOpenHandles,
                ReadHandleCacheHits = readHandles.Hits,
                ReadHandleCacheMisses = readHandles.Misses,
                ReadHandleCacheEvictions = readHandles.Evictions,
                BulkReadFrames = Volatile.Read(ref _bulkReadFrames),
                BulkReadWindows = Volatile.Read(ref _bulkReadWindows),
                BulkReadBytes = Volatile.Read(ref _bulkReadBytes)
            };
        }
    }

    public event EventHandler<AtlasCoreTransactionStateChangedEventArgs>? TransactionStateChanged;

    internal PlacementRoute GetPlacementRoute(string key) => _placement.Route(key);

    internal long ResolvedSnapshotBuilds => Volatile.Read(ref _resolvedSnapshotBuilds);

    internal int ResolvedRouteCacheEntries => Volatile.Read(ref _resolvedGroupAdmissions);

    public static ValueTask<AtlasCoreStore> OpenAsync(
        string rootDirectory,
        AtlasCoreOptions? options = null,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(options?.Telemetry) ? OpenAsyncUnobserved(rootDirectory, options, cancellationToken)
            : OpenAsyncObserved(rootDirectory, options, cancellationToken);

    private static ValueTask<AtlasCoreStore> OpenAsyncObserved(
        string rootDirectory,
        AtlasCoreOptions? options = null,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(options?.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Initialize,
                () => OpenAsyncUnobserved(rootDirectory, options, cancellationToken), log: true, bytes: 0);

    private static async ValueTask<AtlasCoreStore> OpenAsyncUnobserved(
        string rootDirectory,
        AtlasCoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new AtlasCoreOptions();
        options.Validate();
        options = options with { WriteScheduler = new PartitionWriteScheduler(options.MaximumConcurrentPartitionWrites) };
        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        Directory.CreateDirectory(fullRoot);
        if (!ActiveRoots.TryAdd(fullRoot, 0))
        {
            throw new IOException($"Another AtlasCore writer already owns '{fullRoot}'.");
        }

        FileStream? lease = null;
        AtlasTransactionLog? transactionLog = null;
        AtlasCoreStore? store = null;
        try
        {
            lease = AcquireWriterLease(fullRoot);
            var durableFileSystem = options.DurableFileSystem;
            await AtlasCoreFormatManifest.ReadOrCreateAsync(
                    fullRoot,
                    options,
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            transactionLog = await AtlasTransactionLog.OpenAsync(
                    fullRoot,
                    options,
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            var groupSets = await AtlasGroupSetDirectory.OpenAsync(
                    fullRoot,
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            var placement = await HybridPlacementDirectory.OpenAsync(
                fullRoot, groupSets.CurrentRoot, options, durableFileSystem, cancellationToken).ConfigureAwait(false);
            store = new AtlasCoreStore(fullRoot, options, placement, groupSets, transactionLog, lease);
            if (options.Partitioning.IsFixedLayout)
            {
                await store.PrepareForWritesAsync(cancellationToken).ConfigureAwait(false);
                await placement.CompleteFixedInitializationAsync(cancellationToken).ConfigureAwait(false);
            }
            return store;
        }
        catch (Exception exception)
        {
            if (store is not null)
            {
                try { await store.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupException) { RecordOpenCleanupFailure(exception, "fixed layout initialization", cleanupException); }
                throw;
            }
            if (transactionLog is not null)
            {
                try
                {
                    await transactionLog.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    RecordOpenCleanupFailure(exception, "transaction log", cleanupException);
                }
            }

            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    RecordOpenCleanupFailure(exception, "writer lease", cleanupException);
                }
            }

            ActiveRoots.TryRemove(fullRoot, out _);
            throw;
        }
    }

    public ValueTask SaveAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? SaveAsyncUnobserved(key, payload, cancellationToken)
            : SaveAsyncObserved(key, payload, cancellationToken);

    private ValueTask SaveAsyncObserved(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Save,
                () => SaveAsyncUnobserved(key, payload, cancellationToken), log: true, bytes: payload.Length);

    private async ValueTask SaveAsyncUnobserved(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var route = await _placement.GetWriteRouteAsync(key, cancellationToken).ConfigureAwait(false);
            var group = await GetGroupAsync(route.PrimaryGroup).ConfigureAwait(false);
            var address = await group.PutAsync(key, payload, route.Generation, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _routeCacheEpoch);
            Volatile.Write(ref _resolvedSnapshot, null);
            Interlocked.Increment(ref _saves);
            ObservePartition(group, route.PrimaryGroup);
        }
        finally
        {
            ExitMutation();
        }
    }

    public ValueTask SaveAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        IAtlasCoreTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? SaveAsync(key, payload, cancellationToken)
            : RequireOwnedTransaction(transaction).SaveAsync(key, payload, cancellationToken);

    internal ValueTask SaveOwnedAsync(
        string key,
        byte[] payload,
        IAtlasCoreTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? SaveAsync(key, payload, cancellationToken)
            : RequireOwnedTransaction(transaction).SaveOwnedAsync(key, payload, cancellationToken);

    public ValueTask<IAtlasCoreTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        var transaction = new AtlasCoreTransaction(
            this,
            _options.MaximumTransactionMutations,
            _options.MaximumTransactionLogRecordBytes);
        OnTransactionStateChanged(
            transaction.Id,
            AtlasCoreTransactionState.Created,
            AtlasCoreTransactionState.Active,
            failure: null);
        return ValueTask.FromResult<IAtlasCoreTransaction>(transaction);
    }

    public ValueTask<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? GetAsyncUnobserved(key, cancellationToken)
            : GetAsyncObserved(key, cancellationToken);

    private ValueTask<byte[]?> GetAsyncObserved(
        string key,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Read,
                () => GetAsyncUnobserved(key, cancellationToken), log: true, bytes: 0);

    private async ValueTask<byte[]?> GetAsyncUnobserved(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _pointReads);
            return await ReadCoreAsync(key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitReadOperation();
        }
    }

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false)).Length;
        }
        finally
        {
            ExitReadOperation();
        }
    }

    internal async ValueTask<IReadOnlyList<string>> GetKeysSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false);
            var keys = new string[resolved.Length];
            for (var index = 0; index < resolved.Length; index++)
            {
                keys[index] = resolved[index].Key;
            }

            return keys;
        }
        finally
        {
            ExitReadOperation();
        }
    }

    internal async ValueTask<bool> ContainsKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(key, cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            ExitReadOperation();
        }
    }

    internal async ValueTask<AtlasCoreSearchIndexStorageIdentity> GetSearchIndexStorageIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> metadata = stackalloc byte[48];
            BinaryPrimitives.WriteInt32LittleEndian(metadata, snapshot.Length);
            hash.AppendData(metadata[..sizeof(int)]);
            byte[]? keyBuffer = null;
            try
            {
                foreach (var record in snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var keyBytes = Encoding.UTF8.GetByteCount(record.Key);
                    if (keyBuffer is null || keyBuffer.Length < keyBytes)
                    {
                        if (keyBuffer is not null)
                        {
                            ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
                        }

                        keyBuffer = ArrayPool<byte>.Shared.Rent(keyBytes);
                    }

                    _ = Encoding.UTF8.GetBytes(record.Key, keyBuffer.AsSpan(0, keyBytes));
                    BinaryPrimitives.WriteInt32LittleEndian(metadata, keyBytes);
                    hash.AppendData(metadata[..sizeof(int)]);
                    hash.AppendData(keyBuffer.AsSpan(0, keyBytes));

                    BinaryPrimitives.WriteInt32LittleEndian(metadata, record.Group);
                    BinaryPrimitives.WriteInt64LittleEndian(metadata[4..], record.Address.SegmentId);
                    BinaryPrimitives.WriteInt64LittleEndian(metadata[12..], record.Address.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(metadata[20..], record.Address.FrameLength);
                    BinaryPrimitives.WriteInt64LittleEndian(metadata[24..], record.Address.Lsn);
                    BinaryPrimitives.WriteInt64LittleEndian(metadata[32..], record.Address.PlacementGeneration);
                    hash.AppendData(metadata[..40]);
                }
            }
            finally
            {
                if (keyBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
                }
            }

            return new AtlasCoreSearchIndexStorageIdentity(snapshot.Length, hash.GetHashAndReset());
        }
        finally
        {
            ExitReadOperation();
        }
    }

    public ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? ReadAllAsyncUnobserved(cancellationToken)
            : ReadAllAsyncObserved(cancellationToken);

    private ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadAllAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Query,
                () => ReadAllAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadAllAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitReadOperation();
        }
    }

    internal ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? ReadManyAsyncUnobserved(keys, cancellationToken)
            : ReadManyAsyncObserved(keys, cancellationToken);

    private ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadManyAsyncObserved(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Read,
                () => ReadManyAsyncUnobserved(keys, cancellationToken), log: true, bytes: 0);

    private async ValueTask<IReadOnlyList<AtlasCoreRecord>> ReadManyAsyncUnobserved(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var routes = new PlacementRoute[keys.Count];
            var groupTasks = new Dictionary<int, Task<AtlasGroupStore>>();
            for (var index = 0; index < keys.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                routes[index] = _placement.Route(keys[index]);
                foreach (var groupId in routes[index].ReadGroups)
                {
                    groupTasks.TryAdd(groupId, GetGroupAsync(groupId));
                }
            }

            await Task.WhenAll(groupTasks.Values).WaitAsync(cancellationToken).ConfigureAwait(false);
            var resolved = new List<ResolvedRecord>(keys.Count);
            _indexVisibility.EnterReadLock();
            try
            {
                for (var index = 0; index < keys.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var groupId in routes[index].ReadGroups)
                    {
                        if (!groupTasks[groupId].Result.TryGetAddress(keys[index], out var address))
                        {
                            continue;
                        }

                        if (address.Kind != AtlasGroupRecordKind.Delete)
                        {
                            resolved.Add(new ResolvedRecord(keys[index], groupId, address));
                        }

                        break;
                    }
                }
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            return await ReadResolvedAsync(resolved.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitReadOperation();
        }
    }

    public async IAsyncEnumerable<AtlasCoreRecord> StreamAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string[] keys;
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try { keys = (await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false)).Select(record => record.Key).ToArray(); }
        finally { ExitReadOperation(); }

        // Membership is captured at scan start; each batch resolves current locations under
        // its own reservation. No reservation survives a yield into application code.
        var batchSize = _options.StreamingBatchRecords;
        for (var offset = 0; offset < keys.Length; offset += batchSize)
        {
            var records = await ReadManyAsync(
                new ArraySegment<string>(keys, offset, Math.Min(batchSize, keys.Length - offset)),
                cancellationToken).ConfigureAwait(false);
            foreach (var record in records) yield return record;
        }
    }

    public ValueTask<AtlasCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? VerifyIntegrityAsyncUnobserved(cancellationToken)
            : VerifyIntegrityAsyncObserved(cancellationToken);

    private ValueTask<AtlasCoreIntegrityResult> VerifyIntegrityAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Verify,
                () => VerifyIntegrityAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<AtlasCoreIntegrityResult> VerifyIntegrityAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        var liveEntries = 0;
        long frames = 0;
        long payloadBytes = 0;
        try
        {
            await _placement.FlushMaintenanceAsync(cancellationToken).ConfigureAwait(false);
            var groups = await GetAllGroupsAsync(cancellationToken).ConfigureAwait(false);
            for (var group = 0; group < groups.Length; group++)
            {
                try
                {
                    var result = await Task.Run(groups[group].VerifyIntegrity, cancellationToken)
                        .ConfigureAwait(false);
                    frames += result.Frames;
                    payloadBytes += result.PayloadBytes;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"Group {group:N0}: {exception.Message}");
                }
            }

            if (failures.Count == 0)
            {
                try
                {
                    var resolved = await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false);
                    liveEntries = resolved.Length;
                    _ = await ReadResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures.Add($"Live locator verification: {exception.Message}");
                }
            }

            return new AtlasCoreIntegrityResult(
                failures.Count == 0,
                liveEntries,
                frames,
                payloadBytes,
                stopwatch.Elapsed,
                failures);
        }
        finally
        {
            ExitExclusiveMutation();
        }
    }

    public ValueTask<AtlasCoreBackupResult> BackupAsync(
        string backupDirectory,
        AtlasCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? BackupAsyncUnobserved(backupDirectory, options, cancellationToken)
            : BackupAsyncObserved(backupDirectory, options, cancellationToken);

    private ValueTask<AtlasCoreBackupResult> BackupAsyncObserved(
        string backupDirectory,
        AtlasCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Backup,
                () => BackupAsyncUnobserved(backupDirectory, options, cancellationToken), log: true, bytes: 0);

    private async ValueTask<AtlasCoreBackupResult> BackupAsyncUnobserved(
        string backupDirectory,
        AtlasCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        options ??= new AtlasCoreBackupOptions();
        options.Validate();
        var destination = Path.GetFullPath(backupDirectory);
        EnsureSeparateTrees(_rootDirectory, destination, "backup");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"AtlasCore backup destination '{destination}' already exists.");
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.atlas-backup-{Guid.NewGuid():N}");
        await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _ = await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(temporary);
            var admission = new StoreTreeCopyAdmission(
                options.MaximumFiles,
                options.MaximumTotalBytes,
                options.MaximumFileBytes,
                options.MaximumDirectoryDepth,
                options.MaximumDuration,
                destination,
                options.RequireFreeSpaceAdmission,
                options.MinimumFreeSpaceReserveBytes);
            await CopyStoreTreeAsync(
                _rootDirectory,
                temporary,
                _rootDirectory,
                temporary,
                depth: 0,
                admission,
                cancellationToken).ConfigureAwait(false);

            Directory.Move(temporary, destination);
            _options.DurableFileSystem.FlushDirectory(parent);
            int liveEntries;
            try
            {
                if (options.VerifyAfterWrite)
                {
                    await using var verifier = await OpenAsync(destination, _options, cancellationToken)
                        .ConfigureAwait(false);
                    var integrity = await verifier.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
                    if (!integrity.IsValid)
                    {
                        throw new InvalidDataException(
                            "AtlasCore backup verification failed: " + string.Join("; ", integrity.Failures));
                    }

                    liveEntries = integrity.LiveEntriesChecked;
                }
                else
                {
                    liveEntries = (await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false)).Length;
                }
            }
            catch
            {
                TryDeleteOwnedTree(destination);
                throw;
            }

            return new AtlasCoreBackupResult(
                destination,
                liveEntries,
                admission.Files,
                admission.Bytes,
                stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteGeneratedTree(temporary, ".atlas-backup-");
            ExitExclusiveMutation();
        }
    }

    public static ValueTask<AtlasCoreRestoreResult> RestoreAsync(
        string backupDirectory,
        string storeDirectory,
        AtlasCoreOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RestoreAsync(
            backupDirectory,
            storeDirectory,
            options,
            restoreOptions: null,
            cancellationToken);

    public static async ValueTask<AtlasCoreRestoreResult> RestoreAsync(
        string backupDirectory,
        string storeDirectory,
        AtlasCoreOptions? options,
        AtlasCoreRestoreOptions? restoreOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        options ??= new AtlasCoreOptions();
        options.Validate();
        restoreOptions ??= new AtlasCoreRestoreOptions();
        restoreOptions.Validate();
        var source = Path.GetFullPath(backupDirectory);
        var destination = Path.GetFullPath(storeDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"AtlasCore backup '{source}' does not exist.");
        }

        EnsureSeparateTrees(source, destination, "restore");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"AtlasCore restore destination '{destination}' already exists.");
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.atlas-restore-{Guid.NewGuid():N}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(temporary);
            var admission = new StoreTreeCopyAdmission(
                restoreOptions.MaximumFiles,
                restoreOptions.MaximumTotalBytes,
                restoreOptions.MaximumFileBytes,
                restoreOptions.MaximumDirectoryDepth,
                restoreOptions.MaximumDuration,
                destination,
                restoreOptions.RequireFreeSpaceAdmission,
                restoreOptions.MinimumFreeSpaceReserveBytes);
            await CopyStoreTreeAsync(
                source,
                temporary,
                source,
                temporary,
                depth: 0,
                admission,
                cancellationToken).ConfigureAwait(false);
            Directory.Move(temporary, destination);
            options.DurableFileSystem.FlushDirectory(parent);
            int liveEntries;
            try
            {
                await using var verifier = await OpenAsync(destination, options, cancellationToken)
                    .ConfigureAwait(false);
                var integrity = await verifier.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
                if (!integrity.IsValid)
                {
                    throw new InvalidDataException(
                        "AtlasCore restore verification failed: " + string.Join("; ", integrity.Failures));
                }

                liveEntries = integrity.LiveEntriesChecked;
            }
            catch
            {
                TryDeleteOwnedTree(destination);
                throw;
            }

            return new AtlasCoreRestoreResult(
                destination,
                liveEntries,
                admission.Files,
                admission.Bytes,
                stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteGeneratedTree(temporary, ".atlas-restore-");
        }
    }

    public ValueTask<AtlasCoreCompactionResult> CompactAsync(
        AtlasCoreCompactionOptions? options = null,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CompactAsyncUnobserved(options, cancellationToken)
            : CompactAsyncObserved(options, cancellationToken);

    private ValueTask<AtlasCoreCompactionResult> CompactAsyncObserved(
        AtlasCoreCompactionOptions? options = null,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Compact,
                () => CompactAsyncUnobserved(options, cancellationToken), log: true, bytes: 0);

    private async ValueTask<AtlasCoreCompactionResult> CompactAsyncUnobserved(
        AtlasCoreCompactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AtlasCoreCompactionOptions();
        options.Validate();
        await _compactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _compacting, 1);
            var stopwatch = Stopwatch.StartNew();
            var published = false;
            var candidateGeneration = 0L;
            var candidateTransactionFloor = 0L;
            string? candidateRoot = null;
            AtlasGroupStore[]? candidateGroups = null;
            try
            {
                await _placement.FlushMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                var previousRoot = _groupSets.CurrentRoot;
                var bytesBefore = MeasureDirectoryBytes(previousRoot);
                var live = await ResolveLiveRecordsAsync(cancellationToken).ConfigureAwait(false);
                long copiedBytes = 0;
                EnsureCompactionCapacity(
                    checked(live.Sum(static record => (long)record.Address.FrameLength) + (_retention?.Statistics.RetainedBytes ?? 0)),
                    options);

                var candidate = await _groupSets.CreateCandidateAsync(cancellationToken)
                    .ConfigureAwait(false);
                candidateGeneration = candidate.Generation;
                candidateRoot = candidate.RootDirectory;
                candidateGroups = await OpenCandidateGroupsAsync(candidateRoot, cancellationToken)
                    .ConfigureAwait(false);

                for (var offset = 0; offset < live.Length; offset += options.RecordsPerBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(options.RecordsPerBatch, live.Length - offset);
                    var records = await ReadResolvedAsync(
                            live.AsMemory(offset, count),
                            cancellationToken)
                        .ConfigureAwait(false);
                    copiedBytes += records.Sum(record => (long)AtlasGroupLogFormat.CalculateFrameLength(
                        AtlasGroupRecordKind.Put, 1, 1, record.Key, record.Payload.Length));
                    for (var first = 0; first < records.Length; first += options.MaximumCopyConcurrency)
                    {
                        var writeCount = Math.Min(
                            options.MaximumCopyConcurrency,
                            records.Length - first);
                        var writes = new Task<AtlasGroupAddress>[writeCount];
                        for (var index = 0; index < writeCount; index++)
                        {
                            var record = records[first + index];
                            var route = _placement.RouteForWrite(record.Key);
                            writes[index] = candidateGroups[route.PrimaryGroup]
                                .PutAsync(
                                    record.Key,
                                    record.Payload,
                                    route.Generation,
                                    CancellationToken.None)
                                .AsTask();
                        }

                        _ = await Task.WhenAll(writes).ConfigureAwait(false);
                    }
                }

                var transactionWatermark = _transactionLog.LastCommittedSequence;
                var candidateCheckpoints = await Task.WhenAll(candidateGroups.Select(
                        group => group.CheckpointAsync(
                            transactionWatermark,
                            CancellationToken.None).AsTask()))
                    .ConfigureAwait(false);
                candidateTransactionFloor = candidateCheckpoints.Select(
                    static checkpoint => checkpoint.RetainedTransactionFloor).DefaultIfEmpty(0).Min();
                foreach (var group in candidateGroups)
                {
                    _ = group.VerifyIntegrity();
                }

                await DisposeGroupsAsync(candidateGroups).ConfigureAwait(false);
                candidateGroups = null;
                if (_retention is not null) await _retention.RelocateAsync(_ => true, cancellationToken).ConfigureAwait(false);
                await _groupSets.PublishAsync(
                        candidateGeneration,
                        candidateRoot,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                published = true;
                if (candidateTransactionFloor > 0)
                {
                    await _transactionLog.RecycleAsync(
                            candidateTransactionFloor,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                Volatile.Write(ref _readerRetirement, 1);
                while (Volatile.Read(ref _activeReads) != 0)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }

                await DisposeCurrentGroupsAndResetAsync().ConfigureAwait(false);
                _resolvedGroups.Clear();
                Volatile.Write(ref _resolvedGroupAdmissions, 0);
                Volatile.Write(ref _resolvedSnapshot, null);
                Volatile.Write(ref _readerRetirement, 0);
                _groupSets.CleanupSupersededSets();
                var bytesAfter = MeasureDirectoryBytes(_rootDirectory);
                return new AtlasCoreCompactionResult(
                    live.Length,
                    live.LongLength,
                    bytesBefore,
                    bytesAfter,
                    Math.Max(0, bytesBefore - bytesAfter),
                    stopwatch.Elapsed) { BytesCopied = copiedBytes };
            }
            catch
            {
                if (published)
                {
                    Volatile.Write(ref _faulted, 1);
                }

                throw;
            }
            finally
            {
                if (candidateGroups is not null)
                {
                    try
                    {
                        await DisposeGroupsAsync(candidateGroups).ConfigureAwait(false);
                    }
                    catch
                    {
                        Volatile.Write(ref _faulted, 1);
                    }
                }

                if (!published && candidateRoot is not null)
                {
                    _groupSets.DiscardCandidate(candidateGeneration, candidateRoot);
                }

                Volatile.Write(ref _readerRetirement, 0);
                Volatile.Write(ref _compacting, 0);
                ExitExclusiveMutation();
            }
        }
        finally
        {
            _compactionGate.Release();
        }
    }

    public ValueTask<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? DeleteAsyncUnobserved(key, cancellationToken)
            : DeleteAsyncObserved(key, cancellationToken);

    private ValueTask<bool> DeleteAsyncObserved(
        string key,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Delete,
                () => DeleteAsyncUnobserved(key, cancellationToken), log: true, bytes: 0);

    private async ValueTask<bool> DeleteAsyncUnobserved(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadCoreAsync(key, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }

            var route = _placement.RouteForWrite(key);
            var group = await GetGroupAsync(route.PrimaryGroup).ConfigureAwait(false);
            var address = await group.DeleteAsync(
                    key,
                    route.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _routeCacheEpoch);
            Volatile.Write(ref _resolvedSnapshot, null);
            _retention?.Deleted(key);
            Interlocked.Increment(ref _deletes);
            _placement.Observe(key, route);
            return true;
        }
        finally
        {
            ExitMutation();
        }
    }

    public ValueTask<bool> DeleteAsync(
        string key,
        IAtlasCoreTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? DeleteAsync(key, cancellationToken)
            : RequireOwnedTransaction(transaction).DeleteAsync(key, cancellationToken);

    public ValueTask<byte[]?> GetAsync(
        string key,
        IAtlasCoreTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? GetAsync(key, cancellationToken)
            : RequireOwnedTransaction(transaction).GetAsync(key, cancellationToken);

    internal ValueTask CommitTransactionAsync(
        Guid transactionId,
        IReadOnlyList<AtlasCorePendingMutation> mutations,
        CancellationToken cancellationToken)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CommitTransactionAsyncUnobserved(transactionId, mutations, cancellationToken)
            : CommitTransactionAsyncObserved(transactionId, mutations, cancellationToken);

    private ValueTask CommitTransactionAsyncObserved(
        Guid transactionId,
        IReadOnlyList<AtlasCorePendingMutation> mutations,
        CancellationToken cancellationToken)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Transaction,
                () => CommitTransactionAsyncUnobserved(transactionId, mutations, cancellationToken), log: true, bytes: 0);

    private async ValueTask CommitTransactionAsyncUnobserved(
        Guid transactionId,
        IReadOnlyList<AtlasCorePendingMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            return;
        }

        if (mutations.Count > _options.MaximumTransactionMutations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mutations),
                $"An AtlasCore transaction may contain at most {_options.MaximumTransactionMutations:N0} distinct mutations.");
        }

        AtlasTransactionLog.ValidateTransactionAdmission(
            mutations,
            _options.MaximumTransactionLogRecordBytes);

        await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
        var preparationsStarted = false;
        try
        {
            var prepared = new PreparedMutation[mutations.Count];
            var writes = new Task<AtlasGroupAddress>[mutations.Count];
            var requireParticipantDurability =
                _options.TransactionProtocol == AtlasCoreTransactionProtocol.ParticipantCommitJournal;
            for (var index = 0; index < mutations.Count; index++)
            {
                var mutation = mutations[index];
                var route = await _placement.GetWriteRouteAsync(mutation.Key, CancellationToken.None).ConfigureAwait(false);
                var group = await GetGroupAsync(route.PrimaryGroup).ConfigureAwait(false);
                prepared[index] = new PreparedMutation(mutation, route, group, default);
            }

            await _placement.FlushMaintenanceAsync(CancellationToken.None).ConfigureAwait(false);
            preparationsStarted = true;
            for (var index = 0; index < prepared.Length; index++)
            {
                var item = prepared[index];
                var mutation = item.Mutation;
                var route = item.Route;
                var group = item.Group;
                writes[index] = mutation.Kind == AtlasGroupRecordKind.Put
                    ? group.PreparePutAsync(
                            mutation.Key,
                            mutation.Payload,
                            route.Generation,
                            transactionId,
                            requireParticipantDurability,
                            CancellationToken.None)
                        .AsTask()
                    : group.PrepareDeleteAsync(
                            mutation.Key,
                            route.Generation,
                            transactionId,
                            requireParticipantDurability,
                            CancellationToken.None)
                        .AsTask();
            }

            var addresses = await Task.WhenAll(writes).ConfigureAwait(false);
            if (requireParticipantDurability)
            {
                _ = await _transactionLog.CommitAsync(
                        transactionId,
                        mutations.Count,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                var walMutations = new AtlasWalMutation[prepared.Length];
                for (var index = 0; index < prepared.Length; index++)
                {
                    var item = prepared[index];
                    walMutations[index] = new AtlasWalMutation(
                        item.Route.PrimaryGroup,
                        item.Mutation.Kind,
                        item.Mutation.Key,
                        item.Mutation.Payload,
                        addresses[index]);
                }

                _ = await _transactionLog.CommitMutationsAsync(
                        transactionId,
                        walMutations,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _indexVisibility.EnterWriteLock();
            try
            {
                for (var index = 0; index < prepared.Length; index++)
                {
                    var item = prepared[index] with { Address = addresses[index] };
                    prepared[index] = item;
                    item.Group.PublishPrepared(item.Mutation.Key, item.Address);
                    if (item.Mutation.Kind == AtlasGroupRecordKind.Delete) _retention?.Deleted(item.Mutation.Key);
                }

                Interlocked.Increment(ref _routeCacheEpoch);
                Volatile.Write(ref _resolvedSnapshot, null);
            }
            finally
            {
                _indexVisibility.ExitWriteLock();
            }

            foreach (var item in prepared)
            {
                if (item.Mutation.Kind == AtlasGroupRecordKind.Put)
                {
                    Interlocked.Increment(ref _saves);
                }
                else
                {
                    Interlocked.Increment(ref _deletes);
                }

                ObservePartition(item.Group, item.Route.PrimaryGroup);
            }
        }
        catch (Exception error)
        {
            // Routing validation is serialized against placement changes and precedes payload writes.
            // I/O and all failures after preparations begin retain fail-closed behaviour.
            if (preparationsStarted || error is not (ArgumentException or AtlasRouteRejectedException))
                Volatile.Write(ref _faulted, 1);
            throw;
        }
        finally
        {
            ExitExclusiveMutation();
        }
    }

    internal void OnTransactionStateChanged(
        Guid transactionId,
        AtlasCoreTransactionState previousState,
        AtlasCoreTransactionState state,
        Exception? failure)
    {
        switch (state)
        {
            case AtlasCoreTransactionState.Committed:
                Interlocked.Increment(ref _committedTransactions);
                break;
            case AtlasCoreTransactionState.RolledBack:
                Interlocked.Increment(ref _rolledBackTransactions);
                break;
            case AtlasCoreTransactionState.Faulted:
                Interlocked.Increment(ref _faultedTransactions);
                break;
        }

        var handlers = TransactionStateChanged;
        if (handlers is null)
        {
            return;
        }

        var arguments = new AtlasCoreTransactionStateChangedEventArgs(
            transactionId,
            previousState,
            state,
            failure);
        foreach (EventHandler<AtlasCoreTransactionStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, arguments);
            }
            catch
            {
                // Transaction observers cannot alter the durable outcome.
            }
        }
    }

    public ValueTask<AtlasCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CheckpointAsyncUnobserved(cancellationToken)
            : CheckpointAsyncObserved(cancellationToken);

    private ValueTask<AtlasCoreCheckpointResult> CheckpointAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.Atlas, DatabaseOperation.Checkpoint,
                () => CheckpointAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<AtlasCoreCheckpointResult> CheckpointAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckpointCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitExclusiveMutation();
        }
    }

    private async ValueTask<AtlasCoreCheckpointResult> CheckpointCoreAsync(
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await _placement.FlushMaintenanceAsync(cancellationToken).ConfigureAwait(false);

        var stores = await GetAllGroupsAsync(cancellationToken).ConfigureAwait(false);
        var transactionWatermark = _transactionLog.LastCommittedSequence;
        var checkpoints = await Task.WhenAll(
                stores.Select(store => store.CheckpointAsync(
                    transactionWatermark,
                    cancellationToken).AsTask()))
            .ConfigureAwait(false);
        var safeTransactionFloor = checkpoints.Select(
            static checkpoint => checkpoint.RetainedTransactionFloor).DefaultIfEmpty(0).Min();
        if (safeTransactionFloor > 0)
        {
            try
            {
                await _transactionLog.RecycleAsync(
                        safeTransactionFloor,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Replacement may have reached durable storage even when the platform reports
                // failure. Reopen is the only safe way to discover the authoritative journal.
                Volatile.Write(ref _faulted, 1);
                throw;
            }
        }

        return new AtlasCoreCheckpointResult(
            checkpoints.Length,
            checkpoints.Sum(static checkpoint => (long)checkpoint.Entries),
            checkpoints.Sum(static checkpoint => checkpoint.Bytes),
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Opens the bounded segment-group set in parallel. Call this before a latency-sensitive
    /// write burst; dynamic-layout opens remain metadata-only so read-mostly applications pay
    /// only for the groups their keys actually touch. Fixed layouts already activate all owners during Open.
    /// </summary>
    public async ValueTask PrepareForWritesAsync(CancellationToken cancellationToken = default)
        => await PrepareAllGroupsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads the bounded group indexes in parallel so random reads do not pay first-touch
    /// activation. Metadata-only open remains available when minimum startup work matters more.
    /// </summary>
    public async ValueTask PrepareForReadsAsync(CancellationToken cancellationToken = default)
        => await PrepareAllGroupsAsync(cancellationToken).ConfigureAwait(false);

    private async ValueTask PrepareAllGroupsAsync(CancellationToken cancellationToken)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var groupCount = _placement.KnownGroupCount;
            var parallelism = Math.Max(1, Math.Min(_options.MaximumParallelReads, groupCount));
            for (var first = 0; first < groupCount; first += parallelism)
            {
                var count = Math.Min(parallelism, groupCount - first);
                var tasks = new Task<AtlasGroupStore>[count];
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tasks[index] = GetGroupAsync(first + index);
                }

                await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ExitReadOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_disposalSync)
        {
            _disposalTask ??= DisposeCoreAsync();
            disposal = _disposalTask;
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        Task partitionWorker;
        lock (_partitionWorkerSync) { _pendingPartitions.Clear(); partitionWorker = _partitionWorker; }
        await partitionWorker.ConfigureAwait(false);
        _retention?.Dispose();
        while (Volatile.Read(ref _activeOperations) != 0)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }

        List<Exception>? failures = null;
        try
        {
            await _placement.FlushMaintenanceAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        Task<AtlasGroupStore>[] groups;
        lock (_groupSync)
        {
            groups = _groups.Where(static task => task is not null).Cast<Task<AtlasGroupStore>>().ToArray();
        }

        foreach (var groupTask in groups)
        {
            if (groupTask.IsFaulted)
            {
                _ = groupTask.Exception;
                continue;
            }

            if (groupTask.IsCanceled)
            {
                continue;
            }

            try
            {
                var group = await groupTask.ConfigureAwait(false);
                await group.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            await _readHandleCache.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await _transactionLog.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await _writerLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            ActiveRoots.TryRemove(_rootDirectory, out _);
        }

        try
        {
            _exclusiveMutationGate.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _compactionGate.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _indexVisibility.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more AtlasCore segment groups failed to close.", failures);
        }
    }

    private async ValueTask<byte[]?> ReadCoreAsync(string key, CancellationToken cancellationToken)
    {
        ResolvedLocation resolved;
        long epoch;
        _indexVisibility.EnterReadLock();
        try
        {
            _resolvedGroups.TryGetValue(key, out resolved);
            epoch = Volatile.Read(ref _routeCacheEpoch);
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }

        if (resolved != default && resolved.Epoch == epoch)
        {
            var cached = await (await GetGroupAsync(resolved.Group).ConfigureAwait(false))
                .ReadAtAsync(key, resolved.Address, cancellationToken)
                .ConfigureAwait(false);
            if (cached.Found)
            {
                return cached.Deleted ? null : cached.Payload;
            }

            if (_resolvedGroups.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _resolvedGroupAdmissions);
            }
        }

        var route = _placement.Route(key);
        foreach (var groupId in route.ReadGroups)
        {
            var group = await GetGroupAsync(groupId).ConfigureAwait(false);
            AtlasGroupAddress address;
            _indexVisibility.EnterReadLock();
            try
            {
                if (!group.TryGetAddress(key, out address))
                {
                    continue;
                }
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            if (address.Kind == AtlasGroupRecordKind.Delete)
            {
                CacheResolvedGroup(key, groupId, address, epoch);
                return null;
            }

            var stored = await group.ReadAtAsync(key, address, cancellationToken).ConfigureAwait(false);
            if (!stored.Found)
            {
                continue;
            }

            CacheResolvedGroup(key, groupId, address, epoch);
            return stored.Deleted ? null : stored.Payload;
        }

        return null;
    }

    private async ValueTask<ResolvedRecord[]> ResolveLiveRecordsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _indexVisibility.EnterReadLock();
        try
        {
            var cached = Volatile.Read(ref _resolvedSnapshot);
            if (cached is not null && IsSnapshotCurrent(cached))
            {
                return cached.Records;
            }
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }

        AtlasGroupStore[] stores;
        HybridPlacementDirectory.Snapshot placementSnapshot;
        do
        {
            stores = await GetAllGroupsAsync(cancellationToken).ConfigureAwait(false);
            placementSnapshot = _placement.Capture();
        } while (stores.Length < placementSnapshot.Entries.Length);
        _indexVisibility.EnterReadLock();
        try
        {
            var cached = Volatile.Read(ref _resolvedSnapshot);
            if (cached is not null && IsSnapshotCurrent(cached))
            {
                return cached.Records;
            }

            var placementGeneration = placementSnapshot.Generation;
            var versions = new long[stores.Length];
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < stores.Length; index++)
            {
                foreach (var pair in stores[index].SnapshotIndex(out versions[index]))
                {
                    keys.Add(pair.Key);
                }
            }

            var orderedKeys = keys.ToArray();
            Array.Sort(orderedKeys, StringComparer.Ordinal);
            var resolved = new List<ResolvedRecord>(orderedKeys.Length);
            foreach (var key in orderedKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var route = _placement.Route(key, placementSnapshot);
                foreach (var groupId in route.ReadGroups)
                {
                    if (!stores[groupId].TryGetAddress(key, out var address))
                    {
                        continue;
                    }

                    if (address.Kind != AtlasGroupRecordKind.Delete)
                    {
                        resolved.Add(new ResolvedRecord(key, groupId, address));
                    }

                    break;
                }
            }

            var snapshot = new ResolvedSnapshot(placementGeneration, stores, versions, resolved.ToArray());
            Interlocked.Increment(ref _resolvedSnapshotBuilds);
            if (IsSnapshotCurrent(snapshot))
            {
                Volatile.Write(ref _resolvedSnapshot, snapshot);
            }

            return snapshot.Records;
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }
    }

    private bool IsSnapshotCurrent(ResolvedSnapshot snapshot)
    {
        if (snapshot.PlacementGeneration != _placement.Generation)
        {
            return false;
        }

        for (var index = 0; index < snapshot.Groups.Length; index++)
        {
            if (snapshot.Groups[index].IndexVersion != snapshot.Versions[index])
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<AtlasCoreRecord[]> ReadResolvedAsync(
        ReadOnlyMemory<ResolvedRecord> resolved,
        CancellationToken cancellationToken)
    {
        if (resolved.IsEmpty)
        {
            return [];
        }

        var records = new AtlasCoreRecord[resolved.Length];
        var physicalReads = new AtlasPhysicalRead[resolved.Length];
        for (var index = 0; index < resolved.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = resolved.Span[index];
            physicalReads[index] = new AtlasPhysicalRead(
                index,
                item.Group,
                item.Key,
                item.Address);
        }

        Array.Sort(
            physicalReads,
            static (left, right) =>
            {
                var group = left.Group.CompareTo(right.Group);
                if (group != 0)
                {
                    return group;
                }

                var segment = left.Address.SegmentId.CompareTo(right.Address.SegmentId);
                return segment != 0
                    ? segment
                    : left.Address.Offset.CompareTo(right.Address.Offset);
            });

        var batches = new List<PhysicalReadBatch>();
        for (var start = 0; start < physicalReads.Length;)
        {
            var group = physicalReads[start].Group;
            var segment = physicalReads[start].Address.SegmentId;
            var end = start + 1;
            while (end < physicalReads.Length
                && physicalReads[end].Group == group
                && physicalReads[end].Address.SegmentId == segment)
            {
                end++;
            }

            batches.Add(new PhysicalReadBatch(group, start, end - start));
            start = end;
        }

        await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.MaximumParallelReads
                },
                async (batch, token) =>
                {
                    var group = await GetGroupAsync(batch.Group).WaitAsync(token).ConfigureAwait(false);
                    var result = await group.ReadManyFromSegmentAsync(
                            physicalReads.AsMemory(batch.Start, batch.Count),
                            records,
                            token)
                        .ConfigureAwait(false);
                    Interlocked.Add(ref _bulkReadFrames, result.Frames);
                    Interlocked.Add(ref _bulkReadWindows, result.Windows);
                    Interlocked.Add(ref _bulkReadBytes, result.Bytes);
                })
            .ConfigureAwait(false);
        return records;
    }

    private async ValueTask<AtlasGroupStore[]> GetAllGroupsAsync(
        CancellationToken cancellationToken)
    {
        var tasks = new Task<AtlasGroupStore>[_placement.KnownGroupCount];
        for (var group = 0; group < tasks.Length; group++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tasks[group] = GetGroupAsync(group);
        }

        return await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<AtlasGroupStore> GetGroupAsync(int groupId)
    {
        var existing = Volatile.Read(ref _groups[groupId]);
        if (existing is not null)
        {
            return existing;
        }

        lock (_groupSync)
        {
            return _groups[groupId] ??= OpenGroupAsync(groupId);
        }
    }

    private Task<AtlasGroupStore> OpenGroupAsync(int groupId)
    {
        var groupRoot = Path.Combine(_groupSets.CurrentRoot, _placement.GroupDirectoryName(groupId));
        return Task.Run(async () =>
            await AtlasGroupStore.OpenAsync(
                    groupRoot,
                    _options,
                     _transactionLog,
                     _options.DurableFileSystem,
                     groupId: groupId,
                     readHandleCache: _readHandleCache)
                .ConfigureAwait(false));
    }

    private void CacheResolvedGroup(string key, int group, AtlasGroupAddress address, long epoch)
    {
        if (_options.MaximumResolvedRouteCacheEntries == 0)
        {
            return;
        }

        var location = new ResolvedLocation(group, address, epoch);
        if (_resolvedGroups.TryGetValue(key, out var previous))
        {
            _ = _resolvedGroups.TryUpdate(key, location, previous);
            return;
        }

        // Reserve capacity before admission, without the dictionary-wide locks used
        // by ConcurrentDictionary.Count. Racing admissions return unused reservations.
        var admitted = Interlocked.Increment(ref _resolvedGroupAdmissions);
        if (admitted > _options.MaximumResolvedRouteCacheEntries || !_resolvedGroups.TryAdd(key, location))
        {
            Interlocked.Decrement(ref _resolvedGroupAdmissions);
        }
    }

    private readonly record struct ResolvedLocation(int Group, AtlasGroupAddress Address, long Epoch);

    private sealed record ResolvedSnapshot(
        long PlacementGeneration,
        AtlasGroupStore[] Groups,
        long[] Versions,
        ResolvedRecord[] Records);

    private readonly record struct ResolvedRecord(
        string Key,
        int Group,
        AtlasGroupAddress Address);

    private readonly record struct PhysicalReadBatch(
        int Group,
        int Start,
        int Count);

    private readonly record struct PreparedMutation(
        AtlasCorePendingMutation Mutation,
        PlacementRoute Route,
        AtlasGroupStore Group,
        AtlasGroupAddress Address);

    private AtlasGroupStore[] GetOpenGroupsSnapshot()
    {
        lock (_groupSync)
        {
            return _groups
                .Where(static task => task is { IsCompletedSuccessfully: true })
                .Select(static task => task!.Result)
                .ToArray();
        }
    }

    private void EnterOperation()
    {
        ThrowIfUnavailable();
        Interlocked.Increment(ref _activeOperations);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0)
        {
            Interlocked.Decrement(ref _activeOperations);
            ThrowIfUnavailable();
        }
    }

    private void ExitOperation() => Interlocked.Decrement(ref _activeOperations);

    private async ValueTask EnterMutationAsync(CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            while (true)
            {
                while (Volatile.Read(ref _exclusiveMutation) != 0)
                {
                    ThrowIfUnavailable();
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _activeMutations);
                if (Volatile.Read(ref _exclusiveMutation) == 0
                    && Volatile.Read(ref _disposed) == 0
                    && Volatile.Read(ref _faulted) == 0)
                {
                    return;
                }

                Interlocked.Decrement(ref _activeMutations);
            }
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private void ExitMutation()
    {
        Interlocked.Decrement(ref _activeMutations);
        ExitOperation();
    }

    private async ValueTask EnterReadOperationAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (Volatile.Read(ref _readerRetirement) != 0)
            {
                ThrowIfUnavailable();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            EnterOperation();
            Interlocked.Increment(ref _activeReads);
            if (Volatile.Read(ref _readerRetirement) == 0
                && Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _faulted) == 0)
            {
                return;
            }

            Interlocked.Decrement(ref _activeReads);
            ExitOperation();
        }
    }

    private void ExitReadOperation()
    {
        Interlocked.Decrement(ref _activeReads);
        ExitOperation();
    }

    private async ValueTask EnterExclusiveMutationAsync(CancellationToken cancellationToken)
    {
        EnterOperation();
        try
        {
            await _exclusiveMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                Volatile.Write(ref _exclusiveMutation, 1);
                while (Volatile.Read(ref _activeMutations) != 0)
                {
                    ThrowIfUnavailable();
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                Volatile.Write(ref _exclusiveMutation, 0);
                _exclusiveMutationGate.Release();
                throw;
            }
        }
        catch
        {
            ExitOperation();
            throw;
        }
    }

    private void ExitExclusiveMutation()
    {
        Volatile.Write(ref _exclusiveMutation, 0);
        _exclusiveMutationGate.Release();
        ExitOperation();
    }

    private AtlasCoreTransaction RequireOwnedTransaction(IAtlasCoreTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not AtlasCoreTransaction concrete || !ReferenceEquals(concrete.Owner, this))
        {
            throw new InvalidOperationException("The transaction belongs to another AtlasCore store.");
        }

        return concrete;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException(
                "AtlasCore has an uncertain transaction outcome and must be reopened before further use.");
        }
    }

    private static FileStream AcquireWriterLease(string rootDirectory)
    {
        try
        {
            return new FileStream(
                Path.Combine(rootDirectory, "writer.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            // FileShare.None is deliberate. On Unix, .NET maps it to an open-description
            // flock, so symlink/bind-mount aliases still contend and closing an unrelated
            // descriptor cannot release this lease. Deployments must not disable .NET file locking.
        }
        catch (IOException exception)
        {
            throw new IOException($"Another AtlasCore writer already owns '{rootDirectory}'.", exception);
        }
    }

    private static void RecordOpenCleanupFailure(
        Exception openException,
        string resourceName,
        Exception cleanupException)
    {
        try
        {
            openException.Data[$"AtlasCore open cleanup failure: {resourceName}"] = cleanupException;
        }
        catch
        {
            // Cleanup diagnostics must never replace the original open failure.
        }
    }

    private static async Task CopyStoreTreeAsync(
        string source,
        string destination,
        string sourceRoot,
        string destinationRoot,
        int depth,
        StoreTreeCopyAdmission admission,
        CancellationToken cancellationToken)
    {
        admission.CheckDirectory(source, depth);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, directory));
            if (string.Equals(relative, ".idb-history", StringComparison.Ordinal)) continue;
            EnsureNotLink(directory);
            if (!IsAllowedStoreDirectory(relative))
            {
                throw new InvalidDataException(
                    $"AtlasCore transfer contains unrecognized directory '{relative}'.");
            }
            var targetDirectory = ResolveTransferPath(destinationRoot, relative);
            Directory.CreateDirectory(targetDirectory);
            await CopyStoreTreeAsync(
                directory,
                targetDirectory,
                sourceRoot,
                destinationRoot,
                depth + 1,
                admission,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (string.Equals(name, "writer.lock", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(source, sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && (name == ".idb-revision-clock.lock" || name.StartsWith(".idb-revision-clock.creating-", StringComparison.Ordinal)))
                || name.Contains(".tmp-", StringComparison.Ordinal)
                || name.Contains(".recycle-", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, file));
            EnsureNotLink(file);
            if (!IsAllowedStoreFile(relative))
            {
                throw new InvalidDataException(
                    $"AtlasCore transfer contains unrecognized file '{relative}'.");
            }
            var length = new FileInfo(file).Length;
            admission.AdmitFile(relative, length);
            var target = ResolveTransferPath(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            EnsureNotLink(file);
            if (input.Length != length)
            {
                throw new IOException($"AtlasCore transfer source '{relative}' changed before it was copied.");
            }
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            while (copied < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                admission.CheckDuration();
                var requested = (int)Math.Min(buffer.Length, length - copied);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException($"AtlasCore transfer source '{relative}' ended unexpectedly.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }
            if (input.ReadByte() != -1)
            {
                throw new IOException($"AtlasCore transfer source '{relative}' grew while it was copied.");
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }

    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"AtlasCore transfer refuses filesystem link or reparse entry '{path}'.");
        }
    }

    private static string ResolveTransferPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(
            fullRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var candidate = Path.GetRelativePath(fullRoot, path);
        if (Path.IsPathRooted(candidate)
            || candidate == ".."
            || candidate.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"AtlasCore transfer path '{relative}' escapes its root.");
        }
        return path;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsAllowedStoreDirectory(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length == 1)
            return parts[0] is "transactions" or "group-sets" or "hybrid-placement" or "search-indexes";
        if (parts.Length == 2 && parts[0] == "group-sets" && parts[1] == "sets") return true;
        if (parts.Length >= 3 && parts[0] == "group-sets" && parts[1] == "sets"
            && IsNumberedName(parts[2], "set-", digits: 20, suffix: ""))
        {
            if (parts.Length == 3) return true;
            if (!IsNumberedName(parts[3], "bucket-", digits: 8, suffix: "")) return false;
            return parts.Length == 4
                || (parts.Length == 5 && parts[4] is "segments" or "checkpoints");
        }
        return false;
    }

    private static bool IsAllowedStoreFile(string relative)
    {
        if (relative is "storage-format.idbatlas" or ".idb-revision-clock") return true;
        var parts = relative.Split('/');
        if (parts.Length == 2 && parts[0] == "transactions" && parts[1] == "commits.idbatx") return true;
        if (parts.Length == 2 && parts[0] == "hybrid-placement")
            return IsNumberedName(parts[1], "map-", 20, ".idbmap");
        if (parts.Length == 2 && parts[0] == "group-sets")
            return IsNumberedName(parts[1], "set-", 20, ".a")
                || IsNumberedName(parts[1], "set-", 20, ".b")
                || IsNumberedName(parts[1], "set-", 20, ".commit");
        if (parts.Length == 2 && parts[0] == "search-indexes")
            return parts[1].StartsWith("catalog-", StringComparison.Ordinal)
                && parts[1].EndsWith(".idbsidx", StringComparison.Ordinal);
        if (parts.Length == 6 && parts[0] == "group-sets" && parts[1] == "sets"
            && IsNumberedName(parts[2], "set-", 20, "")
            && IsNumberedName(parts[3], "bucket-", 8, ""))
        {
            return parts[4] switch
            {
                "segments" => IsNumberedName(parts[5], "segment-", 20, ".idbag"),
                "checkpoints" => IsNumberedName(parts[5], "checkpoint-", 20, ".idbagi"),
                _ => false
            };
        }
        return false;
    }

    private static bool IsNumberedName(string value, string prefix, int digits, string suffix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith(suffix, StringComparison.Ordinal)
            || value.Length != prefix.Length + digits + suffix.Length)
            return false;
        var numeric = value.AsSpan(prefix.Length, digits);
        return numeric.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private sealed class StoreTreeCopyAdmission(
        int maximumFiles,
        long maximumTotalBytes,
        long maximumFileBytes,
        int maximumDirectoryDepth,
        TimeSpan maximumDuration,
        string destination,
        bool requireFreeSpaceAdmission,
        long minimumFreeSpaceReserveBytes)
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly long _availableDestinationBytes = requireFreeSpaceAdmission
            ? new DriveInfo(Path.GetPathRoot(destination)
                ?? throw new IOException($"Transfer destination '{destination}' has no filesystem root.")).AvailableFreeSpace
            : long.MaxValue;
        public int Files { get; private set; }
        public long Bytes { get; private set; }

        public void CheckDirectory(string path, int depth)
        {
            CheckDuration();
            if (depth > maximumDirectoryDepth)
                throw new InvalidDataException($"AtlasCore transfer exceeds the {maximumDirectoryDepth}-level directory limit at '{path}'.");
            EnsureNotLink(path);
        }

        public void AdmitFile(string relative, long length)
        {
            CheckDuration();
            if (length < 0 || length > maximumFileBytes)
                throw new InvalidDataException($"AtlasCore transfer file '{relative}' exceeds the configured file-byte limit.");
            var nextFiles = checked(Files + 1);
            var nextBytes = checked(Bytes + length);
            if (nextFiles > maximumFiles || nextBytes > maximumTotalBytes)
                throw new InvalidDataException("AtlasCore transfer exceeds its configured file-count or total-byte limit.");
            if (requireFreeSpaceAdmission
                && (_availableDestinationBytes < nextBytes
                    || _availableDestinationBytes - nextBytes < minimumFreeSpaceReserveBytes))
                throw new IOException(
                    $"AtlasCore transfer requires {nextBytes:N0} bytes plus a {minimumFreeSpaceReserveBytes:N0}-byte free-space reserve, but only {_availableDestinationBytes:N0} bytes are available.");
            Files = nextFiles;
            Bytes = nextBytes;
        }

        public void CheckDuration()
        {
            if (_stopwatch.Elapsed > maximumDuration)
                throw new TimeoutException("AtlasCore transfer exceeded its configured duration limit.");
        }
    }

    private async Task<AtlasGroupStore[]> OpenCandidateGroupsAsync(
        string candidateRoot,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<AtlasGroupStore>[_placement.KnownGroupCount];
        for (var group = 0; group < tasks.Length; group++)
        {
            var groupId = group;
            tasks[group] = Task.Run(async () =>
                await AtlasGroupStore.OpenAsync(
                        Path.Combine(candidateRoot, _placement.GroupDirectoryName(groupId)),
                        _options,
                        _transactionLog,
                        _options.DurableFileSystem,
                         CancellationToken.None,
                         groupId,
                         recoverMutationWal: false,
                         readHandleCache: _readHandleCache)
                    .ConfigureAwait(false));
        }

        try
        {
            return await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var opened = tasks
                .Where(static task => task.IsCompletedSuccessfully)
                .Select(static task => task.Result)
                .ToArray();
            await DisposeGroupsAsync(opened).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisposeCurrentGroupsAndResetAsync()
    {
        Task<AtlasGroupStore>[] tasks;
        lock (_groupSync)
        {
            tasks = _groups
                .Where(static task => task is not null)
                .Cast<Task<AtlasGroupStore>>()
                .ToArray();
        }

        var groups = await Task.WhenAll(tasks).ConfigureAwait(false);
        await DisposeGroupsAsync(groups).ConfigureAwait(false);
        lock (_groupSync)
        {
            Array.Clear(_groups);
        }
    }

    private static async Task DisposeGroupsAsync(IEnumerable<AtlasGroupStore> groups)
    {
        List<Exception>? failures = null;
        foreach (var group in groups)
        {
            try
            {
                await group.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more AtlasCore compaction groups failed to close.", failures);
        }
    }

    private void EnsureCompactionCapacity(long estimatedLiveBytes, AtlasCoreCompactionOptions options)
    {
        if (!options.RequireFreeSpaceAdmission)
        {
            return;
        }

        var root = Path.GetPathRoot(_rootDirectory)
            ?? throw new IOException("AtlasCore could not resolve the storage volume for compaction.");
        var required = checked(estimatedLiveBytes + options.MinimumFreeSpaceReserveBytes);
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < required)
        {
            throw new IOException(
                $"AtlasCore compaction requires approximately {required:N0} free bytes but only {available:N0} are available.");
        }
    }

    private static long MeasureDirectoryBytes(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length)
            : 0;

    private static (int Files, long Bytes) MeasureStoreTree(string root)
    {
        var files = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(path), "writer.lock", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files++;
            bytes += new FileInfo(path).Length;
        }

        return (files, bytes);
    }

    private static void EnsureSeparateTrees(string source, string destination, string operation)
    {
        if (IsSameOrDescendant(source, destination) || IsSameOrDescendant(destination, source))
        {
            throw new InvalidOperationException(
                $"AtlasCore {operation} source and destination must be separate directory trees.");
        }
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return string.Equals(relative, ".", StringComparison.Ordinal)
               || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && !string.Equals(relative, "..", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relative));
    }

    private static void TryDeleteGeneratedTree(string path, string marker)
    {
        if (!Path.GetFileName(path).Contains(marker, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteOwnedTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
