using System.Collections.Concurrent;
using System.Diagnostics;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

public sealed partial class SegmentCoreStore
{
    private long _nextIndependentId;
    private readonly record struct CompactionFile(int Stripe, long Id)
    { internal string Key => $"{Stripe}:{Id}"; }
    private sealed record FilePlan(CompactionFile File, CompactionCandidate Candidate,
        KeyValuePair<string, StripedObjectLogRecordAddress>[] Records);
    private readonly record struct Relocation(string Key, StripedObjectLogRecordAddress Before,
        StripedObjectLogRecordAddress After);

    /// <summary>Bounded metadata-only estimate, including active files which execution can seal.
    /// No records are decoded or files rotated. The estimate may become stale as writers continue;
    /// actual execution revalidates candidates, free space and physical identities.</summary>
    public async ValueTask<CompactionEstimate> GetCompactionEstimateAsync(
        SegmentCoreCompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        await EnterShutdownSafeGateAsync(_compactionGate, cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
            IReadOnlyList<ObjectLogWritePosition> positions;
            await EnterExclusiveOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _indexVisibility.EnterReadLock();
                try
                {
                    if (_primaryIndex.Count > options.MaximumMetadataEntries)
                        throw new InvalidOperationException("Compaction metadata entry budget exceeded.");
                    snapshot = _primaryIndex.ToArray(); positions = _writer.GetWritePositions();
                }
                finally { _indexVisibility.ExitReadLock(); }
            }
            finally { ExitExclusiveOperation(); }
            var plans = BuildIndependentCatalog(snapshot, positions, options, cancellationToken, includeActive: true);
            var candidates = plans.Select(p => p.Candidate).ToArray();
            var selected = options.Mode == SegmentCoreCompactionMode.IndependentOutput
                ? CompactionCandidateSelector.Select(candidates, options.Selection) : candidates;
            var required = checked(selected.Sum(c => c.RequiredBytes)
                + (_retention?.Statistics.RetainedBytes ?? 0) + selected.Count * (long)ObjectLogFormat.SegmentHeaderSize);
            var available = !options.RequireFreeSpaceAdmission
                || new DriveInfo(Path.GetPathRoot(_rootDirectory)!).AvailableFreeSpace
                    >= checked(required + options.MinimumFreeSpaceReserveBytes);
            return new(candidates.Sum(c => c.PhysicalBytes), candidates.Sum(c => c.ReclaimableBytes),
                ForegroundOutstanding: _writer.Statistics.CurrentOutstandingWrites, HasWorkingSpace: available);
        }
        finally { _compactionGate.Release(); }
    }

    private async ValueTask<SegmentCoreCompactionResult> CompactIndependentAsync(
        SegmentCoreCompactionOptions options, CancellationToken token)
    {
        await EnterShutdownSafeGateAsync(_compactionGate, token).ConfigureAwait(false);
        try { await EnterShutdownSafeGateAsync(_checkpointGate, token).ConfigureAwait(false); }
        catch { _compactionGate.Release(); throw; }
        var watch = Stopwatch.StartNew();
        Volatile.Write(ref _compacting, 1);
        var longestPause = TimeSpan.Zero;
        var slices = 0;
        try
        {
            var bytesBefore = MeasureConcurrentStorageBytes(_rootDirectory);
            // This durable opt-in marker makes old runtimes reject the new address namespace before
            // inspecting checkpoints. It does not change ordinary new-store format defaults.
            SegmentCoreStorageFormatManifest.EnableIndependentOutput(_rootDirectory,
                SegmentCoreStorageFormat.For(_options.LogOptions), _options.DurableFileSystem);
            for (var stripe = 0; stripe < _writer.StripeCount; stripe += options.MaximumStripesPerPreparationSlice)
            {
                await EnterExclusiveOperationAsync(token).ConfigureAwait(false);
                var pause = Stopwatch.StartNew();
                try
                {
                    await _writer.ForceRotateStripesAsync(stripe,
                        Math.Min(options.MaximumStripesPerPreparationSlice, _writer.StripeCount - stripe), token).ConfigureAwait(false);
                }
                finally { ExitExclusiveOperation(); }
                longestPause = Max(longestPause, pause.Elapsed); slices++;
            }
            KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot;
            IReadOnlyList<ObjectLogWritePosition> positions;
            await EnterExclusiveOperationAsync(token).ConfigureAwait(false);
            var capturePause = Stopwatch.StartNew();
            try
            {
                _indexVisibility.EnterReadLock();
                try
                {
                    if (_primaryIndex.Count > options.MaximumMetadataEntries)
                        throw new InvalidOperationException("Compaction metadata entry budget exceeded.");
                    snapshot = _primaryIndex.ToArray();
                    positions = _writer.GetWritePositions();
                }
                finally { _indexVisibility.ExitReadLock(); }
            }
            finally { ExitExclusiveOperation(); }
            longestPause = Max(longestPause, capturePause.Elapsed); slices++;
            var preparationPause = longestPause;
            var catalog = BuildIndependentCatalog(snapshot, positions, options, token);
            var selected = CompactionCandidateSelector.Select(catalog.Select(p => p.Candidate), options.Selection);
            if (selected.Count == 0)
                return new(Count, 0, bytesBefore, MeasureConcurrentStorageBytes(_rootDirectory), 0, watch.Elapsed)
                { Slices = slices, PreparationPause = preparationPause, LongestExclusiveSlice = longestPause,
                    IsPartial = catalog.Length != 0 };

            var selectedKeys = selected.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var plans = catalog.Where(p => selectedKeys.Contains(p.Candidate.Id)).ToArray();
            var selectedFiles = plans.Select(p => p.File).ToHashSet();
            var needed = checked(selected.Sum(c => c.RequiredBytes) + (_retention?.Statistics.RetainedBytes ?? 0)
                + selected.Count * (long)ObjectLogFormat.SegmentHeaderSize);
            EnsureCompactionCapacity(options, needed);
            // An interrupted first-ever compaction must still have a normal-log recovery base.
            // Once independent files exist, a total loss of all checkpoints is deliberately fail-closed.
            if (_latestCheckpoint is null)
            {
                var basePause = await PublishCompactionCheckpointAsync(token).ConfigureAwait(false);
                longestPause = Max(longestPause, basePause); slices++;
            }
            var relocations = new ConcurrentBag<Relocation[]>();
            long copiedBytes = 0, frames = 0;
            await Parallel.ForEachAsync(plans, new ParallelOptions
            { MaxDegreeOfParallelism = options.MaximumCopyConcurrency, CancellationToken = token }, async (plan, cancellation) =>
            {
                var output = await CopyIndependentFileAsync(plan, options, cancellation).ConfigureAwait(false);
                relocations.Add(output);
                Interlocked.Add(ref frames, output.Length);
                Interlocked.Add(ref copiedBytes, output.Sum(r => (long)r.After.Address.FrameLength));
            }).ConfigureAwait(false);

            // Every output file is durable before any locator becomes visible. Equality is physical,
            // not timestamp-only: metadata acknowledgements can share the logical revision timestamp.
            foreach (var file in relocations)
            {
                for (var first = 0; first < file.Length; first += options.MaximumRecordsPerSlice)
                {
                    await EnterExclusiveOperationAsync(token).ConfigureAwait(false);
                    var pause = Stopwatch.StartNew();
                    try
                    {
                        _indexVisibility.EnterWriteLock();
                        try
                        {
                            var end = Math.Min(file.Length, first + options.MaximumRecordsPerSlice);
                            for (var i = first; i < end; i++)
                            {
                                var item = file[i];
                                if (_primaryIndex.TryUpdate(item.Key, item.After, item.Before))
                                    _checkpointChanges[item.Key] = new PrimaryIndexChange(false, item.After);
                            }
                        }
                        finally { _indexVisibility.ExitWriteLock(); }
                    }
                    finally { ExitExclusiveOperation(); }
                    longestPause = Max(longestPause, pause.Elapsed); slices++;
                    if (options.DelayBetweenSlices > TimeSpan.Zero)
                        await Task.Delay(options.DelayBetweenSlices, token).ConfigureAwait(false);
                }
            }

            // After conditional publication, no new editor can select a retiring current address.
            // Preserve already admitted baselines outside the mutation gate. Source files stay alive
            // until all physical readers are drained in the final retirement phase.
            long archivedBytes = 0;
            if (_retention is not null)
                archivedBytes = await _retention.RelocateAsync(a => selectedFiles.Contains(new(a.Stripe, a.Address.SegmentId)),
                    options.MaintenanceBudget, token).ConfigureAwait(false);

            var firstCheckpointPause = await PublishCompactionCheckpointAsync(token).ConfigureAwait(false);
            var safeFloor = Volatile.Read(ref _checkpointGeneration);
            var secondCheckpointPause = await PublishCompactionCheckpointAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The floor prevents fallback to a checkpoint referencing retired files, including a
            // silent full-log rebuild that could ignore independent output after checkpoint loss.
            SegmentCoreIndependentFiles.PublishRecoveryFloor(_rootDirectory, safeFloor, _options.DurableFileSystem);
            longestPause = Max(longestPause, Max(firstCheckpointPause, secondCheckpointPause));

            long reclaimed = 0;
            var reclaimedFiles = 0;
            var deletionFailures = 0;
            await EnterExclusiveOperationAsync(CancellationToken.None).ConfigureAwait(false);
            var retirementPause = Stopwatch.StartNew();
            Volatile.Write(ref _readerRetirement, 1);
            try
            {
                while (Volatile.Read(ref _activeReads) != 0) await Task.Delay(1).ConfigureAwait(false);
                _indexVisibility.EnterReadLock();
                try
                {
                    if (_primaryIndex.Values.Any(a => selectedFiles.Contains(new(a.Stripe, a.Address.SegmentId))))
                        throw new InvalidOperationException("A current locator still references a retirement candidate.");
                }
                finally { _indexVisibility.ExitReadLock(); }
                _reader.Dispose();
                try
                {
                    foreach (var plan in plans)
                    {
                        var path = SegmentCoreIndependentFiles.Resolve(_rootDirectory, plan.File.Stripe, plan.File.Id);
                        try
                        {
                            if (!File.Exists(path)) continue;
                            var length = new FileInfo(path).Length;
                            File.Delete(path);
                            _options.DurableFileSystem.FlushDirectory(Path.GetDirectoryName(path)!);
                            reclaimed += length; reclaimedFiles++;
                        }
                        catch (IOException) { deletionFailures++; }
                        catch (UnauthorizedAccessException) { deletionFailures++; }
                    }
                }
                finally { _reader = new ObjectLogRecordReader(_rootDirectory, _options.ReadOptions); }
            }
            finally { Volatile.Write(ref _readerRetirement, 0); ExitExclusiveOperation(); }
            longestPause = Max(longestPause, retirementPause.Elapsed); slices += 3;
            Volatile.Write(ref _lastCompactionElapsedTicks, watch.Elapsed.Ticks);
            var bytesAfter = MeasureConcurrentStorageBytes(_rootDirectory);
            // Observed total-store net change includes concurrent foreground writes, checkpoint
            // pruning and archives. Gross retired bytes are reported separately, never as net gain.
            return new(Count, frames, bytesBefore, bytesAfter, Math.Max(0, bytesBefore - bytesAfter), watch.Elapsed)
            {
                BytesCopied = copiedBytes, FilesSelected = plans.Length, FilesReclaimed = reclaimedFiles,
                IsPartial = deletionFailures != 0 || plans.Length < catalog.Length,
                Slices = slices, LongestExclusiveSlice = longestPause, PreparationPause = preparationPause,
                LongestCopySlice = TimeSpan.Zero, FinalizationPause = retirementPause.Elapsed,
                LongestCheckpointCapturePause = Max(firstCheckpointPause, secondCheckpointPause),
                DeletionFailures = deletionFailures, RetiredFileBytes = reclaimed, RetainedBytesCopied = archivedBytes
            };
        }
        finally
        {
            // Finished output files may already be referenced by the live index or a published
            // checkpoint after a failure. Never delete them speculatively in this cleanup path.
            Volatile.Write(ref _compacting, 0); _checkpointGate.Release(); _compactionGate.Release();
        }
    }

    private FilePlan[] BuildIndependentCatalog(KeyValuePair<string, StripedObjectLogRecordAddress>[] snapshot,
        IReadOnlyList<ObjectLogWritePosition> positions, SegmentCoreCompactionOptions options, CancellationToken token,
        bool includeActive = false)
    {
        var records = snapshot.ToLookup(p => new CompactionFile(p.Value.Stripe, p.Value.Address.SegmentId));
        var current = snapshot.Select(p => p.Value).ToHashSet();
        var protectedBytes = (_retention?.SnapshotUnarchived() ?? [])
            .Where(p => !current.Contains(p.Address))
            .GroupBy(p => new CompactionFile(p.Address.Stripe, p.Address.Address.SegmentId))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Bytes));
        var plans = new List<FilePlan>();
        var maxId = DateTime.UtcNow.Ticks;
        for (var stripe = 0; stripe < positions.Count; stripe++)
        {
            foreach (var independent in new[] { false, true })
            {
                var directory = independent ? SegmentCoreIndependentFiles.DirectoryFor(_rootDirectory, stripe)
                    : Path.Combine(StripedObjectLogRecovery.StripeRoot(_rootDirectory, stripe), "segments");
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "segment-*.idbseg"))
                {
                    token.ThrowIfCancellationRequested();
                    if (!ObjectLogFormat.TryParseSegmentId(path, out var id)) continue;
                    if (independent) maxId = Math.Max(maxId, id);
                    else if (id > positions[stripe].SegmentId || (!includeActive && id == positions[stripe].SegmentId)) continue;
                    if (plans.Count >= options.Selection.MaximumCandidatesExamined)
                        throw new InvalidOperationException("Compaction file metadata budget exceeded.");
                    var file = new CompactionFile(stripe, independent ? -id : id);
                    var entries = records[file].ToArray();
                    var live = entries.Sum(p => (long)p.Value.Address.FrameLength);
                    var physical = new FileInfo(path).Length;
                    var protectedOld = protectedBytes.GetValueOrDefault(file);
                    if (live > physical || protectedOld > physical - live)
                        throw new ObjectLogCorruptionException("Compaction candidate has inconsistent physical byte accounting.");
                    plans.Add(new(file, new(file.Key, physical, live, protectedOld, true), entries));
                }
            }
        }
        _nextIndependentId = Math.Max(_nextIndependentId, maxId);
        return plans.ToArray();
    }

    private async ValueTask<Relocation[]> CopyIndependentFileAsync(FilePlan plan,
        SegmentCoreCompactionOptions options, CancellationToken token)
    {
        if (plan.Records.Length == 0) return [];
        var id = Interlocked.Increment(ref _nextIndependentId);
        if (id <= 0) throw new InvalidOperationException("Compaction output identity space exhausted.");
        var path = SegmentCoreIndependentFiles.Resolve(_rootDirectory, plan.File.Stripe, -id);
        var temporary = path + ".creating-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sourcePath = SegmentCoreIndependentFiles.Resolve(_rootDirectory, plan.File.Stripe, plan.File.Id);
        var results = new Relocation[plan.Records.Length];
        var budget = options.MaintenanceBudget;
        try
        {
            using (var source = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.RandomAccess))
            using (var output = File.OpenHandle(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var header = ObjectLogFormat.CreateSegmentHeader(id, 0, 0, out _);
                using (budget is null ? null : await budget.AcquireAsync(header.Length, token).ConfigureAwait(false))
                    await RandomAccess.WriteAsync(output, header, 0, token).ConfigureAwait(false);
                long offset = header.Length;
                for (var i = 0; i < plan.Records.Length; i++)
                {
                    var pair = plan.Records[i];
                    var bytes = await SegmentCoreIndependentFiles.CopyFrameAsync(sourcePath, source, output, offset,
                        pair.Key, pair.Value.Address, options.CopyBufferBytes, budget, token).ConfigureAwait(false);
                    results[i] = new(pair.Key, pair.Value, new(plan.File.Stripe,
                        pair.Value.Address with { SegmentId = -id, Offset = offset }));
                    offset += bytes;
                }
                using (budget is null ? null : await budget.AcquireAsync(0, token).ConfigureAwait(false))
                    _options.DurableFileSystem.FlushFile(output);
            }
            token.ThrowIfCancellationRequested();
            _options.DurableFileSystem.PublishFile(temporary, path);
            return results;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    private static long MeasureConcurrentStorageBytes(string root)
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) { } // Foreground log publication can rename a temporary file.
            catch (DirectoryNotFoundException) { } // An expired history-owner directory may disappear.
        }
        return bytes;
    }
}
