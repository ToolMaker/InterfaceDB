using Idb.Diagnostics;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

public sealed partial class SegmentCoreStore : IAsyncDisposable
{
    private readonly string _rootDirectory;
    private readonly SegmentCoreOptions _options;
    private readonly SegmentCoreStoreOwnerLease _ownerLease;
    private readonly PrimaryIndexMap _primaryIndex;
    private Dictionary<string, PrimaryIndexChange> _checkpointChanges = new(StringComparer.Ordinal);
    private readonly StripedObjectLogProbeWriter _writer;
    private ObjectLogRecordReader _reader;
    private readonly TransactionCommitJournal? _transactionJournal;
    private readonly TransactionWriteAheadLog? _transactionWal;
    private readonly ReaderWriterLockSlim _indexVisibility = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
    private readonly SemaphoreSlim _transactionPreparationGate = new(1, 1);
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly SemaphoreSlim _compactionGate = new(1, 1);
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PrimaryIndexCheckpointState? _latestCheckpoint;
    private readonly Dictionary<long, PrimaryIndexCheckpointFingerprint> _validatedCheckpointRoots = [];
    private long _checkpointGeneration;
    private int _tailRecordsReplayed;
    private readonly long _fullRecoveryFramesScanned;
    private long _repairedTailBytes;
    private long _committedTransactions;
    private long _rolledBackTransactions;
    private long _faultedTransactions;
    private long _dirtyStripeMask;
    private long _lastCheckpointElapsedTicks;
    private long _lastCompactionElapsedTicks;
    private int _activeMutations;
    private int _activeReads;
    private int _gateAdmissions;
    private int _readerRetirement;
    private int _exclusiveOperation;
    private int _checkpointing;
    private int _compacting;
    private int _faulted;
    private int _disposed;

    private SegmentCoreStore(
        string rootDirectory,
        SegmentCoreOptions options,
        SegmentCoreStoreOwnerLease ownerLease,
        PrimaryIndexMap primaryIndex,
        StripedObjectLogProbeWriter writer,
        TransactionCommitJournal? transactionJournal,
        TransactionWriteAheadLog? transactionWal,
        PrimaryIndexCheckpointState? latestCheckpoint,
        long checkpointGeneration,
        int tailRecordsReplayed,
        long fullRecoveryFramesScanned,
        long repairedTailBytes,
        ulong dirtyStripeMask = 0)
    {
        _rootDirectory = rootDirectory;
        _options = options;
        _ownerLease = ownerLease;
        _primaryIndex = primaryIndex;
        _writer = writer;
        _transactionJournal = transactionJournal;
        _transactionWal = transactionWal;
        _latestCheckpoint = latestCheckpoint;
        _reader = new ObjectLogRecordReader(rootDirectory, options.ReadOptions);
        _checkpointGeneration = checkpointGeneration;
        _tailRecordsReplayed = tailRecordsReplayed;
        _fullRecoveryFramesScanned = fullRecoveryFramesScanned;
        _repairedTailBytes = repairedTailBytes;
        _dirtyStripeMask = unchecked((long)dirtyStripeMask);
        if (options.RetainedVersions is { } retention)
            _retention = new(rootDirectory, retention, EnterHistoryReadAsync,
                (key, address, token) => _reader.ReadPayloadAsync(key, address, token),
                (key, address, budget, token) => _reader.ReadPayloadBudgetedAsync(key, address, budget, token));
    }

    public event EventHandler<SegmentCoreTransactionStateChangedEventArgs>? TransactionStateChanged;

    public int Count
    {
        get
        {
            _indexVisibility.EnterReadLock();
            try
            {
                return _primaryIndex.Count;
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }
        }
    }

    public IReadOnlyList<string> GetKeysSnapshot()
    {
        ThrowIfUnavailable();
        _indexVisibility.EnterReadLock();
        try
        {
            return _primaryIndex.Keys.ToArray();
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }
    }

    internal SegmentCoreSearchIndexStorageIdentity GetSearchIndexStorageIdentity(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
        _indexVisibility.EnterReadLock();
        try
        {
            snapshot = _primaryIndex.ToArray();
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }

        Array.Sort(
            snapshot,
            static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> metadata = stackalloc byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(metadata, snapshot.Length);
        hash.AppendData(metadata[..sizeof(int)]);
        byte[]? keyBuffer = null;
        try
        {
            foreach (var pair in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyBytes = Encoding.UTF8.GetByteCount(pair.Key);
                if (keyBuffer is null || keyBuffer.Length < keyBytes)
                {
                    if (keyBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
                    }

                    keyBuffer = ArrayPool<byte>.Shared.Rent(keyBytes);
                }

                _ = Encoding.UTF8.GetBytes(pair.Key, keyBuffer.AsSpan(0, keyBytes));
                BinaryPrimitives.WriteInt32LittleEndian(metadata, keyBytes);
                hash.AppendData(metadata[..sizeof(int)]);
                hash.AppendData(keyBuffer.AsSpan(0, keyBytes));

                var locator = pair.Value;
                BinaryPrimitives.WriteInt32LittleEndian(metadata, locator.Stripe);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[4..], locator.Address.SegmentId);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[12..], locator.Address.Offset);
                BinaryPrimitives.WriteInt32LittleEndian(metadata[20..], locator.Address.FrameLength);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[24..], locator.Address.Lsn);
                hash.AppendData(metadata[..32]);
            }
        }
        finally
        {
            if (keyBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(keyBuffer, clearArray: false);
            }
        }

        return new SegmentCoreSearchIndexStorageIdentity(snapshot.Length, hash.GetHashAndReset());
    }

    public SegmentCoreStatistics Statistics => new(
        Count,
        Volatile.Read(ref _checkpointGeneration),
        Volatile.Read(ref _checkpointing) != 0,
        Volatile.Read(ref _tailRecordsReplayed),
        _fullRecoveryFramesScanned,
        Volatile.Read(ref _repairedTailBytes),
        _writer.Statistics,
        Volatile.Read(ref _committedTransactions),
        Volatile.Read(ref _rolledBackTransactions),
        Volatile.Read(ref _faultedTransactions))
    {
        WalRecoveryPrefixBytesSkipped = _transactionWal?.RecoveryPrefixBytesSkipped ?? 0,
        PrimaryIndexIsPaged = _primaryIndex.IsPaged,
        DirtyStripes = BitOperations.PopCount(unchecked((ulong)Volatile.Read(ref _dirtyStripeMask))),
        PendingCheckpointChanges = GetPendingCheckpointChangeCount(),
        CompactionInProgress = Volatile.Read(ref _compacting) != 0,
        LastCheckpointElapsed = TimeSpan.FromTicks(Volatile.Read(ref _lastCheckpointElapsedTicks)),
        LastCompactionElapsed = TimeSpan.FromTicks(Volatile.Read(ref _lastCompactionElapsedTicks))
    };

    public static ValueTask<SegmentCoreStore> OpenAsync(
        string rootDirectory,
        SegmentCoreOptions? options = null,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(options?.Telemetry) ? OpenAsyncUnobserved(rootDirectory, options, cancellationToken)
            : OpenAsyncObserved(rootDirectory, options, cancellationToken);

    private static ValueTask<SegmentCoreStore> OpenAsyncObserved(
        string rootDirectory,
        SegmentCoreOptions? options = null,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(options?.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Initialize,
                () => OpenAsyncUnobserved(rootDirectory, options, cancellationToken), log: true, bytes: 0);

    private static async ValueTask<SegmentCoreStore> OpenAsyncUnobserved(
        string rootDirectory,
        SegmentCoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        options ??= new SegmentCoreOptions();
        options.Validate();
        var fullRoot = Path.GetFullPath(rootDirectory);
        var openStarted = SegmentCoreDiagnostics.StartTimestamp();
        try
        {
            var store = await OpenCoreAsync(fullRoot, options, cancellationToken).ConfigureAwait(false);
            SegmentCoreDiagnostics.RecordOperation(
                "open",
                openStarted,
                options.TransactionProtocol,
                succeeded: true);
            return store;
        }
        catch
        {
            SegmentCoreDiagnostics.RecordOperation(
                "open",
                openStarted,
                options.TransactionProtocol,
                succeeded: false);
            throw;
        }
    }

    private static async ValueTask<SegmentCoreStore> OpenCoreAsync(
        string fullRoot,
        SegmentCoreOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(fullRoot);
        var ownerLease = SegmentCoreStoreOwnerLease.Acquire(fullRoot);
        try
        {
            return await OpenOwnedCoreAsync(
                fullRoot,
                options,
                ownerLease,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await DisposeOpenResourceWithoutMaskingAsync(
                ownerLease,
                exception,
                "owner lease").ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SegmentCoreStore> OpenOwnedCoreAsync(
        string fullRoot,
        SegmentCoreOptions options,
        SegmentCoreStoreOwnerLease ownerLease,
        CancellationToken cancellationToken)
    {
        var stageStarted = SegmentCoreDiagnostics.StartTimestamp();
        _ = SegmentCoreStorageFormatManifest.ReadOrCreate(
            fullRoot,
            SegmentCoreStorageFormat.For(options.LogOptions),
            options.DurableFileSystem);
        SegmentCoreDiagnostics.RecordOpenStage(
            "storage_format_manifest",
            stageStarted,
            options.TransactionProtocol);
        stageStarted = SegmentCoreDiagnostics.StartTimestamp();
        var protocol = TransactionProtocolManifest.ReadOrCreate(
            fullRoot,
            options.TransactionProtocol,
            options.DurableFileSystem);
        SegmentCoreDiagnostics.RecordOpenStage(
            "transaction_protocol_manifest",
            stageStarted,
            protocol);
        if (protocol == SegmentCoreTransactionProtocol.MutationWriteAheadLog)
        {
            return await OpenWalStoreAsync(
                fullRoot,
                options,
                ownerLease,
                cancellationToken).ConfigureAwait(false);
        }

        TransactionCommitJournal? journal = null;
        StripedObjectLogProbeWriter? writer = null;
        PrimaryIndexMap? primaryIndex = null;
        try
        {
            stageStarted = SegmentCoreDiagnostics.StartTimestamp();
            journal = await TransactionCommitJournal.OpenAsync(
                fullRoot,
                options.LogOptions.DurabilityStrategy,
                options.DurableFileSystem,
                cancellationToken).ConfigureAwait(false);
            SegmentCoreDiagnostics.RecordOpenStage(
                "transaction_journal",
                stageStarted,
                protocol);
            var commitSnapshot = journal.CreateSnapshot();
            stageStarted = SegmentCoreDiagnostics.StartTimestamp();
            var checkpoint = PrimaryIndexCheckpoint.TryLoadLatest(
                fullRoot,
                options.LogOptions.StripeCount,
                options.PrimaryIndexOptions);
            primaryIndex = new PrimaryIndexMap(checkpoint?.Entries);
            SegmentCoreIndependentFiles.VerifyRecovery(fullRoot, checkpoint);
            SegmentCoreDiagnostics.RecordOpenStage(
                "primary_checkpoint",
                stageStarted,
                protocol);
            if (checkpoint is not null
                && checkpoint.TransactionWatermark > commitSnapshot.HighestTransactionId)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index checkpoint transaction watermark {checkpoint.TransactionWatermark:N0} "
                    + $"is ahead of durable commit journal {commitSnapshot.HighestTransactionId:N0}.");
            }

            var replayed = 0;
            long fullRecoveryFramesScanned = 0;
            long repairedTailBytes = 0;
            long highestObservedTransactionId = commitSnapshot.HighestTransactionId;
            stageStarted = SegmentCoreDiagnostics.StartTimestamp();

            if (checkpoint is not null)
            {
                var tailRecords = new List<StripedObjectLogReplayRecord>();
                writer = await StripedObjectLogProbeWriter.OpenFromCheckpointAsync(
                    fullRoot,
                    options.LogOptions,
                    checkpoint.Positions,
                    (stripe, record) => tailRecords.Add(new StripedObjectLogReplayRecord(stripe, record)),
                    options.DurableFileSystem,
                    cancellationToken).ConfigureAwait(false);
                commitSnapshot.ValidateRecoveryRecords(
                    tailRecords,
                    checkpoint.TransactionWatermark,
                    requireEveryCommitAfterWatermark: true);
                foreach (var item in tailRecords)
                {
                    highestObservedTransactionId = Math.Max(
                        highestObservedTransactionId,
                        item.Record.TransactionId);
                    if (item.Record.TransactionId != 0
                        && (!commitSnapshot.IsCommitted(item.Record.TransactionId)
                            || item.Record.TransactionId <= checkpoint.TransactionWatermark))
                    {
                        continue;
                    }

                    ApplyReplayRecord(primaryIndex, item.Stripe, item.Record);
                    replayed++;
                }
            }
            else
            {
                var manifestPath = Path.Combine(fullRoot, "striped-log.manifest");
                IReadOnlyList<ObjectLogRecoveryResult>? stripeRecoveries = null;
                if (File.Exists(manifestPath))
                {
                    var recovery = StripedObjectLogRecovery.Recover(
                        fullRoot,
                        repairActiveTails: true,
                        options.DurableFileSystem,
                        commitSnapshot.CommittedTransactionIds);
                    stripeRecoveries = recovery.StripeRecoveries;
                    repairedTailBytes = recovery.RepairedTailBytes;
                    fullRecoveryFramesScanned = recovery.ScannedFrames;
                    commitSnapshot.ValidateRecoveryRecords(
                        recovery.TransactionRecords,
                        transactionWatermark: 0,
                        requireEveryCommitAfterWatermark: true);
                    foreach (var item in recovery.TransactionRecords)
                    {
                        highestObservedTransactionId = Math.Max(
                            highestObservedTransactionId,
                            item.Record.TransactionId);
                    }

                    foreach (var pair in recovery.Records)
                    {
                        primaryIndex[pair.Key] = pair.Value.Address;
                    }
                }
                else
                {
                    commitSnapshot.ValidateRecoveryRecords(
                        [],
                        transactionWatermark: 0,
                        requireEveryCommitAfterWatermark: true);
                }

                writer = stripeRecoveries is not null
                    ? await StripedObjectLogProbeWriter.OpenRecoveredOwnedAsync(fullRoot, options.LogOptions,
                        stripeRecoveries, options.DurableFileSystem, cancellationToken).ConfigureAwait(false)
                    : await StripedObjectLogProbeWriter.OpenAsync(
                    fullRoot,
                    options.LogOptions,
                    options.DurableFileSystem,
                    cancellationToken).ConfigureAwait(false);
            }

            SegmentCoreDiagnostics.RecordOpenStage(
                "object_log_recovery",
                stageStarted,
                protocol);
            journal.AdvanceTransactionIdPast(highestObservedTransactionId);
            return new SegmentCoreStore(
                fullRoot,
                options,
                ownerLease,
                primaryIndex,
                writer,
                journal,
                transactionWal: null,
                checkpoint,
                checkpoint?.Generation ?? 0,
                replayed,
                fullRecoveryFramesScanned,
                repairedTailBytes + writer.RepairedTailBytesOnOpen);
        }
        catch (Exception exception)
        {
            await DisposeOpenResourceWithoutMaskingAsync(
                writer,
                exception,
                "object-log writer").ConfigureAwait(false);
            await DisposeOpenResourceWithoutMaskingAsync(
                journal,
                exception,
                "transaction journal").ConfigureAwait(false);
            DisposeOpenResourceWithoutMasking(primaryIndex, exception, "primary index");
            throw;
        }
    }

    private static async ValueTask<SegmentCoreStore> OpenWalStoreAsync(
        string fullRoot,
        SegmentCoreOptions options,
        SegmentCoreStoreOwnerLease ownerLease,
        CancellationToken cancellationToken)
    {
        PrimaryIndexCheckpointState? checkpoint = null;
        TransactionWriteAheadLog? wal = null;
        StripedObjectLogProbeWriter? writer = null;
        PrimaryIndexMap? primaryIndex = null;
        try
        {
            var stageStarted = SegmentCoreDiagnostics.StartTimestamp();
            checkpoint = PrimaryIndexCheckpoint.TryLoadLatest(
                fullRoot,
                options.LogOptions.StripeCount,
                options.PrimaryIndexOptions);
            primaryIndex = new PrimaryIndexMap(checkpoint?.Entries);
            SegmentCoreIndependentFiles.VerifyRecovery(fullRoot, checkpoint);
            SegmentCoreDiagnostics.RecordOpenStage(
                "primary_checkpoint",
                stageStarted,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            IReadOnlyList<StripedObjectLogReplayRecord> replayRecords = [];
            if (checkpoint is not null)
            {
                // WAL recovery and checkpoint-bounded object-log activation read independent
                // files. Transaction reconciliation still happens only after both complete.
                // TransactionWriteAheadLog.OpenAsync currently performs its recovery scan
                // synchronously, so offload that scan to overlap it with stripe activation.
                // The store-wide owner lease was acquired before recovery and prevents competing
                // processes from splitting ownership of the WAL and object-log leases.
                var tailRecords = new List<StripedObjectLogReplayRecord>();
                var walOpenTask = OpenWalInBackgroundAsync(
                    fullRoot,
                    options,
                    checkpoint.Generation,
                    checkpoint.TransactionWatermark,
                    cancellationToken);
                var writerOpenTask = OpenCheckpointObjectLogAsync(
                    fullRoot,
                    options,
                    checkpoint.Positions,
                    tailRecords,
                    cancellationToken);
                try
                {
                    await Task.WhenAll(walOpenTask, writerOpenTask).ConfigureAwait(false);
                }
                finally
                {
                    // Task.WhenAll drains both opens. Capture any successful sibling here so
                    // the outer failure path can release its handles and writer leases.
                    if (walOpenTask.IsCompletedSuccessfully)
                    {
                        wal = walOpenTask.Result;
                    }

                    if (writerOpenTask.IsCompletedSuccessfully)
                    {
                        writer = writerOpenTask.Result;
                    }
                }

                replayRecords = tailRecords;
            }
            else
            {
                stageStarted = SegmentCoreDiagnostics.StartTimestamp();
                wal = await TransactionWriteAheadLog.OpenAsync(
                    fullRoot,
                    options.LogOptions.DurabilityStrategy,
                    options.LogOptions.GroupCommitDelay,
                    options.LogOptions.MaximumBatchRecords,
                    options.LogOptions.MaximumCombinedWriteBytes,
                    options.DurableFileSystem,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                SegmentCoreDiagnostics.RecordOpenStage(
                    "transaction_wal",
                    stageStarted,
                    SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            }

            var openedWal = wal
                ?? throw new InvalidOperationException("Mutation WAL activation did not return a writer.");
            var walSnapshot = openedWal.CreateRecoverySnapshot();
            if (checkpoint is null && walSnapshot.BaseTransactionId != 0)
            {
                throw new ObjectLogCorruptionException(
                    $"Mutation WAL was recycled through transaction {walSnapshot.BaseTransactionId:N0}, "
                    + "but no valid primary-index checkpoint remains.");
            }

            if (checkpoint is not null
                && checkpoint.TransactionWatermark < walSnapshot.BaseTransactionId)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index checkpoint transaction watermark {checkpoint.TransactionWatermark:N0} "
                    + $"is behind recycled mutation WAL base {walSnapshot.BaseTransactionId:N0}.");
            }

            if (checkpoint is not null
                && checkpoint.TransactionWatermark > walSnapshot.HighestTransactionId)
            {
                throw new ObjectLogCorruptionException(
                    $"Primary-index checkpoint transaction watermark {checkpoint.TransactionWatermark:N0} "
                    + $"is ahead of durable mutation WAL {walSnapshot.HighestTransactionId:N0}.");
            }

            long fullRecoveryFramesScanned = 0;
            long repairedTailBytes = 0;
            if (checkpoint is null)
            {
                stageStarted = SegmentCoreDiagnostics.StartTimestamp();
                var manifestPath = Path.Combine(fullRoot, "striped-log.manifest");
                IReadOnlyList<ObjectLogRecoveryResult>? stripeRecoveries = null;
                if (File.Exists(manifestPath))
                {
                    var recovery = StripedObjectLogRecovery.Recover(
                        fullRoot,
                        repairActiveTails: true,
                        options.DurableFileSystem,
                        walSnapshot.CommittedTransactionIds);
                    stripeRecoveries = recovery.StripeRecoveries;
                    replayRecords = recovery.ReplayRecords;
                    repairedTailBytes = recovery.RepairedTailBytes;
                    fullRecoveryFramesScanned = recovery.ScannedFrames;
                }
                else
                {
                    replayRecords = [];
                }

                writer = stripeRecoveries is not null
                    ? await StripedObjectLogProbeWriter.OpenRecoveredOwnedAsync(fullRoot, options.LogOptions,
                        stripeRecoveries, options.DurableFileSystem, cancellationToken).ConfigureAwait(false)
                    : await StripedObjectLogProbeWriter.OpenAsync(
                    fullRoot,
                    options.LogOptions,
                    options.DurableFileSystem,
                    cancellationToken).ConfigureAwait(false);
                SegmentCoreDiagnostics.RecordOpenStage(
                    "object_log_recovery",
                    stageStarted,
                    SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            }

            var openedWriter = writer
                ?? throw new InvalidOperationException("Object-log activation did not return a writer.");
            stageStarted = SegmentCoreDiagnostics.StartTimestamp();
            var recoveryResult = await RecoverWalTransactionsAsync(
                primaryIndex,
                openedWriter,
                walSnapshot,
                replayRecords,
                checkpoint,
                cancellationToken).ConfigureAwait(false);
            SegmentCoreDiagnostics.RecordOpenStage(
                "transaction_replay",
                stageStarted,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            var highestObservedTransactionId = replayRecords
                .Select(static item => item.Record.TransactionId)
                .Append(walSnapshot.HighestTransactionId)
                .Max();
            openedWal.AdvanceTransactionIdPast(highestObservedTransactionId);
            openedWal.ReleaseRecoveryPayloads();
            return new SegmentCoreStore(
                fullRoot,
                options,
                ownerLease,
                primaryIndex,
                openedWriter,
                transactionJournal: null,
                openedWal,
                checkpoint,
                checkpoint?.Generation ?? 0,
                checkpoint is null ? 0 : recoveryResult.RecordsReplayed,
                fullRecoveryFramesScanned,
                repairedTailBytes + openedWriter.RepairedTailBytesOnOpen,
                recoveryResult.DirtyStripeMask);
        }
        catch (Exception exception)
        {
            await DisposeOpenResourceWithoutMaskingAsync(
                writer,
                exception,
                "object-log writer").ConfigureAwait(false);
            await DisposeOpenResourceWithoutMaskingAsync(
                wal,
                exception,
                "mutation WAL").ConfigureAwait(false);
            DisposeOpenResourceWithoutMasking(primaryIndex, exception, "primary index");
            throw;
        }
    }

    private static async Task<TransactionWriteAheadLog> OpenWalInBackgroundAsync(
        string fullRoot,
        SegmentCoreOptions options,
        long checkpointGeneration,
        long checkpointTransactionWatermark,
        CancellationToken cancellationToken)
    {
        var stageStarted = SegmentCoreDiagnostics.StartTimestamp();
        var wal = await Task.Run(
            () => TransactionWriteAheadLog.OpenAsync(
                fullRoot,
                options.LogOptions.DurabilityStrategy,
                options.LogOptions.GroupCommitDelay,
                options.LogOptions.MaximumBatchRecords,
                options.LogOptions.MaximumCombinedWriteBytes,
                options.DurableFileSystem,
                checkpointGeneration,
                checkpointTransactionWatermark,
                cancellationToken).AsTask(),
            cancellationToken).ConfigureAwait(false);
        try
        {
            SegmentCoreDiagnostics.RecordOpenStage(
                "transaction_wal",
                stageStarted,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            return wal;
        }
        catch (Exception exception)
        {
            await DisposeOpenResourceWithoutMaskingAsync(
                wal,
                exception,
                "mutation WAL after telemetry failure").ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<StripedObjectLogProbeWriter> OpenCheckpointObjectLogAsync(
        string fullRoot,
        SegmentCoreOptions options,
        IReadOnlyList<ObjectLogWritePosition> checkpointPositions,
        List<StripedObjectLogReplayRecord> tailRecords,
        CancellationToken cancellationToken)
    {
        var stageStarted = SegmentCoreDiagnostics.StartTimestamp();
        var writer = await StripedObjectLogProbeWriter.OpenFromCheckpointAsync(
            fullRoot,
            options.LogOptions,
            checkpointPositions,
            (stripe, record) => tailRecords.Add(new StripedObjectLogReplayRecord(stripe, record)),
            options.DurableFileSystem,
            cancellationToken).ConfigureAwait(false);
        try
        {
            SegmentCoreDiagnostics.RecordOpenStage(
                "object_log_recovery",
                stageStarted,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog);
            return writer;
        }
        catch (Exception exception)
        {
            await DisposeOpenResourceWithoutMaskingAsync(
                writer,
                exception,
                "object-log writer after telemetry failure").ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask DisposeOpenResourceWithoutMaskingAsync(
        IAsyncDisposable? resource,
        Exception openException,
        string resourceName)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            RecordOpenCleanupFailure(openException, resourceName, cleanupException);
        }
    }

    private static void DisposeOpenResourceWithoutMasking(
        IDisposable? resource,
        Exception openException,
        string resourceName)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception cleanupException)
        {
            RecordOpenCleanupFailure(openException, resourceName, cleanupException);
        }
    }

    private static void RecordOpenCleanupFailure(
        Exception openException,
        string resourceName,
        Exception cleanupException)
    {
        try
        {
            openException.Data[$"SegmentCore open cleanup failure: {resourceName}"] = cleanupException;
        }
        catch
        {
            // Cleanup diagnostics must never replace the original open failure.
        }
    }

    private sealed class SegmentCoreStoreOwnerLease : IAsyncDisposable
    {
        // Keep this lexical guard ordinal. Windows can host case-sensitive directories,
        // while the FileShare.None lease below supplies filesystem-identity contention.
        private static readonly ConcurrentDictionary<string, byte> ActiveRoots =
            new(StringComparer.Ordinal);
        private readonly string _rootDirectory;
        private readonly FileStream _stream;
        private int _disposed;

        private SegmentCoreStoreOwnerLease(string rootDirectory, FileStream stream)
        {
            _rootDirectory = rootDirectory;
            _stream = stream;
        }

        public static SegmentCoreStoreOwnerLease Acquire(string rootDirectory)
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            if (!ActiveRoots.TryAdd(fullRoot, 0))
            {
                throw new IOException($"Another SegmentCore store already owns '{fullRoot}'.");
            }

            FileStream? stream = null;
            try
            {
                if (!OperatingSystem.IsWindows()
                    && !OperatingSystem.IsLinux()
                    && !OperatingSystem.IsAndroid()
                    && !OperatingSystem.IsMacOS()
                    && !OperatingSystem.IsIOS()
                    && !OperatingSystem.IsMacCatalyst())
                {
                    throw new PlatformNotSupportedException(
                        "The SegmentCore owner lease is implemented only for Windows, Linux, Android, macOS, iOS, and Mac Catalyst.");
                }

                stream = new FileStream(
                    Path.Combine(fullRoot, "segment-core.writer.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                // FileShare.None is deliberate. On Unix, .NET maps this to an open-description
                // flock, so symlink/bind-mount aliases still contend and closing an unrelated
                // descriptor cannot release this lease. Deployments must not disable .NET file locking.

                return new SegmentCoreStoreOwnerLease(fullRoot, stream);
            }
            catch (Exception exception)
            {
                stream?.Dispose();
                ActiveRoots.TryRemove(fullRoot, out _);
                if (exception is IOException)
                {
                    throw new IOException(
                        $"Another SegmentCore store already owns '{fullRoot}'.",
                        exception);
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ActiveRoots.TryRemove(_rootDirectory, out _);
            }
        }
    }

    private static async ValueTask<WalRecoveryResult> RecoverWalTransactionsAsync(
        PrimaryIndexMap primaryIndex,
        StripedObjectLogProbeWriter writer,
        TransactionWalSnapshot walSnapshot,
        IReadOnlyList<StripedObjectLogReplayRecord> replayRecords,
        PrimaryIndexCheckpointState? checkpoint,
        CancellationToken cancellationToken)
    {
        var transactionWatermark = checkpoint?.TransactionWatermark ?? 0;
        var commitsAfterWatermark = walSnapshot.After(transactionWatermark).ToArray();
        if (replayRecords.Count == 0 && commitsAfterWatermark.Length == 0)
        {
            return default;
        }

        var mutationMaps = commitsAfterWatermark.ToDictionary(
            static commit => commit.TransactionId,
            static commit => commit.Mutations.ToDictionary(
                static mutation => mutation.Mutation.Key,
                StringComparer.Ordinal));
        var candidates = new Dictionary<(long TransactionId, string Key), List<StripedObjectLogReplayRecord>>();
        var operations = new List<WalReplayOperation>(replayRecords.Count + 16);
        ulong dirtyStripeMask = 0;
        foreach (var item in replayRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedStripe = writer.SelectStripe(item.Record.Key);
            if (item.Stripe != expectedStripe)
            {
                throw new ObjectLogCorruptionException(
                    $"Key '{item.Record.Key}' was recovered from stripe {item.Stripe}; expected {expectedStripe}.");
            }

            if (item.Record.TransactionId == 0)
            {
                operations.Add(new WalReplayOperation(
                    item.Stripe,
                    item.Record.Address.Lsn,
                    item.Record.Kind,
                    item.Record.Key,
                    new StripedObjectLogRecordAddress(item.Stripe, item.Record.Address)));
                continue;
            }

            if (!walSnapshot.TryGetCommit(item.Record.TransactionId, out var commit))
            {
                if (item.Record.TransactionId <= walSnapshot.BaseTransactionId)
                {
                    throw new ObjectLogCorruptionException(
                        $"Transaction frame {item.Record.TransactionId:N0} appears after a checkpoint "
                        + $"that recycled the WAL through {walSnapshot.BaseTransactionId:N0}.");
                }

                continue;
            }

            if (item.Record.TransactionId <= transactionWatermark)
            {
                throw new ObjectLogCorruptionException(
                    $"Committed WAL transaction {item.Record.TransactionId:N0} crosses primary-index checkpoint "
                    + $"watermark {transactionWatermark:N0}.");
            }

            if (!mutationMaps[commit.TransactionId].TryGetValue(item.Record.Key, out var expectedMutation)
                || !MatchesWalMutation(writer, item, expectedMutation))
            {
                throw new ObjectLogCorruptionException(
                    $"Derived frame for WAL transaction {item.Record.TransactionId:N0} does not match its mutation set.");
            }

            var key = (item.Record.TransactionId, item.Record.Key);
            if (!candidates.TryGetValue(key, out var matchingRecords))
            {
                matchingRecords = [];
                candidates.Add(key, matchingRecords);
            }

            matchingRecords.Add(item);
        }

        var logicalAddresses = operations
            .Select(static operation => (operation.Stripe, operation.LogicalLsn))
            .ToHashSet();
        if (logicalAddresses.Count != operations.Count)
        {
            throw new ObjectLogCorruptionException("Recovered autocommit frames contain duplicate logical locators.");
        }
        foreach (var commit in commitsAfterWatermark)
        {
            foreach (var walMutation in commit.Mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mutation = walMutation.Mutation;
                var prepared = walMutation.PreparedAddress;
                var expectedStripe = writer.SelectStripe(mutation.Key);
                dirtyStripeMask |= 1UL << expectedStripe;
                if (prepared.Stripe != expectedStripe
                    || prepared.Stripe >= writer.StripeCount
                    || !logicalAddresses.Add((prepared.Stripe, prepared.Address.Lsn)))
                {
                    throw new ObjectLogCorruptionException(
                        $"Mutation WAL transaction {commit.TransactionId:N0} has an invalid prepared locator.");
                }

                if (checkpoint is not null
                    && prepared.Address.Lsn <= checkpoint.Positions[prepared.Stripe].LastLsn)
                {
                    throw new ObjectLogCorruptionException(
                        $"Mutation WAL transaction {commit.TransactionId:N0} is after the checkpoint watermark "
                        + "but its prepared locator is not after the checkpoint position.");
                }

                StripedObjectLogRecordAddress effectiveAddress;
                if (candidates.TryGetValue((commit.TransactionId, mutation.Key), out var matchingRecords))
                {
                    var selected = matchingRecords.MaxBy(static item => item.Record.Address.Lsn);
                    effectiveAddress = new StripedObjectLogRecordAddress(selected.Stripe, selected.Record.Address);
                }
                else
                {
                    effectiveAddress = await writer.AppendDerivedTransactionalAsync(
                        mutation.Kind,
                        mutation.Key,
                        mutation.Payload,
                        commit.TransactionId,
                        cancellationToken).ConfigureAwait(false);
                }

                operations.Add(new WalReplayOperation(
                    prepared.Stripe,
                    prepared.Address.Lsn,
                    mutation.Kind,
                    mutation.Key,
                    effectiveAddress));
            }
        }

        operations.Sort(static (left, right) =>
        {
            var stripeComparison = left.Stripe.CompareTo(right.Stripe);
            return stripeComparison != 0 ? stripeComparison : left.LogicalLsn.CompareTo(right.LogicalLsn);
        });
        foreach (var operation in operations)
        {
            if (operation.Kind == ObjectLogRecordKind.Put)
            {
                primaryIndex[operation.Key] = operation.EffectiveAddress;
            }
            else
            {
                primaryIndex.TryRemove(operation.Key, out _);
            }
        }

        return new WalRecoveryResult(operations.Count, dirtyStripeMask);
    }

    private static bool MatchesWalMutation(
        StripedObjectLogProbeWriter writer,
        StripedObjectLogReplayRecord candidate,
        TransactionWalMutation expected)
    {
        var mutation = expected.Mutation;
        var prepared = expected.PreparedAddress;
        var candidateAddress = candidate.Record.Address;
        return candidate.Stripe == prepared.Stripe
            && candidate.Stripe == writer.SelectStripe(mutation.Key)
            && candidate.Record.Kind == mutation.Kind
            && candidate.Record.DataChecksum == ObjectLogFormat.ComputeDataChecksum(
                mutation.Key,
                mutation.Payload)
            && candidateAddress.Lsn >= prepared.Address.Lsn
            && (candidateAddress.Lsn != prepared.Address.Lsn
                || candidateAddress == prepared.Address);
    }

    private readonly record struct WalReplayOperation(
        int Stripe,
        long LogicalLsn,
        ObjectLogRecordKind Kind,
        string Key,
        StripedObjectLogRecordAddress EffectiveAddress);

    private readonly record struct WalRecoveryResult(int RecordsReplayed, ulong DirtyStripeMask);

    private sealed record CheckpointCapture(
        IReadOnlyList<ObjectLogWritePosition> Positions,
        IReadOnlyDictionary<string, StripedObjectLogRecordAddress>? Entries,
        IReadOnlyDictionary<string, PrimaryIndexChange> Changes,
        long PreviousGeneration,
        long TransactionWatermark,
        int LiveEntryCount,
        bool UseFull,
        bool IsNoOp,
        PrimaryIndexCheckpointState? Basis);

    public ValueTask<ISegmentCoreTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        var transaction = new SegmentCoreTransaction(this);
        OnTransactionStateChanged(
            transaction.Id,
            SegmentCoreTransactionState.Created,
            SegmentCoreTransactionState.Active,
            failure: null);
        return ValueTask.FromResult<ISegmentCoreTransaction>(transaction);
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
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Save,
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
            var address = await _writer.AppendValidatedAsync(key, payload, cancellationToken).ConfigureAwait(false);
            _indexVisibility.EnterWriteLock();
            try
            {
                PublishIfNewer(_primaryIndex, key, address);
                _checkpointChanges[key] = new PrimaryIndexChange(false, _primaryIndex[key]);
            }
            finally
            {
                _indexVisibility.ExitWriteLock();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeMutations);
        }
    }

    public ValueTask SaveAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? SaveAsync(key, payload, cancellationToken)
            : RequireOwnedTransaction(transaction).SaveAsync(key, payload, cancellationToken);

    internal ValueTask SaveOwnedAsync(
        string key,
        byte[] payload,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? SaveAsync(key, payload, cancellationToken)
            : RequireOwnedTransaction(transaction).SaveOwnedAsync(key, payload, cancellationToken);

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? DeleteAsyncUnobserved(key, cancellationToken)
            : DeleteAsyncObserved(key, cancellationToken);

    private ValueTask<bool> DeleteAsyncObserved(string key, CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Delete,
                () => DeleteAsyncUnobserved(key, cancellationToken), log: true, bytes: 0);

    private async ValueTask<bool> DeleteAsyncUnobserved(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _indexVisibility.EnterReadLock();
            try
            {
                if (!_primaryIndex.ContainsKey(key))
                {
                    return false;
                }
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            var tombstone = await _writer.DeleteValidatedAsync(key, cancellationToken).ConfigureAwait(false);
            _indexVisibility.EnterWriteLock();
            try
            {
                var removed = RemoveIfNotNewer(_primaryIndex, key, tombstone);
                if (removed)
                {
                    _checkpointChanges[key] = new PrimaryIndexChange(true, default);
                    _retention?.Deleted(key);
                }

                return removed;
            }
            finally
            {
                _indexVisibility.ExitWriteLock();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeMutations);
        }
    }

    public ValueTask<bool> DeleteAsync(
        string key,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? DeleteAsync(key, cancellationToken)
            : RequireOwnedTransaction(transaction).DeleteAsync(key, cancellationToken);

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? GetAsyncUnobserved(key, cancellationToken)
            : GetAsyncObserved(key, cancellationToken);

    private ValueTask<byte[]?> GetAsyncObserved(string key, CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Read,
                () => GetAsyncUnobserved(key, cancellationToken), log: true, bytes: 0);

    private async ValueTask<byte[]?> GetAsyncUnobserved(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StripedObjectLogRecordAddress locator;
            _indexVisibility.EnterReadLock();
            try
            {
                if (!_primaryIndex.TryGetValue(key, out locator))
                {
                    return null;
                }
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            return await _reader.ReadPayloadAsync(key, locator, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    // Results carry logical keys and follow physical read order; missing keys are omitted.
    internal ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadManyAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? ReadManyAsyncUnobserved(keys, cancellationToken)
            : ReadManyAsyncObserved(keys, cancellationToken);

    private ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadManyAsyncObserved(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Read,
                () => ReadManyAsyncUnobserved(keys, cancellationToken), log: true, bytes: 0);

    private async ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadManyAsyncUnobserved(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = new List<KeyValuePair<string, StripedObjectLogRecordAddress>>(keys.Count);
            _indexVisibility.EnterReadLock();
            try
            {
                foreach (var key in keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentException.ThrowIfNullOrWhiteSpace(key);
                    if (_primaryIndex.TryGetValue(key, out var locator))
                    {
                        snapshot.Add(new KeyValuePair<string, StripedObjectLogRecordAddress>(key, locator));
                    }
                }
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            return await _reader.ReadManyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    public ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadAllAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? ReadAllAsyncUnobserved(cancellationToken)
            : ReadAllAsyncObserved(cancellationToken);

    private ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadAllAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Query,
                () => ReadAllAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<IReadOnlyList<SegmentCoreRecord>> ReadAllAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
            _indexVisibility.EnterReadLock();
            try
            {
                snapshot = _primaryIndex.ToArray();
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            return await _reader.ReadManyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    public async IAsyncEnumerable<SegmentCoreRecord> StreamAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
            _indexVisibility.EnterReadLock();
            try
            {
                snapshot = _primaryIndex.ToArray();
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            var batchSize = _options.ReadOptions.StreamingBatchRecords;
            for (var offset = 0; offset < snapshot.Length; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, snapshot.Length - offset);
                var records = await _reader.ReadManyAsync(
                    new ArraySegment<KeyValuePair<string, StripedObjectLogRecordAddress>>(
                        snapshot,
                        offset,
                        count),
                    cancellationToken).ConfigureAwait(false);
                foreach (var record in records)
                {
                    yield return record;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    public ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? VerifyIntegrityAsyncUnobserved(cancellationToken)
            : VerifyIntegrityAsyncObserved(cancellationToken);

    private ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Verify,
                () => VerifyIntegrityAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var telemetryStarted = SegmentCoreDiagnostics.StartTimestamp();
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        var entriesChecked = 0;
        long payloadBytesChecked = 0;
        var checkpointGeneration = Volatile.Read(ref _checkpointGeneration);
        var readEntered = false;
        try
        {
            await EnterReadOperationAsync(cancellationToken).ConfigureAwait(false);
            readEntered = true;
            KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
            _indexVisibility.EnterReadLock();
            try
            {
                snapshot = _primaryIndex.ToArray();
            }
            finally
            {
                _indexVisibility.ExitReadLock();
            }

            await foreach (var record in StreamSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false))
            {
                entriesChecked++;
                payloadBytesChecked += record.Payload.Length;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add($"Live-record verification failed: {exception.Message}");
        }
        finally
        {
            if (readEntered)
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        try
        {
            var checkpoint = PrimaryIndexCheckpoint.TryLoadLatest(
                _rootDirectory,
                _options.LogOptions.StripeCount);
            if (checkpointGeneration > 0
                && (checkpoint is null || checkpoint.Generation < checkpointGeneration))
            {
                failures.Add(
                    $"No valid checkpoint at or after in-memory generation {checkpointGeneration:N0} was found.");
            }
        }
        catch (Exception exception)
        {
            failures.Add($"Checkpoint verification failed: {exception.Message}");
        }

        stopwatch.Stop();
        SegmentCoreDiagnostics.RecordOperation(
            "integrity_verify",
            telemetryStarted,
            _options.TransactionProtocol,
            failures.Count == 0);
        return new SegmentCoreIntegrityResult(
            failures.Count == 0,
            entriesChecked,
            payloadBytesChecked,
            checkpointGeneration,
            stopwatch.Elapsed,
            failures);
    }

    private async IAsyncEnumerable<SegmentCoreRecord> StreamSnapshotAsync(
        KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batchSize = _options.ReadOptions.StreamingBatchRecords;
        for (var offset = 0; offset < snapshot.Length; offset += batchSize)
        {
            var count = Math.Min(batchSize, snapshot.Length - offset);
            var records = await _reader.ReadManyAsync(
                new ArraySegment<KeyValuePair<string, StripedObjectLogRecordAddress>>(
                    snapshot,
                    offset,
                    count),
                cancellationToken).ConfigureAwait(false);
            foreach (var record in records)
            {
                yield return record;
            }
        }
    }

    public async ValueTask<SegmentCoreCompactionResult> CompactAsync(
        CancellationToken cancellationToken = default) =>
        await CompactAsync(new SegmentCoreCompactionOptions(), cancellationToken).ConfigureAwait(false);

    public ValueTask<SegmentCoreCompactionResult> CompactAsync(
        SegmentCoreCompactionOptions options,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CompactAsyncUnobserved(options, cancellationToken)
            : CompactAsyncObserved(options, cancellationToken);

    private ValueTask<SegmentCoreCompactionResult> CompactAsyncObserved(
        SegmentCoreCompactionOptions options,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Compact,
                () => CompactAsyncUnobserved(options, cancellationToken), log: true, bytes: 0);

    private async ValueTask<SegmentCoreCompactionResult> CompactAsyncUnobserved(
        SegmentCoreCompactionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ThrowIfUnavailable();
        if (_transactionWal is null)
        {
            throw new NotSupportedException(
                "Online segment compaction currently requires the mutation-WAL transaction protocol.");
        }

        if (_options.CheckpointGenerationsToKeep < 2)
        {
            throw new InvalidOperationException(
                "Online segment compaction requires at least two retained checkpoint generations.");
        }

        if (options.Mode == SegmentCoreCompactionMode.IndependentOutput)
            return await CompactIndependentAsync(options, cancellationToken).ConfigureAwait(false);

        await EnterShutdownSafeGateAsync(_compactionGate, cancellationToken).ConfigureAwait(false);
        try
        {
            await EnterShutdownSafeGateAsync(_checkpointGate, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _compactionGate.Release();
            throw;
        }

        var stopwatch = Stopwatch.StartNew();
        var telemetryStarted = SegmentCoreDiagnostics.StartTimestamp();
        var telemetrySucceeded = false;
        Volatile.Write(ref _compacting, 1);
        try
        {
            var bytesBefore = GetStorageBytes(_rootDirectory);
            long estimatedLiveFrameBytes;
            string[] keys;
            long[] compactedSegmentFloor;
            var longestPreparationPause = TimeSpan.Zero;
            var preparationSlices = 0;
            for (var stripe = 0;
                 stripe < _writer.StripeCount;
                 stripe += options.MaximumStripesPerPreparationSlice)
            {
                var stripeCount = Math.Min(
                    options.MaximumStripesPerPreparationSlice,
                    _writer.StripeCount - stripe);
                var rotationSlice = Stopwatch.StartNew();
                await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _writer.ForceRotateStripesAsync(stripe, stripeCount, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ExitExclusiveOperation();
                    rotationSlice.Stop();
                }

                preparationSlices++;
                if (rotationSlice.Elapsed > longestPreparationPause)
                {
                    longestPreparationPause = rotationSlice.Elapsed;
                }
            }

            var catalogSlice = Stopwatch.StartNew();
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                keys = _primaryIndex.Keys.ToArray();
                estimatedLiveFrameBytes = _primaryIndex.Values.Sum(
                    static locator => (long)locator.Address.FrameLength);
                EnsureCompactionCapacity(options, checked(estimatedLiveFrameBytes + (_retention?.Statistics.RetainedBytes ?? 0)));
                compactedSegmentFloor = _writer.GetWritePositions()
                    .Select(static position => position.SegmentId)
                    .ToArray();
            }
            finally
            {
                ExitExclusiveOperation();
                catalogSlice.Stop();
            }

            preparationSlices++;
            if (catalogSlice.Elapsed > longestPreparationPause)
            {
                longestPreparationPause = catalogSlice.Elapsed;
            }

            long copied = 0;
            long copiedFrameBytes = 0;
            var slices = preparationSlices;
            var longestSlice = longestPreparationPause;
            var longestCopySlice = TimeSpan.Zero;
            for (var offset = 0; offset < keys.Length; offset += options.MaximumRecordsPerSlice)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sliceStopwatch = Stopwatch.StartNew();
                await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var count = Math.Min(options.MaximumRecordsPerSlice, keys.Length - offset);
                    var snapshot = new List<KeyValuePair<string, StripedObjectLogRecordAddress>>(count);
                    _indexVisibility.EnterReadLock();
                    try
                    {
                        for (var index = 0; index < count; index++)
                        {
                            var key = keys[offset + index];
                            if (_primaryIndex.TryGetValue(key, out var locator))
                            {
                                snapshot.Add(new KeyValuePair<string, StripedObjectLogRecordAddress>(key, locator));
                            }
                        }
                    }
                    finally
                    {
                        _indexVisibility.ExitReadLock();
                    }

                    var records = await _reader.ReadManyAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    var tasks = new Task<StripedObjectLogRecordAddress>[records.Count];
                    for (var index = 0; index < records.Count; index++)
                    {
                        var record = records[index];
                        tasks[index] = _writer.AppendMaintenanceAsync(
                            record.Key,
                            record.Payload,
                            cancellationToken).AsTask();
                    }

                    var addresses = await Task.WhenAll(tasks).ConfigureAwait(false);
                    MarkDirtyStripes(addresses);
                    _indexVisibility.EnterWriteLock();
                    try
                    {
                        for (var index = 0; index < records.Count; index++)
                        {
                            var key = records[index].Key;
                            _primaryIndex[key] = addresses[index];
                            _checkpointChanges[key] = new PrimaryIndexChange(false, addresses[index]);
                        }
                    }
                    finally
                    {
                        _indexVisibility.ExitWriteLock();
                    }

                    copied += records.Count;
                    copiedFrameBytes += addresses.Sum(static address => (long)address.Address.FrameLength);
                }
                finally
                {
                    ExitExclusiveOperation();
                    sliceStopwatch.Stop();
                }

                slices++;
                if (sliceStopwatch.Elapsed > longestSlice)
                {
                    longestSlice = sliceStopwatch.Elapsed;
                }

                if (sliceStopwatch.Elapsed > longestCopySlice)
                {
                    longestCopySlice = sliceStopwatch.Elapsed;
                }

                if (options.DelayBetweenSlices > TimeSpan.Zero && offset + options.MaximumRecordsPerSlice < keys.Length)
                {
                    await Task.Delay(options.DelayBetweenSlices, cancellationToken).ConfigureAwait(false);
                }
            }

            var firstCheckpointPause = await PublishCompactionCheckpointAsync(cancellationToken)
                .ConfigureAwait(false);
            var secondCheckpointPause = await PublishCompactionCheckpointAsync(cancellationToken)
                .ConfigureAwait(false);
            if (firstCheckpointPause > longestSlice)
            {
                longestSlice = firstCheckpointPause;
            }

            if (secondCheckpointPause > longestSlice)
            {
                longestSlice = secondCheckpointPause;
            }

            var finalSlice = Stopwatch.StartNew();
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _readerRetirement, 1);
            try
            {
                while (Volatile.Read(ref _activeReads) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }

                if (_retention is not null)
                {
                    EnsureCompactionCapacity(options, _retention.Statistics.RetainedBytes);
                    await _retention.RelocateAsync(address => address.Address.SegmentId < compactedSegmentFloor[address.Stripe], cancellationToken).ConfigureAwait(false);
                }
                _reader.Dispose();
                try
                {
                    DeleteSupersededSegments(compactedSegmentFloor);
                }
                finally
                {
                    _reader = new ObjectLogRecordReader(_rootDirectory, _options.ReadOptions);
                }
            }
            finally
            {
                Volatile.Write(ref _readerRetirement, 0);
                ExitExclusiveOperation();
                finalSlice.Stop();
            }

            slices++;
            if (finalSlice.Elapsed > longestSlice)
            {
                longestSlice = finalSlice.Elapsed;
            }

            var bytesAfter = GetStorageBytes(_rootDirectory);
            stopwatch.Stop();
            Volatile.Write(ref _lastCompactionElapsedTicks, stopwatch.Elapsed.Ticks);
            SegmentCoreDiagnostics.RecordStorageBytes("compaction.before", bytesBefore);
            SegmentCoreDiagnostics.RecordStorageBytes("compaction.after", bytesAfter);
            SegmentCoreDiagnostics.RecordStorageBytes(
                "compaction.reclaimed",
                Math.Max(0, bytesBefore - bytesAfter));
            telemetrySucceeded = true;
            return new SegmentCoreCompactionResult(
                Count,
                copied,
                bytesBefore,
                bytesAfter,
                Math.Max(0, bytesBefore - bytesAfter),
                stopwatch.Elapsed)
            {
                BytesCopied = copiedFrameBytes,
                Slices = slices,
                LongestExclusiveSlice = longestSlice,
                PreparationPause = longestPreparationPause,
                LongestCopySlice = longestCopySlice,
                LongestCheckpointCapturePause = firstCheckpointPause > secondCheckpointPause
                    ? firstCheckpointPause
                    : secondCheckpointPause,
                FinalizationPause = finalSlice.Elapsed
            };
        }
        finally
        {
            SegmentCoreDiagnostics.RecordOperation(
                "compaction",
                telemetryStarted,
                _options.TransactionProtocol,
                telemetrySucceeded);
            Volatile.Write(ref _compacting, 0);
            _checkpointGate.Release();
            _compactionGate.Release();
        }
    }

    public ValueTask<byte[]?> GetAsync(
        string key,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        transaction is null
            ? GetAsync(key, cancellationToken)
            : RequireOwnedTransaction(transaction).GetAsync(key, cancellationToken);

    public ValueTask<SegmentCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CheckpointAsyncUnobserved(cancellationToken)
            : CheckpointAsyncObserved(cancellationToken);

    private ValueTask<SegmentCoreCheckpointResult> CheckpointAsyncObserved(
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Checkpoint,
                () => CheckpointAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask<SegmentCoreCheckpointResult> CheckpointAsyncUnobserved(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        var operationStopwatch = Stopwatch.StartNew();
        await EnterShutdownSafeGateAsync(_checkpointGate, cancellationToken).ConfigureAwait(false);
        var telemetryStarted = SegmentCoreDiagnostics.StartTimestamp();
        Volatile.Write(ref _checkpointing, 1);
        CheckpointCapture? capture = null;
        try
        {
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                capture = await CaptureCheckpointAsync(
                    forceFull: false,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitExclusiveOperation();
            }

            var result = await PublishCheckpointAsync(
                capture,
                forceFull: false,
                cancellationToken).ConfigureAwait(false);
            operationStopwatch.Stop();
            Volatile.Write(ref _lastCheckpointElapsedTicks, operationStopwatch.Elapsed.Ticks);
            SegmentCoreDiagnostics.RecordStorageBytes("checkpoint", result.Bytes);
            SegmentCoreDiagnostics.RecordOperation(
                "checkpoint",
                telemetryStarted,
                _options.TransactionProtocol,
                succeeded: true);
            return result with { Elapsed = operationStopwatch.Elapsed };
        }
        catch
        {
            SegmentCoreDiagnostics.RecordOperation(
                "checkpoint",
                telemetryStarted,
                _options.TransactionProtocol,
                succeeded: false);
            if (capture is not null)
            {
                RestoreCheckpointChanges(capture.Changes);
            }

            throw;
        }
        finally
        {
            Volatile.Write(ref _checkpointing, 0);
            _checkpointGate.Release();
        }
    }

    public ValueTask<SegmentCoreBackupResult> BackupAsync(
        string destinationDirectory,
        SegmentCoreBackupOptions? backupOptions = null,
        CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? BackupAsyncUnobserved(destinationDirectory, backupOptions, cancellationToken)
            : BackupAsyncObserved(destinationDirectory, backupOptions, cancellationToken);

    private ValueTask<SegmentCoreBackupResult> BackupAsyncObserved(
        string destinationDirectory,
        SegmentCoreBackupOptions? backupOptions = null,
        CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Backup,
                () => BackupAsyncUnobserved(destinationDirectory, backupOptions, cancellationToken), log: true, bytes: 0);

    private async ValueTask<SegmentCoreBackupResult> BackupAsyncUnobserved(
        string destinationDirectory,
        SegmentCoreBackupOptions? backupOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        backupOptions ??= new SegmentCoreBackupOptions();
        var destination = Path.GetFullPath(destinationDirectory);
        if (IsPathWithin(destination, _rootDirectory) || IsPathWithin(_rootDirectory, destination))
        {
            throw new ArgumentException(
                "A SegmentCore backup must be outside the live store tree.",
                nameof(destinationDirectory));
        }

        SegmentCoreBackupFiles.EnsureRegularDirectoryTree(_rootDirectory);

        var stopwatch = Stopwatch.StartNew();
        var telemetryStarted = SegmentCoreDiagnostics.StartTimestamp();
        var staging = SegmentCoreBackupFiles.CreateStagingDirectory(destination, "backup");
        var compactionGateEntered = false;
        var checkpointGateEntered = false;
        CheckpointCapture? capture = null;
        var checkpointPublished = false;
        try
        {
            await EnterShutdownSafeGateAsync(_compactionGate, cancellationToken).ConfigureAwait(false);
            compactionGateEntered = true;
            await EnterShutdownSafeGateAsync(_checkpointGate, cancellationToken).ConfigureAwait(false);
            checkpointGateEntered = true;
            Volatile.Write(ref _checkpointing, 1);
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.ForceRotateStripesAsync(
                    firstStripe: 0,
                    count: _writer.StripeCount,
                    cancellationToken).ConfigureAwait(false);
                capture = await CaptureCheckpointAsync(
                    forceFull: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitExclusiveOperation();
            }

            _ = await PublishCheckpointAsync(
                capture,
                forceFull: true,
                cancellationToken).ConfigureAwait(false);
            checkpointPublished = true;
            var state = _latestCheckpoint
                ?? throw new InvalidOperationException("Backup checkpoint was not published.");
            if (!state.IsFull)
            {
                throw new InvalidOperationException("An online backup requires a self-contained full checkpoint.");
            }

            var files = new List<SegmentCoreBackupFile>();
            var revisionClock = Path.Combine(_rootDirectory, ".idb-revision-clock");
            if (File.Exists(revisionClock))
                files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                    _rootDirectory,
                    revisionClock,
                    staging,
                    ".idb-revision-clock",
                    SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, revisionClock),
                    _options.DurableFileSystem,
                    admission: null,
                    cancellationToken).ConfigureAwait(false));
            foreach (var relativePath in new[]
                     {
                         "storage-format.manifest",
                         "transaction-protocol.manifest",
                         "striped-log.manifest"
                     })
            {
                var source = Path.Combine(_rootDirectory, relativePath);
                files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                    _rootDirectory,
                    source,
                    staging,
                    relativePath,
                    SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, source),
                    _options.DurableFileSystem,
                    admission: null,
                    cancellationToken).ConfigureAwait(false));
            }

            var checkpointRelative = Path.Combine(
                "checkpoints",
                $"checkpoint-{state.Generation:D20}.idbidx");
            var checkpointPath = Path.Combine(_rootDirectory, checkpointRelative);
            files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                _rootDirectory,
                checkpointPath,
                staging,
                checkpointRelative,
                SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, checkpointPath),
                _options.DurableFileSystem,
                admission: null,
                cancellationToken).ConfigureAwait(false));
            var pageMapPath = Path.ChangeExtension(checkpointPath, ".idbmap");
            if (File.Exists(pageMapPath))
            {
                var pageMapRelative = Path.ChangeExtension(checkpointRelative, ".idbmap");
                files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                    _rootDirectory,
                    pageMapPath,
                    staging,
                    pageMapRelative,
                    SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, pageMapPath),
                    _options.DurableFileSystem,
                    admission: null,
                    cancellationToken).ConfigureAwait(false));
            }

            for (var stripe = 0; stripe < state.Positions.Count; stripe++)
            {
                var position = state.Positions[stripe];
                var segmentsDirectory = Path.Combine(
                    StripedObjectLogRecovery.StripeRoot(_rootDirectory, stripe),
                    "segments");
                SegmentCoreBackupFiles.EnsureRegularDirectory(_rootDirectory, segmentsDirectory);
                foreach (var source in Directory
                             .EnumerateFiles(segmentsDirectory, "segment-*.idbseg", SearchOption.TopDirectoryOnly)
                             .OrderBy(static path => path, StringComparer.Ordinal))
                {
                    if (!ObjectLogFormat.TryParseSegmentId(source, out var segmentId)
                        || segmentId > position.SegmentId)
                    {
                        continue;
                    }

                    var length = segmentId == position.SegmentId
                        ? position.NextOffset
                        : SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, source);
                    var relative = Path.GetRelativePath(_rootDirectory, source);
                    files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                        _rootDirectory,
                        source,
                        staging,
                        relative,
                        length,
                        _options.DurableFileSystem,
                        admission: null,
                        cancellationToken).ConfigureAwait(false));
                }
            }

            foreach (var file in state.Entries.Values.Where(a => a.Address.SegmentId < 0)
                         .Select(a => (a.Stripe, a.Address.SegmentId)).Distinct())
            {
                var source = SegmentCoreIndependentFiles.Resolve(_rootDirectory, file.Stripe, file.SegmentId);
                files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                    _rootDirectory,
                    source,
                    staging,
                    Path.GetRelativePath(_rootDirectory, source),
                    SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, source),
                    _options.DurableFileSystem,
                    admission: null,
                    cancellationToken).ConfigureAwait(false));
            }
            var floorPath = Path.Combine(_rootDirectory, SegmentCoreIndependentFiles.FloorFileName);
            if (File.Exists(floorPath))
                files.Add(await SegmentCoreBackupFiles.CopyPrefixAsync(
                    _rootDirectory,
                    floorPath,
                    staging,
                    SegmentCoreIndependentFiles.FloorFileName,
                    SegmentCoreBackupFiles.GetRegularFileLength(_rootDirectory, floorPath),
                    _options.DurableFileSystem,
                    admission: null,
                    cancellationToken).ConfigureAwait(false));

            var transactionRelative = Path.Combine(
                "transactions",
                _transactionWal is not null ? "mutation-wal.idbwal" : "commit-journal.idbtxn");
            var transactionDestination = SegmentCoreBackupFiles.ResolveRelativePath(staging, transactionRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(transactionDestination)!);
            if (_transactionWal is not null)
            {
                await _transactionWal.WriteCheckpointBaseAsync(
                    transactionDestination,
                    state.TransactionWatermark,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _transactionJournal!.WriteCheckpointPrefixAsync(
                    transactionDestination,
                    state.TransactionWatermark,
                    cancellationToken).ConfigureAwait(false);
            }

            files.Add(SegmentCoreBackupFiles.DescribeFile(staging, transactionRelative));
            var manifest = new SegmentCoreBackupManifest
            {
                StorageFormat = SegmentCoreStorageFormatManifest.ReadOrCreate(_rootDirectory,
                    SegmentCoreStorageFormat.For(_options.LogOptions), _options.DurableFileSystem),
                CheckpointGeneration = state.Generation,
                TransactionWatermark = state.TransactionWatermark,
                Entries = state.Entries.Count,
                StripeCount = state.Positions.Count,
                TransactionProtocol = _options.TransactionProtocol,
                CreatedUtc = DateTimeOffset.UtcNow,
                Positions = state.Positions.ToArray(),
                Files = files
            };

            if (backupOptions.VerifyAfterWrite)
            {
                await using var verificationStore = await OpenAsync(
                    staging,
                    _options,
                    cancellationToken).ConfigureAwait(false);
                var integrity = await verificationStore.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
                if (!integrity.IsValid || verificationStore.Count != manifest.Entries)
                {
                    throw new InvalidDataException(
                        "Online backup verification failed: " + string.Join("; ", integrity.Failures));
                }
            }

            await SegmentCoreBackupFiles.WriteManifestAsync(
                staging,
                manifest,
                _options.DurableFileSystem,
                cancellationToken).ConfigureAwait(false);
            _ = SegmentCoreBackupFiles.ReadAndValidateManifest(staging);
            SegmentCoreBackupFiles.PublishDirectory(staging, destination, _options.DurableFileSystem);
            stopwatch.Stop();
            var result = new SegmentCoreBackupResult(
                destination,
                state.Generation,
                state.TransactionWatermark,
                manifest.Entries,
                files.Count,
                files.Sum(static file => file.Length),
                stopwatch.Elapsed);
            SegmentCoreDiagnostics.RecordStorageBytes("backup", result.Bytes);
            SegmentCoreDiagnostics.RecordOperation(
                "backup",
                telemetryStarted,
                _options.TransactionProtocol,
                succeeded: true);
            return result;
        }
        catch
        {
            SegmentCoreDiagnostics.RecordOperation(
                "backup",
                telemetryStarted,
                _options.TransactionProtocol,
                succeeded: false);
            if (capture is not null && !checkpointPublished)
            {
                RestoreCheckpointChanges(capture.Changes);
            }

            SegmentCoreBackupFiles.TryDeleteStaging(staging);
            throw;
        }
        finally
        {
            if (checkpointGateEntered)
            {
                Volatile.Write(ref _checkpointing, 0);
                _checkpointGate.Release();
            }

            if (compactionGateEntered)
            {
                _compactionGate.Release();
            }
        }
    }

    public static ValueTask<SegmentCoreRestoreResult> RestoreBackupAsync(
        string backupDirectory,
        string destinationDirectory,
        SegmentCoreOptions? options = null,
        CancellationToken cancellationToken = default)
        => RestoreBackupAsync(
            backupDirectory,
            destinationDirectory,
            options,
            new SegmentCoreRestoreOptions(),
            cancellationToken);

    public static async ValueTask<SegmentCoreRestoreResult> RestoreBackupAsync(
        string backupDirectory,
        string destinationDirectory,
        SegmentCoreOptions? options,
        SegmentCoreRestoreOptions restoreOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(restoreOptions);
        restoreOptions.Validate();
        var admission = new SegmentCoreRestoreAdmission(restoreOptions);
        using var restoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        restoreCancellation.CancelAfter(restoreOptions.MaximumDuration);
        var restoreToken = restoreCancellation.Token;
        var backup = Path.GetFullPath(backupDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (IsPathWithin(destination, backup) || IsPathWithin(backup, destination))
        {
            throw new ArgumentException("Backup and restore directories must not contain one another.");
        }

        var manifest = SegmentCoreBackupFiles.ReadAndValidateManifest(backup, admission, restoreToken);
        EnsureRestoreFreeSpace(destination, manifest, restoreOptions);
        if (!SegmentCoreHashRouter.TryParseRouterId(
                manifest.StorageFormat.RouterId,
                out var backupHashRouting))
        {
            throw new SegmentCoreCompatibilityException(
                $"Backup uses unsupported placement algorithm '{manifest.StorageFormat.RouterId}'.");
        }

        options ??= new SegmentCoreOptions
        {
            TransactionProtocol = manifest.TransactionProtocol,
            LogOptions = new StripedObjectLogProbeOptions
            {
                StripeCount = manifest.StripeCount,
                HashAlgorithm = backupHashRouting.Algorithm,
                HashSeed = backupHashRouting.Seed,
                DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
            }
        };
        options.Validate();
        var expectedStorageFormat = SegmentCoreStorageFormat.For(options.LogOptions);
        if (options.TransactionProtocol != manifest.TransactionProtocol
            || options.LogOptions.StripeCount != manifest.StripeCount
            || !string.Equals(
                expectedStorageFormat.RouterId,
                manifest.StorageFormat.RouterId,
                StringComparison.Ordinal))
        {
            throw new SegmentCoreCompatibilityException(
                "Restore options do not match the backup transaction protocol, stripe count, or hash routing.");
        }

        var stopwatch = Stopwatch.StartNew();
        var telemetryStarted = SegmentCoreDiagnostics.StartTimestamp();
        var staging = SegmentCoreBackupFiles.CreateStagingDirectory(destination, "restore");
        try
        {
            foreach (var file in manifest.Files)
            {
                admission.ThrowIfExceeded(restoreToken);
                var copied = await SegmentCoreBackupFiles.CopyPrefixAsync(
                    backup,
                    SegmentCoreBackupFiles.ResolveRelativePath(backup, file.RelativePath),
                    staging,
                    file.RelativePath,
                    file.Length,
                    options.DurableFileSystem,
                    admission,
                    restoreToken).ConfigureAwait(false);
                if (!string.Equals(copied.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Restored file '{file.RelativePath}' changed while it was copied.");
                }
            }

            admission.ThrowIfExceeded(restoreToken);
            await using (var verificationStore = await OpenAsync(staging, options, restoreToken).ConfigureAwait(false))
            {
                var integrity = await verificationStore.VerifyIntegrityAsync(restoreToken).ConfigureAwait(false);
                if (!integrity.IsValid
                    || verificationStore.Count != manifest.Entries
                    || verificationStore.Statistics.CheckpointGeneration != manifest.CheckpointGeneration)
                {
                    throw new InvalidDataException(
                        "Restored SegmentCore store failed integrity or checkpoint validation: "
                        + string.Join("; ", integrity.Failures));
                }
            }

            admission.ThrowIfExceeded(restoreToken);
            SegmentCoreBackupFiles.PublishDirectory(staging, destination, options.DurableFileSystem);
            stopwatch.Stop();
            var result = new SegmentCoreRestoreResult(
                destination,
                manifest.CheckpointGeneration,
                manifest.Entries,
                manifest.Files.Count,
                manifest.Files.Sum(static file => file.Length),
                stopwatch.Elapsed);
            SegmentCoreDiagnostics.RecordStorageBytes("restore", result.Bytes);
            SegmentCoreDiagnostics.RecordOperation(
                "restore",
                telemetryStarted,
                options.TransactionProtocol,
                succeeded: true);
            return result;
        }
        catch
        {
            SegmentCoreDiagnostics.RecordOperation(
                "restore",
                telemetryStarted,
                options.TransactionProtocol,
                succeeded: false);
            SegmentCoreBackupFiles.TryDeleteStaging(staging);
            throw;
        }
    }

    private static void EnsureRestoreFreeSpace(
        string destination,
        SegmentCoreBackupManifest manifest,
        SegmentCoreRestoreOptions options)
    {
        if (!options.RequireFreeSpaceAdmission)
        {
            return;
        }

        long requiredBytes = 0;
        foreach (var file in manifest.Files)
        {
            requiredBytes = checked(requiredBytes + file.Length);
        }

        var root = Path.GetPathRoot(destination)
            ?? throw new IOException($"Restore destination '{destination}' has no filesystem root.");
        var availableBytes = new DriveInfo(root).AvailableFreeSpace;
        if (availableBytes < requiredBytes
            || availableBytes - requiredBytes < options.MinimumFreeSpaceReserveBytes)
        {
            throw new IOException(
                $"SegmentCore restore requires {requiredBytes:N0} bytes plus a {options.MinimumFreeSpaceReserveBytes:N0}-byte free-space reserve, but only {availableBytes:N0} bytes are available.");
        }
    }

    private async ValueTask<TimeSpan> PublishCompactionCheckpointAsync(
        CancellationToken cancellationToken)
    {
        CheckpointCapture? capture = null;
        var exclusivePause = Stopwatch.StartNew();
        try
        {
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                capture = await CaptureCheckpointAsync(
                    forceFull: true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitExclusiveOperation();
                exclusivePause.Stop();
            }

            _ = await PublishCheckpointAsync(
                capture,
                forceFull: true,
                cancellationToken).ConfigureAwait(false);
            return exclusivePause.Elapsed;
        }
        catch
        {
            if (capture is not null)
            {
                RestoreCheckpointChanges(capture.Changes);
            }

            throw;
        }
    }

    private async ValueTask<CheckpointCapture> CaptureCheckpointAsync(
        bool forceFull,
        CancellationToken cancellationToken)
    {
        await FlushPendingDerivedFramesAsync(cancellationToken).ConfigureAwait(false);

        var positions = _writer.GetWritePositions();
        var previousGeneration = Volatile.Read(ref _checkpointGeneration);
        var transactionWatermark = _transactionWal?.HighestDurableCommittedTransactionId
            ?? _transactionJournal!.HighestDurableCommittedTransactionId;
        var basis = _latestCheckpoint;
        Dictionary<string, StripedObjectLogRecordAddress>? entries = null;
        Dictionary<string, PrimaryIndexChange> changeSnapshot;
        int liveEntryCount;
        bool useFull;
        _indexVisibility.EnterWriteLock();
        try
        {
            liveEntryCount = _primaryIndex.Count;
            if (!forceFull
                && _checkpointChanges.Count == 0
                && basis is not null
                && basis.Generation == previousGeneration
                && basis.TransactionWatermark == transactionWatermark
                && PositionsEqual(basis.Positions, positions))
            {
                return new CheckpointCapture(
                    positions,
                    Entries: null,
                    new Dictionary<string, PrimaryIndexChange>(StringComparer.Ordinal),
                    previousGeneration,
                    transactionWatermark,
                    liveEntryCount,
                    UseFull: false,
                    IsNoOp: true,
                    basis);
            }

            useFull = PrimaryIndexCheckpoint.ShouldWriteFull(
                _rootDirectory,
                liveEntryCount,
                _checkpointChanges.Count,
                previousGeneration,
                _options.CheckpointGenerationsToKeep,
                forceFull,
                basis);
            if (useFull)
            {
                entries = new Dictionary<string, StripedObjectLogRecordAddress>(
                    _primaryIndex,
                    StringComparer.Ordinal);
            }

            // Transfer the old map to this capture while holding the writer lock. New
            // mutations use the replacement; failure restoration never mutates the capture.
            changeSnapshot = _checkpointChanges;
            _checkpointChanges = new Dictionary<string, PrimaryIndexChange>(StringComparer.Ordinal);
        }
        finally
        {
            _indexVisibility.ExitWriteLock();
        }

        return new CheckpointCapture(
            positions,
            entries,
            changeSnapshot,
            previousGeneration,
            transactionWatermark,
            liveEntryCount,
            useFull,
            IsNoOp: false,
            basis);
    }

    private async ValueTask<SegmentCoreCheckpointResult> PublishCheckpointAsync(
        CheckpointCapture capture,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (capture.IsNoOp)
        {
            var basis = capture.Basis
                ?? throw new InvalidOperationException("A no-op checkpoint has no published basis.");
            stopwatch.Stop();
            return new SegmentCoreCheckpointResult(
                basis.Generation,
                capture.LiveEntryCount,
                basis.FileBytes,
                stopwatch.Elapsed);
        }

        var state = await PrimaryIndexCheckpoint.WriteOptimizedAsync(
            _rootDirectory,
            capture.Positions,
            capture.Entries,
            capture.LiveEntryCount,
            capture.Changes,
            capture.PreviousGeneration,
            capture.TransactionWatermark,
            _options.CheckpointGenerationsToKeep,
            capture.UseFull || forceFull,
            capture.Basis,
            _options.PrimaryIndexOptions,
            _options.DurableFileSystem,
            cancellationToken).ConfigureAwait(false);

        if (_transactionWal is not null)
        {
            var recycleWatermark = PrimaryIndexCheckpoint.GetOldestRetainedTransactionWatermark(
                _rootDirectory,
                _options.LogOptions.StripeCount,
                state,
                _options.CheckpointGenerationsToKeep,
                _validatedCheckpointRoots);
            try
            {
                await _transactionWal.RecycleAsync(
                    recycleWatermark,
                    _options.WalRecycleMinimumReclaimBytes,
                    _options.WalMaximumBytesBeforeForcedRecycle,
                    CancellationToken.None).ConfigureAwait(false);
                await _transactionWal.PublishRecoveryBoundaryAsync(
                    state.Generation,
                    state.TransactionWatermark,
                    _options.CheckpointGenerationsToKeep,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _faulted, 1);
                throw;
            }
        }

        _latestCheckpoint = state;
        Volatile.Write(ref _checkpointGeneration, state.Generation);
        stopwatch.Stop();
        return new SegmentCoreCheckpointResult(
            state.Generation,
            capture.LiveEntryCount,
            state.FileBytes,
            stopwatch.Elapsed);
    }

    private static bool PositionsEqual(
        IReadOnlyList<ObjectLogWritePosition> left,
        IReadOnlyList<ObjectLogWritePosition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask FlushPendingDerivedFramesAsync(CancellationToken cancellationToken)
    {
        if (_transactionWal is null)
        {
            return;
        }

        var dirtyMask = unchecked((ulong)Volatile.Read(ref _dirtyStripeMask));
        if (dirtyMask == 0)
        {
            return;
        }

        if (_options.LogOptions.DurabilityStrategy == ObjectLogDurabilityStrategy.ExplicitFlush)
        {
            await _writer.FlushStripesAsync(dirtyMask, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.And(ref _dirtyStripeMask, unchecked((long)~dirtyMask));
    }

    private void RestoreCheckpointChanges(IReadOnlyDictionary<string, PrimaryIndexChange> changes)
    {
        _indexVisibility.EnterWriteLock();
        try
        {
            foreach (var pair in changes)
            {
                _checkpointChanges.TryAdd(pair.Key, pair.Value);
            }
        }
        finally
        {
            _indexVisibility.ExitWriteLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        _retention?.Dispose();

        while (Volatile.Read(ref _gateAdmissions) != 0
               || Volatile.Read(ref _activeMutations) != 0
               || Volatile.Read(ref _activeReads) != 0
               || Volatile.Read(ref _exclusiveOperation) != 0)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }

        await _compactionGate.WaitAsync().ConfigureAwait(false);
        await _checkpointGate.WaitAsync().ConfigureAwait(false);
        await _exclusiveGate.WaitAsync().ConfigureAwait(false);

        List<Exception>? failures = null;
        if (_transactionWal is not null && Volatile.Read(ref _faulted) == 0)
        {
            try
            {
                await _writer.FlushAllAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (_transactionJournal is not null)
        {
            try
            {
                await _transactionJournal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (_transactionWal is not null)
        {
            try
            {
                await _transactionWal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            _reader.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _primaryIndex.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            _indexVisibility.Dispose();
            _exclusiveGate.Dispose();
            _transactionPreparationGate.Dispose();
            _checkpointGate.Dispose();
            _compactionGate.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await _ownerLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new AggregateException("SegmentCore failed to dispose one or more durable components.", failures);
        }
    }

    internal ValueTask<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        _indexVisibility.EnterReadLock();
        try
        {
            return ValueTask.FromResult(_primaryIndex.ContainsKey(key));
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }
    }

    internal ValueTask CommitTransactionAsync(
        Guid publicTransactionId,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        CancellationToken cancellationToken)
        => !DatabaseTelemetryOperation.IsEnabled(_options.Telemetry) ? CommitTransactionAsyncUnobserved(publicTransactionId, mutations, cancellationToken)
            : CommitTransactionAsyncObserved(publicTransactionId, mutations, cancellationToken);

    private ValueTask CommitTransactionAsyncObserved(
        Guid publicTransactionId,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        CancellationToken cancellationToken)
        => DatabaseTelemetryOperation.ObserveAsync(_options.Telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Transaction,
                () => CommitTransactionAsyncUnobserved(publicTransactionId, mutations, cancellationToken), log: true, bytes: 0);

    private async ValueTask CommitTransactionAsyncUnobserved(
        Guid publicTransactionId,
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        CancellationToken cancellationToken)
    {
        _ = publicTransactionId;
        ArgumentNullException.ThrowIfNull(mutations);
        ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count == 0)
        {
            return;
        }

        if (_transactionWal is not null)
        {
            await CommitWalTransactionAsync(mutations, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
        var storageStarted = false;
        try
        {
            ThrowIfUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            var transactionId = _transactionWal?.ReserveTransactionId()
                ?? _transactionJournal!.ReserveTransactionId();
            storageStarted = true;
            var addresses = await _writer.AppendTransactionBatchAsync(
                mutations, transactionId, requiresDurabilityFlush: _transactionWal is null,
                cancellationToken).ConfigureAwait(false);
            if (_transactionWal is null)
            {
                var identities = new TransactionMutationIdentity[mutations.Count];
                for (var index = 0; index < mutations.Count; index++)
                {
                    identities[index] = new TransactionMutationIdentity(
                        addresses[index].Stripe,
                        mutations[index].Kind,
                        addresses[index].Address,
                        ObjectLogFormat.ComputeDataChecksum(mutations[index].Key, mutations[index].Payload));
                }

                await _transactionJournal!.AppendCommitAsync(
                    transactionId,
                    identities,
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _transactionWal.AppendCommitAsync(
                    transactionId,
                    mutations,
                    addresses,
                    CancellationToken.None).ConfigureAwait(false);
                MarkDirtyStripes(addresses);
            }

            _indexVisibility.EnterWriteLock();
            try
            {
                for (var index = 0; index < mutations.Count; index++)
                {
                    var mutation = mutations[index];
                    if (mutation.Kind == ObjectLogRecordKind.Put)
                    {
                        PublishIfNewer(_primaryIndex, mutation.Key, addresses[index]);
                        _checkpointChanges[mutation.Key] = new PrimaryIndexChange(
                            false,
                            _primaryIndex[mutation.Key]);
                    }
                    else
                    {
                        if (RemoveIfNotNewer(_primaryIndex, mutation.Key, addresses[index]))
                        {
                            _checkpointChanges[mutation.Key] = new PrimaryIndexChange(true, default);
                        _retention?.Deleted(mutation.Key);
                        }
                    }
                }
            }
            finally
            {
                _indexVisibility.ExitWriteLock();
            }
        }
        catch
        {
            if (storageStarted)
            {
                Volatile.Write(ref _faulted, 1);
            }

            throw;
        }
        finally
        {
            ExitExclusiveOperation();
        }
    }

    private async ValueTask CommitWalTransactionAsync(
        IReadOnlyList<SegmentCorePendingMutation> mutations,
        CancellationToken cancellationToken)
    {
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        var storageStarted = false;
        Task? durableCommit = null;
        StripedObjectLogRecordAddress[]? addresses = null;
        try
        {
            await _transactionPreparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                var transactionId = _transactionWal!.ReserveTransactionId();
                storageStarted = true;
                addresses = await _writer.AppendTransactionBatchAsync(
                    mutations, transactionId, requiresDurabilityFlush: false,
                    cancellationToken).ConfigureAwait(false);
                durableCommit = _transactionWal.AppendCommitAsync(
                    transactionId,
                    mutations,
                    addresses,
                    CancellationToken.None).AsTask();
            }
            finally
            {
                _transactionPreparationGate.Release();
            }

            await durableCommit.ConfigureAwait(false);
            MarkDirtyStripes(addresses);
            _indexVisibility.EnterWriteLock();
            try
            {
                for (var index = 0; index < mutations.Count; index++)
                {
                    var mutation = mutations[index];
                    if (mutation.Kind == ObjectLogRecordKind.Put)
                    {
                        PublishIfNewer(_primaryIndex, mutation.Key, addresses[index]);
                        _checkpointChanges[mutation.Key] = new PrimaryIndexChange(
                            false,
                            _primaryIndex[mutation.Key]);
                    }
                    else if (RemoveIfNotNewer(_primaryIndex, mutation.Key, addresses[index]))
                    {
                        _checkpointChanges[mutation.Key] = new PrimaryIndexChange(true, default);
                        _retention?.Deleted(mutation.Key);
                    }
                }
            }
            finally
            {
                _indexVisibility.ExitWriteLock();
            }
        }
        catch
        {
            if (storageStarted)
            {
                Volatile.Write(ref _faulted, 1);
            }

            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeMutations);
        }
    }

    internal void OnTransactionStateChanged(
        Guid transactionId,
        SegmentCoreTransactionState previousState,
        SegmentCoreTransactionState state,
        Exception? failure)
    {
        if (state == SegmentCoreTransactionState.Committed)
        {
            Interlocked.Increment(ref _committedTransactions);
        }
        else if (state == SegmentCoreTransactionState.RolledBack)
        {
            Interlocked.Increment(ref _rolledBackTransactions);
        }
        else if (state == SegmentCoreTransactionState.Faulted)
        {
            Interlocked.Increment(ref _faultedTransactions);
        }

        var handlers = TransactionStateChanged;
        if (handlers is null)
        {
            return;
        }

        var arguments = new SegmentCoreTransactionStateChangedEventArgs(
            transactionId,
            previousState,
            state,
            failure);
        foreach (EventHandler<SegmentCoreTransactionStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, arguments);
            }
            catch
            {
                // Observers cannot alter a durable transaction outcome.
            }
        }
    }

    private async ValueTask EnterMutationAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        while (true)
        {
            ThrowIfUnavailable();
            while (Volatile.Read(ref _exclusiveOperation) != 0)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _activeMutations);
            if (Volatile.Read(ref _exclusiveOperation) == 0
                && Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _faulted) == 0)
            {
                return;
            }

            Interlocked.Decrement(ref _activeMutations);
        }
    }

    private async ValueTask EnterShutdownSafeGateAsync(
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _gateAdmissions);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfUnavailable();
            }
            catch
            {
                gate.Release();
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _gateAdmissions);
        }
    }

    private async ValueTask EnterReadOperationAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        while (true)
        {
            while (Volatile.Read(ref _readerRetirement) != 0)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _activeReads);
            if (Volatile.Read(ref _readerRetirement) == 0
                && Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _faulted) == 0)
            {
                return;
            }

            Interlocked.Decrement(ref _activeReads);
        }
    }

    private async ValueTask EnterExclusiveOperationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _gateAdmissions);
        var gateEntered = false;
        var ownerRegistered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            await _exclusiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            Interlocked.Increment(ref _exclusiveOperation);
            ownerRegistered = true;
            Interlocked.Decrement(ref _gateAdmissions);
            ThrowIfUnavailable();
            while (Volatile.Read(ref _activeMutations) != 0)
            {
                ThrowIfUnavailable();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (gateEntered)
            {
                _exclusiveGate.Release();
            }

            if (ownerRegistered)
            {
                Interlocked.Decrement(ref _exclusiveOperation);
            }
            else
            {
                Interlocked.Decrement(ref _gateAdmissions);
            }

            throw;
        }
    }

    private void ExitExclusiveOperation()
    {
        _exclusiveGate.Release();
        Interlocked.Decrement(ref _exclusiveOperation);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new InvalidOperationException(
                "SegmentCore has an uncertain transaction outcome and must be reopened before further use.");
        }
    }

    private void DeleteSupersededSegments(IReadOnlyList<long> compactedSegmentFloor)
    {
        for (var stripe = 0; stripe < compactedSegmentFloor.Count; stripe++)
        {
            var segmentsDirectory = Path.Combine(
                StripedObjectLogRecovery.StripeRoot(_rootDirectory, stripe),
                "segments");
            foreach (var path in Directory.EnumerateFiles(
                         segmentsDirectory,
                         "segment-*.idbseg",
                         SearchOption.TopDirectoryOnly))
            {
                if (ObjectLogFormat.TryParseSegmentId(path, out var segmentId)
                    && segmentId < compactedSegmentFloor[stripe])
                {
                    File.Delete(path);
                }
            }

            _options.DurableFileSystem.FlushDirectory(segmentsDirectory);
        }
    }

    private static long GetStorageBytes(string rootDirectory) =>
        Directory
            .EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);

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

    private int GetPendingCheckpointChangeCount()
    {
        _indexVisibility.EnterReadLock();
        try
        {
            return _checkpointChanges.Count;
        }
        finally
        {
            _indexVisibility.ExitReadLock();
        }
    }

    private void EnsureCompactionCapacity(
        SegmentCoreCompactionOptions options,
        long estimatedLiveFrameBytes)
    {
        if (!options.RequireFreeSpaceAdmission)
        {
            return;
        }

        var root = Path.GetPathRoot(_rootDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new IOException("Cannot determine the storage volume for compaction admission.");
        }

        var available = new DriveInfo(root).AvailableFreeSpace;
        var required = checked(estimatedLiveFrameBytes + options.MinimumFreeSpaceReserveBytes);
        if (available < required)
        {
            throw new IOException(
                $"SegmentCore compaction requires approximately {required:N0} free bytes "
                + $"but volume '{root}' reports {available:N0} bytes available.");
        }
    }

    private SegmentCoreTransaction RequireOwnedTransaction(IStorageTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not SegmentCoreTransaction segmentTransaction
            || !ReferenceEquals(segmentTransaction.Owner, this))
        {
            throw new ArgumentException(
                "The transaction handle was not created by this SegmentCore store.",
                nameof(transaction));
        }

        return segmentTransaction;
    }

    private static void ApplyReplayRecord(
        PrimaryIndexMap index,
        int stripe,
        ObjectLogReplayRecord record)
    {
        var locator = new StripedObjectLogRecordAddress(stripe, record.Address);
        if (record.Kind == ObjectLogRecordKind.Put)
        {
            PublishIfNewer(index, record.Key, locator);
        }
        else
        {
            RemoveIfNotNewer(index, record.Key, locator);
        }
    }

    private static void PublishIfNewer(
        PrimaryIndexMap index,
        string key,
        StripedObjectLogRecordAddress candidate)
    {
        while (true)
        {
            if (!index.TryGetValue(key, out var current))
            {
                if (index.TryAdd(key, candidate))
                {
                    return;
                }

                continue;
            }

            RequireSameStripe(key, current, candidate);
            if (current.Address.Lsn >= candidate.Address.Lsn)
            {
                return;
            }

            if (index.TryUpdate(key, candidate, current))
            {
                return;
            }
        }
    }

    private static bool RemoveIfNotNewer(
        PrimaryIndexMap index,
        string key,
        StripedObjectLogRecordAddress tombstone)
    {
        while (index.TryGetValue(key, out var current))
        {
            RequireSameStripe(key, current, tombstone);
            if (current.Address.Lsn > tombstone.Address.Lsn)
            {
                return false;
            }

            if (index.Remove(key, current))
            {
                return true;
            }
        }

        return false;
    }

    private static void RequireSameStripe(
        string key,
        StripedObjectLogRecordAddress current,
        StripedObjectLogRecordAddress candidate)
    {
        if (current.Stripe != candidate.Stripe)
        {
            throw new ObjectLogCorruptionException(
                $"Key '{key}' was assigned to physical stripes {current.Stripe} and {candidate.Stripe}.");
        }
    }

    private void MarkDirtyStripes(IEnumerable<StripedObjectLogRecordAddress> addresses)
    {
        ulong mask = 0;
        foreach (var address in addresses)
        {
            mask |= 1UL << address.Stripe;
        }

        if (mask != 0)
        {
            Interlocked.Or(ref _dirtyStripeMask, unchecked((long)mask));
        }
    }
}
