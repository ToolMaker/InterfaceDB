using System.Diagnostics;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore;

/// <summary>Private opt-in whole-group-set cleanup. Live copying and retention relocation run
/// outside mutation exclusion; a bounded number of catch-up passes seeks a stable publication cut.
/// This is not per-file selection: an oversized group set is deferred intact.</summary>
public sealed record AtlasConcurrentCompactionOptions
{
    public int MaximumCopyConcurrency { get; init; } = 2;
    public long MaximumCopyBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumRecordBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumMetadataEntries { get; init; } = 1_000_000;
    public int MaximumCatchUpPasses { get; init; } = 4;
    public TimeSpan MaximumCopyDuration { get; init; } = TimeSpan.FromMinutes(2);
    public IMaintenanceBudget? MaintenanceBudget { get; init; }
    public bool RequireFreeSpaceAdmission { get; init; } = true;
    public long MinimumFreeSpaceReserveBytes { get; init; } = 64L * 1024 * 1024;
    internal void Validate()
    {
        if (MaximumCopyConcurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumCopyConcurrency));
        if (MaximumCopyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaximumCopyBytes));
        if (MaximumRecordBytes is < 128 or > 256 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumRecordBytes));
        if (MaximumMetadataEntries is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumMetadataEntries));
        if (MaximumCatchUpPasses is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumCatchUpPasses));
        if (MaximumCopyDuration <= TimeSpan.Zero || MaximumCopyDuration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(MaximumCopyDuration));
        if (MinimumFreeSpaceReserveBytes < 0) throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceReserveBytes));
        if (MaintenanceBudget is { MaximumWorkUnitBytes: < 128 }) throw new ArgumentOutOfRangeException(nameof(MaintenanceBudget));
    }
}

public readonly record struct AtlasConcurrentCompactionResult(bool Published, CompactionWorkResult Work,
    TimeSpan Elapsed, TimeSpan LongestExclusivePause, int CatchUpPasses, string? DeferReason = null);

public sealed partial class AtlasCoreStore
{
    private readonly record struct MaintenanceRecord(ResolvedRecord Source, int TargetGroup, long PlacementGeneration);
    private sealed record MaintenanceSnapshot(MaintenanceRecord[] Records, AtlasGroupStore[] Groups,
        long[] Versions, long PlacementGeneration, long TransactionWatermark);

    public async ValueTask<CompactionEstimate> EstimateConcurrentCompactionAsync(
        AtlasConcurrentCompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        EnterOperation();
        try
        {
            await _compactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                MaintenanceSnapshot snapshot;
                await EnterExclusiveMutationAsync(cancellationToken).ConfigureAwait(false);
                try { snapshot = await CaptureMaintenanceSnapshotAsync(options, cancellationToken).ConfigureAwait(false); }
                finally { ExitExclusiveMutation(); }
                var live = snapshot.Records.Sum(r => (long)r.Source.Address.FrameLength);
                var liveAddresses = snapshot.Records.Select(r => new RetainedAddress(r.Source.Group, _groupSets.CurrentRoot, r.Source.Address)).ToHashSet();
                var protectedBytes = (_retention?.SnapshotUnarchived() ?? []).Where(r => !liveAddresses.Contains(r.Address)).Sum(r => r.Bytes);
                var physical = snapshot.Groups.Sum(g => g.PhysicalBytes);
                var required = checked(live + (_retention?.Statistics.RetainedBytes ?? 0));
                var available = !options.RequireFreeSpaceAdmission || new DriveInfo(Path.GetPathRoot(_rootDirectory)!).AvailableFreeSpace
                    >= checked(required + options.MinimumFreeSpaceReserveBytes);
                return new(physical, Math.Max(0, physical - live - protectedBytes),
                    ForegroundOutstanding: Volatile.Read(ref _activeMutations), HasWorkingSpace: available);
            }
            finally { _compactionGate.Release(); }
        }
        finally { ExitOperation(); }
    }

    public async ValueTask<AtlasConcurrentCompactionResult> CompactConcurrentAsync(
        AtlasConcurrentCompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); options.Validate();
        EnterOperation();
        try
        {
            await _compactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await CompactConcurrentCoreAsync(options, cancellationToken).ConfigureAwait(false); }
            finally { _compactionGate.Release(); }
        }
        finally { ExitOperation(); }
    }

    private async ValueTask<AtlasConcurrentCompactionResult> CompactConcurrentCoreAsync(
        AtlasConcurrentCompactionOptions options, CancellationToken token)
    {
        var callerToken = token;
        using var deadline = new CancellationTokenSource(options.MaximumCopyDuration);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        token = bounded.Token;
        Volatile.Write(ref _compacting, 1);
        var watch = Stopwatch.StartNew(); var longestPause = TimeSpan.Zero;
        var candidates = new List<AtlasGroupStore>();
        string? candidateRoot = null; long generation = 0;
        var attemptedPublication = false; var published = false;
        long bytesCopied = 0, framesCopied = 0, retainedCopied = 0;
        var passes = 0;
        var previous = new Dictionary<string, MaintenanceRecord>(StringComparer.Ordinal);
        long bytesBefore = 0;
        AtlasConcurrentCompactionResult Deferred(string reason) => new(false,
            new CompactionWorkResult(framesCopied, bytesCopied, 0, _retention?.Statistics.RetainedBytes ?? 0, true)
            { Deferred = true, ReasonCode = reason },
            watch.Elapsed, longestPause, passes, reason);
        try
        {
            bytesBefore = MeasureMaintenanceBytes(_rootDirectory);
            for (passes = 1; passes <= options.MaximumCatchUpPasses; passes++)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed >= options.MaximumCopyDuration) return Deferred("time-budget");
                MaintenanceSnapshot snapshot;
                await EnterExclusiveMutationAsync(token).ConfigureAwait(false);
                var pause = Stopwatch.StartNew();
                try { snapshot = await CaptureMaintenanceSnapshotAsync(options, token).ConfigureAwait(false); }
                finally { ExitExclusiveMutation(); longestPause = Longer(longestPause, pause.Elapsed); }
                var current = snapshot.Records.ToDictionary(r => r.Source.Key, StringComparer.Ordinal);
                var changed = snapshot.Records.Where(r => !previous.TryGetValue(r.Source.Key, out var old) || old != r).ToArray();
                var removals = previous.Values.Where(old => !current.TryGetValue(old.Source.Key, out var now)
                    || now.TargetGroup != old.TargetGroup).ToArray();
                var maximumRecord = Math.Min(options.MaximumRecordBytes, options.MaintenanceBudget?.MaximumWorkUnitBytes ?? long.MaxValue);
                var retained = _retention?.SnapshotUnarchived() ?? [];
                if (changed.Any(r => r.Source.Address.FrameLength > maximumRecord) || retained.Any(r => r.Bytes > maximumRecord))
                    return Deferred("record-unit-limit");
                var deleteBytes = removals.Sum(r => (long)MaintenanceDeleteLength(r.Source.Key));
                if (removals.Any(r => MaintenanceDeleteLength(r.Source.Key) > maximumRecord)) return Deferred("record-unit-limit");
                var additional = checked(changed.Sum(r => (long)r.Source.Address.FrameLength) + deleteBytes + retained.Sum(r => r.Bytes));
                if (additional > options.MaximumCopyBytes - bytesCopied - retainedCopied) return Deferred("copy-byte-budget");
                if (options.RequireFreeSpaceAdmission && new DriveInfo(Path.GetPathRoot(_rootDirectory)!).AvailableFreeSpace
                    < checked(additional + options.MinimumFreeSpaceReserveBytes)) return Deferred("working-space");
                if (candidateRoot is null)
                {
                    var candidate = await _groupSets.CreateCandidateAsync(token).ConfigureAwait(false);
                    generation = candidate.Generation; candidateRoot = candidate.RootDirectory;
                }
                // Candidate writers are private to this operation. The shared budget, not the number
                // of radix groups, limits active copy I/O. Never replay the application WAL into them.
                while (candidates.Count < snapshot.Groups.Length)
                {
                    var id = candidates.Count;
                    candidates.Add(await AtlasGroupStore.OpenAsync(Path.Combine(candidateRoot, MaintenanceGroupDirectoryName(id)),
                        _options, _transactionLog, _options.DurableFileSystem, token, id,
                        recoverMutationWal: false, readHandleCache: _readHandleCache).ConfigureAwait(false));
                }
                var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaximumCopyConcurrency, CancellationToken = token };
                await Parallel.ForEachAsync(removals, parallel, async (record, cancellation) =>
                {
                    var length = MaintenanceDeleteLength(record.Source.Key);
                    using var admission = options.MaintenanceBudget is null ? null
                        : await options.MaintenanceBudget.AcquireAsync(length, cancellation).ConfigureAwait(false);
                    await candidates[record.TargetGroup].DeleteAsync(record.Source.Key, snapshot.PlacementGeneration, cancellation).ConfigureAwait(false);
                    Interlocked.Add(ref bytesCopied, length);
                }).ConfigureAwait(false);
                await Parallel.ForEachAsync(changed, parallel, async (record, cancellation) =>
                {
                    byte[] payload;
                    using (options.MaintenanceBudget is null ? null
                        : await options.MaintenanceBudget.AcquireAsync(record.Source.Address.FrameLength, cancellation).ConfigureAwait(false))
                        payload = (await snapshot.Groups[record.Source.Group].ReadAtAsync(record.Source.Key,
                            record.Source.Address, cancellation).ConfigureAwait(false)).Payload
                            ?? throw new InvalidDataException("Maintenance source record disappeared.");
                    using (options.MaintenanceBudget is null ? null
                        : await options.MaintenanceBudget.AcquireAsync(record.Source.Address.FrameLength, cancellation).ConfigureAwait(false))
                        await candidates[record.TargetGroup].PutAsync(record.Source.Key, payload,
                            record.PlacementGeneration, cancellation).ConfigureAwait(false);
                    Interlocked.Add(ref bytesCopied, record.Source.Address.FrameLength);
                    Interlocked.Increment(ref framesCopied);
                }).ConfigureAwait(false);
                previous = current;
                if (_retention is not null)
                    retainedCopied += await _retention.RelocateAsync(_ => true, options.MaintenanceBudget, token).ConfigureAwait(false);
                // Candidate checkpoints capture exactly the snapshot's transaction watermark. If
                // application state changed meanwhile the final check rejects this publication cut.
                foreach (var group in candidates)
                    await group.CheckpointAsync(snapshot.TransactionWatermark, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await EnterExclusiveMutationAsync(token).ConfigureAwait(false);
                pause.Restart();
                try
                {
                    await _placement.FlushMaintenanceAsync(token).ConfigureAwait(false);
                    var finalGroups = await GetAllGroupsAsync(token).ConfigureAwait(false);
                    if (_placement.Generation != snapshot.PlacementGeneration
                        || finalGroups.Length != snapshot.Groups.Length
                        || snapshot.Groups.Where((g, i) => g.IndexVersion != snapshot.Versions[i]).Any()
                        || (_retention?.SnapshotUnarchived().Count ?? 0) != 0)
                        continue;
                    // No data copy, policy or I/O-budget callback occurs under this final gate.
                    // Durability publication and handle retirement still produce a measured pause.
                    await DisposeGroupsAsync(candidates).ConfigureAwait(false); candidates.Clear();
                    attemptedPublication = true;
                    await _groupSets.PublishAsync(generation, candidateRoot, CancellationToken.None).ConfigureAwait(false);
                    published = true;
                    Volatile.Write(ref _readerRetirement, 1);
                    while (Volatile.Read(ref _activeReads) != 0) await Task.Delay(1).ConfigureAwait(false);
                    await DisposeCurrentGroupsAndResetAsync().ConfigureAwait(false);
                    _resolvedGroups.Clear(); Volatile.Write(ref _resolvedGroupAdmissions, 0); Volatile.Write(ref _resolvedSnapshot, null);
                    Volatile.Write(ref _readerRetirement, 0);
                }
                finally { Volatile.Write(ref _readerRetirement, 0); ExitExclusiveMutation(); longestPause = Longer(longestPause, pause.Elapsed); }
                // The old generation is no longer addressable by a new read. It may be removed
                // outside mutation exclusion; active old readers and retained baselines were drained.
                _groupSets.CleanupSupersededSets();
                var remainingOld = Directory.EnumerateDirectories(Path.Combine(_rootDirectory, "group-sets", "sets"), "set-*")
                    .Any(path => !string.Equals(path, candidateRoot, StringComparison.OrdinalIgnoreCase));
                return new(true, new(framesCopied, bytesCopied, Math.Max(0, bytesBefore - MeasureMaintenanceBytes(_rootDirectory)),
                    _retention?.Statistics.RetainedBytes ?? 0, remainingOld), watch.Elapsed, longestPause, passes,
                    remainingOld ? "retirement-incomplete" : null);
            }
            passes = options.MaximumCatchUpPasses;
            return Deferred("foreground-contention");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerToken.IsCancellationRequested && !attemptedPublication)
        {
            return Deferred("time-budget");
        }
        catch
        {
            // A manifest operation may have durably published before throwing. Preserve both
            // generations and require reopen; never delete the potentially authoritative candidate.
            if (attemptedPublication) Volatile.Write(ref _faulted, 1);
            throw;
        }
        finally
        {
            try
            {
                await DisposeGroupsAsync(candidates).ConfigureAwait(false);
                if (!attemptedPublication && !published && candidateRoot is not null)
                    _groupSets.DiscardCandidate(generation, candidateRoot);
            }
            finally { Volatile.Write(ref _compacting, 0); }
        }
    }

    private async ValueTask<MaintenanceSnapshot> CaptureMaintenanceSnapshotAsync(AtlasConcurrentCompactionOptions options, CancellationToken token)
    {
        await _placement.FlushMaintenanceAsync(token).ConfigureAwait(false);
        var groups = await GetAllGroupsAsync(token).ConfigureAwait(false);
        if (groups.Sum(g => (long)g.Count) > options.MaximumMetadataEntries)
            throw new InvalidOperationException("Atlas compaction metadata budget exceeded.");
        var live = await ResolveLiveRecordsAsync(token).ConfigureAwait(false);
        var records = live.Select(r =>
        {
            var route = _placement.RouteForWrite(r.Key);
            return new MaintenanceRecord(r, route.PrimaryGroup, route.Generation);
        }).ToArray();
        return new(records, groups, groups.Select(g => g.IndexVersion).ToArray(), _placement.Generation, _transactionLog.LastCommittedSequence);
    }

    private static TimeSpan Longer(TimeSpan first, TimeSpan second) => first > second ? first : second;
    private static int MaintenanceDeleteLength(string key) =>
        AtlasGroupLogFormat.CalculateFrameLength(AtlasGroupRecordKind.Delete, 1, 1, key, 0);
    private static long MeasureMaintenanceBytes(string root)
    {
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return bytes;
    }
}
