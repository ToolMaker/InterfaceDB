using System.Diagnostics;
using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore;

public readonly record struct AtlasPartitionMaintenanceStatistics(long Attempts, long PublishedSplits,
    long DeferredSplits, long Failures, long BytesCopied, string? LastReason, string? LastError);

public sealed partial class AtlasCoreStore
{
    private readonly int[] _partitionMutationCounts;
    private readonly long[] _nextPartitionAttempt;
    private readonly object _partitionWorkerSync = new();
    private readonly HashSet<int> _pendingPartitions = [];
    private Task _partitionWorker = Task.CompletedTask;
    private bool _partitionWorkerRunning;
    private Exception? _partitionError;
    private string? _lastPartitionReason;
    private long _partitionAttempts, _partitionPublished, _partitionDeferred, _partitionFailures, _partitionBytesCopied;

    public IReadOnlyList<AtlasPartitionInfo> Partitions => _placement.Capture().Entries
        .Select(e => new AtlasPartitionInfo(e.Id, e.ParentId, e.PrefixHex, e.SplitDepth)).ToArray();
    public AtlasPartitionMaintenanceStatistics PartitionMaintenanceStatistics => new(
        Interlocked.Read(ref _partitionAttempts), Interlocked.Read(ref _partitionPublished),
        Interlocked.Read(ref _partitionDeferred), Interlocked.Read(ref _partitionFailures),
        Interlocked.Read(ref _partitionBytesCopied), Volatile.Read(ref _lastPartitionReason), Volatile.Read(ref _partitionError)?.Message);

    private void ObservePartition(AtlasGroupStore group, int id, bool newlySplitChild = false)
    {
        var policy = _options.Partitioning;
        if (!policy.EnableAutomaticSplitting || Volatile.Read(ref _disposed) != 0
            || _placement.Capture().Entries[id].SplitDepth != 0) return;
        if ((!newlySplitChild && Interlocked.Increment(ref _partitionMutationCounts[id]) % policy.CheckEveryMutations != 0)
            || (group.LiveCount <= policy.SplitAboveLiveRecords && group.LiveBytes <= policy.SplitAboveLiveBytes)
            || Stopwatch.GetTimestamp() < Volatile.Read(ref _nextPartitionAttempt[id])) return;
        lock (_partitionWorkerSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _pendingPartitions.Add(id);
            StartPartitionWorkerLocked();
        }
    }

    private void StartPartitionWorkerLocked()
    {
        if (_partitionWorkerRunning || _pendingPartitions.Count == 0 || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _faulted) != 0) return;
        _partitionWorkerRunning = true;
        _partitionWorker = Task.Run(RunPartitionWorkerAsync);
    }

    private async Task RunPartitionWorkerAsync()
    {
        while (true)
        {
            int id;
            lock (_partitionWorkerSync)
            {
                if (_pendingPartitions.Count == 0 || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0)
                { _pendingPartitions.Clear(); _partitionWorkerRunning = false; return; }
                id = _pendingPartitions.First(); _pendingPartitions.Remove(id);
            }
            try
            {
                await SplitPartitionAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
            catch (Exception exception)
            {
                Volatile.Write(ref _partitionError, exception);
                Interlocked.Increment(ref _partitionFailures);
            }
            finally
            {
                Volatile.Write(ref _nextPartitionAttempt[id], Stopwatch.GetTimestamp()
                    + (long)(_options.Partitioning.RetryDelay.TotalSeconds * Stopwatch.Frequency));
            }
        }
    }

    /// <summary>Waits for automatic split requests already observed. Call after foreground writers have completed.
    /// Background failures are observable here as well as in PartitionMaintenanceStatistics.</summary>
    public async ValueTask FlushPartitionMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task worker;
            lock (_partitionWorkerSync)
            {
                StartPartitionWorkerLocked();
                worker = _partitionWorker;
            }
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_partitionWorkerSync)
            {
                if (_pendingPartitions.Count == 0 || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0) break;
            }
        }
        if (Volatile.Read(ref _partitionError) is { } error)
            throw new InvalidOperationException("Atlas automatic partition maintenance failed; inspect its diagnostics.", error);
    }

    /// <summary>Evaluates and splits the leaf owning a key using the configured thresholds. No performance calibration is performed.</summary>
    public ValueTask<AtlasPartitionSplitResult> SplitBucketAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfUnavailable();
        var route = _placement.Route(key);
        if (_options.Partitioning.IsFixedLayout)
            return ValueTask.FromResult(new AtlasPartitionSplitResult(false, "fixed-layout", route.PrimaryGroup, 0, 0, 0, TimeSpan.Zero));
        return route.PrimaryGroup < 0
            ? ValueTask.FromResult(new AtlasPartitionSplitResult(false, "missing-bucket", -1, 0, 0, 0, TimeSpan.Zero))
            : SplitPartitionAsync(route.PrimaryGroup, cancellationToken);
    }

    private async ValueTask<AtlasPartitionSplitResult> SplitPartitionAsync(int parentId, CancellationToken callerToken)
    {
        EnterOperation();
        try
        {
            await _compactionGate.WaitAsync(callerToken).ConfigureAwait(false);
            try
            {
                await EnterExclusiveMutationAsync(callerToken).ConfigureAwait(false);
                try { return await SplitPartitionCoreAsync(parentId, callerToken).ConfigureAwait(false); }
                finally { ExitExclusiveMutation(); }
            }
            finally { _compactionGate.Release(); }
        }
        finally { ExitOperation(); }
    }

    private async ValueTask<AtlasPartitionSplitResult> SplitPartitionCoreAsync(int parentId, CancellationToken callerToken)
    {
        var policy = _options.Partitioning;
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(policy.MaximumSplitDuration);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline.Token);
        var token = bounded.Token;
        var candidates = new List<(int Id, string Path, AtlasGroupStore Store)>();
        var writes = new List<Task>(Math.Min(_options.MaximumBatchRecords, 256));
        var candidatePaths = new List<string>();
        var attemptedPublication = false;
        var published = false;
        long copied = 0, bytesCopied = 0;
        Interlocked.Increment(ref _partitionAttempts);
        AtlasPartitionSplitResult Deferred(string reason)
        {
            Volatile.Write(ref _lastPartitionReason, reason);
            Interlocked.Increment(ref _partitionDeferred);
            return new(false, reason, parentId, 0, copied, bytesCopied, watch.Elapsed);
        }
        try
        {
            var before = _placement.Capture();
            var parent = before.Entries[parentId];
            if (parent.SplitDepth != 0) return Deferred("already-split");
            var source = await GetGroupAsync(parentId).ConfigureAwait(false);
            if (source.LiveCount <= policy.SplitAboveLiveRecords && source.LiveBytes <= policy.SplitAboveLiveBytes)
                return Deferred("below-threshold");
            if (source.Count > policy.MaximumSplitRecords) return Deferred("metadata-budget");
            var records = source.SnapshotIndex().Where(r => r.Value.Kind == AtlasGroupRecordKind.Put).ToArray();
            var totalBytes = records.Sum(r => (long)r.Value.FrameLength);
            if (totalBytes > policy.MaximumSplitBytes) return Deferred("copy-byte-budget");
            var prefixLength = parent.PrefixHex.Length / 2;
            if (prefixLength >= policy.MaximumPrefixBytes) return Deferred("maximum-prefix-depth");
            var keyed = records.Select(r => (Record: r, Bytes: _placement.PrefixBytes(r.Key)))
                .Where(r => r.Bytes.Length > prefixLength).ToArray();
            if (keyed.Length < 2) return Deferred("no-useful-key-split");
            var common = keyed[0].Bytes.Length;
            foreach (var item in keyed) common = Math.Min(common, keyed[0].Bytes.AsSpan(0, common).CommonPrefixLength(item.Bytes));
            var depth = common + 1;
            if (depth > policy.MaximumPrefixBytes) return Deferred("maximum-prefix-depth");
            var children = keyed.GroupBy(r => Convert.ToHexString(r.Bytes.AsSpan(0, Math.Min(r.Bytes.Length, depth))))
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
            if (children.Count(g => g.Count() >= policy.MinimumChildRecords) < 2) return Deferred("insufficient-child-population");
            if (children.Length > policy.MaximumPartitions - before.Entries.Length) return Deferred("partition-limit");
            if (new DriveInfo(Path.GetPathRoot(_rootDirectory)!).AvailableFreeSpace < checked(totalBytes + policy.MinimumFreeSpaceReserveBytes))
                return Deferred("working-space");
            var next = _placement.PrepareSplit(parentId, depth, children.Select(g => g.Key).ToArray());
            var nextId = before.Entries.Length;
            for (var i = 0; i < children.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var id = nextId + i;
                var path = Path.Combine(_groupSets.CurrentRoot, _placement.GroupDirectoryName(id));
                if (Directory.Exists(path)) throw new InvalidDataException("Unpublished split group exists; reopen before retrying.");
                candidatePaths.Add(path);
                var child = await AtlasGroupStore.OpenAsync(path, _options, _transactionLog, _options.DurableFileSystem,
                    token, id, recoverMutationWal: false, readHandleCache: _readHandleCache).ConfigureAwait(false);
                candidates.Add((id, path, child));
                writes.Clear();
                foreach (var item in children[i])
                {
                    token.ThrowIfCancellationRequested();
                    var record = item.Record;
                    var payload = (await source.ReadAtAsync(record.Key, record.Value, token).ConfigureAwait(false)).Payload
                        ?? throw new InvalidDataException("Split source disappeared.");
                    writes.Add(child.PutAsync(record.Key, payload, next.Generation, token).AsTask());
                    copied++; bytesCopied += record.Value.FrameLength;
                    if (writes.Count >= Math.Min(_options.MaximumBatchRecords, 256))
                    { await Task.WhenAll(writes).ConfigureAwait(false); writes.Clear(); }
                }
                await Task.WhenAll(writes).ConfigureAwait(false);
                await child.CheckpointAsync(_transactionLog.LastCommittedSequence, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            _options.SplitTestHook?.Invoke("children-durable");
            _options.DurableFileSystem.FlushDirectory(_groupSets.CurrentRoot);
            foreach (var child in candidates) Volatile.Write(ref _groups[child.Id], Task.FromResult(child.Store));
            // Cancellation is no longer accepted once the ownership commit starts.
            attemptedPublication = true;
            await _placement.PublishSplitAsync(next).ConfigureAwait(false);
            published = true;
            Interlocked.Increment(ref _routeCacheEpoch);
            Volatile.Write(ref _resolvedSnapshot, null);
            _options.SplitTestHook?.Invoke("ownership-published");
            Interlocked.Increment(ref _partitionPublished);
            Interlocked.Add(ref _partitionBytesCopied, bytesCopied);
            Volatile.Write(ref _lastPartitionReason, "split");
            // Parents are not deleted here: in-flight reads and retained edit bases may still
            // address them. Existing retention-aware compaction reclaims obsolete payloads.
            foreach (var child in candidates) ObservePartition(child.Store, child.Id, newlySplitChild: true);
            return new(true, "split", parentId, children.Length, copied, bytesCopied, watch.Elapsed);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerToken.IsCancellationRequested && !attemptedPublication)
        { return Deferred("time-budget"); }
        catch
        {
            if (attemptedPublication) Volatile.Write(ref _faulted, 1);
            throw;
        }
        finally
        {
            if (!published && !attemptedPublication)
            {
                // Commands already accepted by a child can finish after cancellation. Observe
                // them before closing/deleting the unpublished group, without masking the cause.
                try { await Task.WhenAll(writes).ConfigureAwait(false); } catch { }
                foreach (var child in candidates) await child.Store.DisposeAsync().ConfigureAwait(false);
                foreach (var path in candidatePaths)
                    if (Directory.Exists(path)) HybridPlacementDirectory.DeleteUnpublishedGroup(_groupSets.CurrentRoot, path, _options.DurableFileSystem);
            }
        }
    }
}
