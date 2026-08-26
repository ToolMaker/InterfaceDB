using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Storage.SegmentCore.ObjectLog;
using Idb.Storage.SegmentCore;

internal static class SegmentCoreLimitsRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = SegmentCoreLimitsOptions.Parse(args);
        var runRoot = Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreLimits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new SegmentCoreLimitsReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            PhysicalDisk = Environment.GetEnvironmentVariable("INTERFACEDB_PHASE_TWO_DISK") ?? "not supplied by runner",
            Options = options
        };

        try
        {
            Console.WriteLine("InterfaceDB SegmentCore adversarial limits suite");
            Console.WriteLine($"Records: {options.Records:N0}; churn cycles: {options.ChurnCycles}; payload: {options.PayloadBytes:N0} B");
            await ExecuteAsync(runRoot, options, report).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            Analyze(report);
            await WriteReportAsync(report, options.OutputPath).ConfigureAwait(false);
            if (!options.KeepData)
            {
                TryDeleteRunRoot(runRoot);
            }
            else
            {
                Console.WriteLine($"Run data retained at: {runRoot}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(report.Passed
            ? "SegmentCore limits execution/integrity: PASS (not a production admission)"
            : "SegmentCore limits execution/integrity: FAIL");
        foreach (var finding in report.Findings)
        {
            Console.WriteLine($"  {finding}");
        }

        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task ExecuteAsync(
        string runRoot,
        SegmentCoreLimitsOptions options,
        SegmentCoreLimitsReport report)
    {
        var segmentRoot = Path.Combine(runRoot, "segment-core");
        var sqlitePath = Path.Combine(runRoot, "sqlite", "limits.sqlite");
        var segmentOptions = CreateSegmentOptions(options);
        var records = CreateRecords(options.Records, options.PayloadBytes);
        var encodedPayloadBytes = records.Sum(static record => (long)PhaseZeroCodec.Encode(record).Length);
        SegmentCoreStore? segment = null;
        SqliteGateStore? sqlite = null;

        try
        {
            segment = await SegmentCoreStore.OpenAsync(segmentRoot, segmentOptions).ConfigureAwait(false);
            sqlite = new SqliteGateStore(
                sqlitePath,
                StorageDurabilityMode.Durable,
                SqliteClientKind.MicrosoftDataSqlite,
                createSecondaryIndexes: true);
            await sqlite.InitializeAsync().ConfigureAwait(false);

            report.Metrics.Add(await MeasureConcurrentAsync(
                "Durable object-at-a-time seed",
                "SegmentCore",
                records.Length,
                options.Concurrency,
                index => SaveSegmentAsync(segment, records[index]),
                collectGarbage: true).ConfigureAwait(false));
            report.Metrics.Add(await MeasureConcurrentAsync(
                "Durable object-at-a-time seed",
                "SQLite FULL",
                records.Length,
                options.Concurrency,
                index => InsertSqliteAsync(sqlite, records[index]),
                collectGarbage: true).ConfigureAwait(false));

            _ = await segment.CheckpointAsync().ConfigureAwait(false);
            await sqlite.CheckpointWalAsync(truncate: true).ConfigureAwait(false);
            AddStorageSample(report, "Seeded and maintained", 0, "SegmentCore", segmentRoot, records.Length, encodedPayloadBytes);
            AddStorageSample(report, "Seeded and maintained", 0, "SQLite FULL", sqlite.GetStorageStats(), records.Length, encodedPayloadBytes);

            await AddExactReadMetricsAsync(segment, sqlite, records, options, report).ConfigureAwait(false);
            await AddScanMetricsAsync(segment, sqlite, records, options, report).ConfigureAwait(false);

            await segment.DisposeAsync().ConfigureAwait(false);
            segment = null;
            report.Metrics.Add(await MeasureSegmentReopenAsync(
                "Checkpoint reopen",
                segmentRoot,
                segmentOptions,
                replacement => segment = replacement).ConfigureAwait(false));
            report.Metrics.Add(await MeasureSingleAsync(
                "Checkpoint reopen",
                "SQLite FULL",
                async () =>
                {
                    await sqlite.ReopenAsync().ConfigureAwait(false);
                    return 1;
                }).ConfigureAwait(false));

            var checkpointReopenedSegment = segment
                ?? throw new InvalidOperationException("SegmentCore checkpoint reopen did not publish the store.");
            await VerifyStoreAsync(checkpointReopenedSegment, sqlite, records).ConfigureAwait(false);

            await checkpointReopenedSegment.DisposeAsync().ConfigureAwait(false);
            segment = null;
            DeleteCheckpointDirectory(segmentRoot);
            report.Metrics.Add(await MeasureSegmentReopenAsync(
                "Full log recovery without checkpoint",
                segmentRoot,
                segmentOptions,
                replacement => segment = replacement).ConfigureAwait(false));
            var recoveredSegment = segment
                ?? throw new InvalidOperationException("SegmentCore full recovery did not publish the reopened store.");
            report.Capabilities.Add(new SegmentCoreCapability(
                "Recovery without persistent index catalog",
                "Supported but O(total log history)",
                "SQLite keeps its B-tree catalog in the database file",
                $"SegmentCore scanned {recoveredSegment.Statistics.FullRecoveryFramesScanned:N0} log frames in the measured recovery."));
            await VerifyStoreAsync(recoveredSegment, sqlite, records).ConfigureAwait(false);
            _ = await recoveredSegment.CheckpointAsync().ConfigureAwait(false);

            await AddCapabilityChecksAsync(recoveredSegment, segmentRoot, segmentOptions, sqlitePath, records[0], report)
                .ConfigureAwait(false);

            for (var cycle = 1; cycle <= options.ChurnCycles; cycle++)
            {
                var revision = cycle;
                records = records
                    .Select(record => record with
                    {
                        Revision = revision,
                        Content = MutateContent(record.Content, revision)
                    })
                    .ToArray();
                report.Metrics.Add(await MeasureConcurrentAsync(
                    $"Update churn cycle {cycle}",
                    "SegmentCore",
                    records.Length,
                    options.Concurrency,
                    index => UpdateSegmentAsync(segment, records[index]),
                    collectGarbage: true).ConfigureAwait(false));
                report.Metrics.Add(await MeasureConcurrentAsync(
                    $"Update churn cycle {cycle}",
                    "SQLite FULL",
                    records.Length,
                    options.Concurrency,
                    index => UpdateSqliteAsync(sqlite, records[index]),
                    collectGarbage: true).ConfigureAwait(false));

                _ = await segment.CheckpointAsync().ConfigureAwait(false);
                await sqlite.CheckpointWalAsync(truncate: true).ConfigureAwait(false);
                AddStorageSample(report, "Repeated update", cycle, "SegmentCore", segmentRoot, records.Length, encodedPayloadBytes);
                AddStorageSample(report, "Repeated update", cycle, "SQLite FULL", sqlite.GetStorageStats(), records.Length, encodedPayloadBytes);
            }

            var deleted = records.Where((_, index) => index % 2 == 0).ToArray();
            var retained = records.Where((_, index) => index % 2 != 0).ToArray();
            report.Metrics.Add(await MeasureConcurrentAsync(
                "Delete half the live set",
                "SegmentCore",
                deleted.Length,
                options.Concurrency,
                index => DeleteSegmentAsync(segment, deleted[index].Id),
                collectGarbage: true).ConfigureAwait(false));
            report.Metrics.Add(await MeasureConcurrentAsync(
                "Delete half the live set",
                "SQLite FULL",
                deleted.Length,
                options.Concurrency,
                index => DeleteSqliteAsync(sqlite, deleted[index].Id),
                collectGarbage: true).ConfigureAwait(false));
            _ = await segment.CheckpointAsync().ConfigureAwait(false);
            await sqlite.CheckpointWalAsync(truncate: true).ConfigureAwait(false);
            var retainedPayloadBytes = retained.Sum(static record => (long)PhaseZeroCodec.Encode(record).Length);
            AddStorageSample(report, "Half deleted; before space reclamation", options.ChurnCycles + 1, "SegmentCore", segmentRoot, retained.Length, retainedPayloadBytes);
            AddStorageSample(report, "Half deleted; before space reclamation", options.ChurnCycles + 1, "SQLite FULL", sqlite.GetStorageStats(), retained.Length, retainedPayloadBytes);

            SegmentCoreCompactionResult compaction = default;
            var segmentCompactionMetric = await MeasureSingleAsync(
                "Space reclamation maintenance",
                "SegmentCore",
                async () =>
                {
                    compaction = await segment.CompactAsync().ConfigureAwait(false);
                    return compaction.LiveEntries;
                }).ConfigureAwait(false);
            report.Metrics.Add(segmentCompactionMetric with
            {
                Detail = $"Reclaimed {compaction.BytesReclaimed:N0} bytes in {compaction.Slices:N0} bounded slices; longest exclusive slice {compaction.LongestExclusiveSlice.TotalMilliseconds:N3} ms."
            });

            var sqliteVacuumMetric = await MeasureSingleAsync(
                "Space reclamation maintenance",
                "SQLite FULL",
                async () =>
                {
                    await sqlite.VacuumAsync().ConfigureAwait(false);
                    await sqlite.CheckpointWalAsync(truncate: true).ConfigureAwait(false);
                    return 1;
                }).ConfigureAwait(false);
            report.Metrics.Add(sqliteVacuumMetric with
            {
                Detail = "VACUUM followed by a truncating WAL checkpoint."
            });

            AddStorageSample(report, "Half deleted; space reclaimed", options.ChurnCycles + 2, "SegmentCore", segmentRoot, retained.Length, retainedPayloadBytes);
            AddStorageSample(report, "Half deleted; space reclaimed", options.ChurnCycles + 2, "SQLite FULL", sqlite.GetStorageStats(), retained.Length, retainedPayloadBytes);

            await segment.DisposeAsync().ConfigureAwait(false);
            segment = null;
            report.Metrics.Add(await MeasureSegmentReopenAsync(
                "Post-compaction reopen",
                segmentRoot,
                segmentOptions,
                replacement => segment = replacement).ConfigureAwait(false));
            var compactedSegment = segment
                ?? throw new InvalidOperationException("SegmentCore post-compaction reopen did not publish the store.");
            await VerifyStoreAsync(compactedSegment, sqlite, retained).ConfigureAwait(false);

            await AddSqliteCheckpointPressureMetricsAsync(sqlite, retained, options, report).ConfigureAwait(false);
            await AddSegmentCheckpointStallMetricsAsync(runRoot, options, report).ConfigureAwait(false);

            report.Capabilities.Add(new SegmentCoreCapability(
                "Online compaction and obsolete-version reclamation",
                "Implemented as bounded interruption-safe copy-forward compaction",
                "WAL checkpoint plus VACUUM available",
                $"This scenario records growth before maintenance, then reclaimed {compaction.BytesReclaimed:N0} bytes in {compaction.Slices:N0} bounded SegmentCore slices and validates the compacted store after reopen."));
            report.Capabilities.Add(new SegmentCoreCapability(
                "Persistent secondary range/prefix/contains indexes",
                "Persisted at the IRepository layer; the raw SegmentCoreStore intentionally has no query-index API",
                "Persistent B-tree indexes available; contains may still scan",
                "The raw-engine filtered-query metric deliberately exercises its full-scan fallback rather than the InterfaceDB repository planner."));
            report.Capabilities.Add(new SegmentCoreCapability(
                "Cold-cache/data-larger-than-RAM qualification",
                "Not yet qualified",
                "Mature cache behavior, but workload-specific",
                "This suite does not claim cold physical-media reads; the operating-system cache remains available."));
        }
        finally
        {
            if (segment is not null)
            {
                await segment.DisposeAsync().ConfigureAwait(false);
            }

            if (sqlite is not null)
            {
                await sqlite.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task AddExactReadMetricsAsync(
        SegmentCoreStore segment,
        SqliteGateStore sqlite,
        IReadOnlyList<GateRecord> records,
        SegmentCoreLimitsOptions options,
        SegmentCoreLimitsReport report)
    {
        var operations = Math.Max(options.Records * 4, 2_000);
        report.Metrics.Add(await MeasureConcurrentAsync(
            "Warm exact read and materialization",
            "SegmentCore",
            operations,
            options.Concurrency,
            async index =>
            {
                var expected = records[index % records.Count];
                var payload = await segment.GetAsync(expected.Id).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Missing SegmentCore key '{expected.Id}'.");
                var decoded = PhaseZeroCodec.Decode(payload);
                return decoded.MaterializedChecksum;
            },
            collectGarbage: true).ConfigureAwait(false));
        report.Metrics.Add(await MeasureConcurrentAsync(
            "Warm exact read and materialization",
            "SQLite FULL",
            operations,
            options.Concurrency,
            async index =>
            {
                var result = await sqlite.QueryAsync(GateQueryKind.Exact, records[index % records.Count].Id)
                    .ConfigureAwait(false);
                return result.Count == 1 ? result.Checksum : throw new InvalidDataException("SQLite exact read failed.");
            },
            collectGarbage: true).ConfigureAwait(false));
    }

    private static async Task AddScanMetricsAsync(
        SegmentCoreStore segment,
        SqliteGateStore sqlite,
        IReadOnlyList<GateRecord> records,
        SegmentCoreLimitsOptions options,
        SegmentCoreLimitsReport report)
    {
        var expectedAll = new GateQueryResult(records.Count, records.Sum(static record => record.MaterializedChecksum));
        report.Metrics.Add(await MeasureRepeatedAsync(
            "Complete materialized scan",
            "SegmentCore",
            options.Iterations,
            async () =>
            {
                var result = await ScanSegmentAsync(segment, static _ => true).ConfigureAwait(false);
                RequireResult(expectedAll, result, "SegmentCore complete scan");
                return result.Checksum;
            }).ConfigureAwait(false));
        report.Metrics.Add(await MeasureRepeatedAsync(
            "Complete materialized scan",
            "SQLite FULL",
            options.Iterations,
            async () =>
            {
                var snapshot = await sqlite.SnapshotAsync().ConfigureAwait(false);
                var result = new GateQueryResult(
                    snapshot.Count,
                    snapshot.Values.Sum(static record => record.MaterializedChecksum));
                RequireResult(expectedAll, result, "SQLite complete scan");
                return result.Checksum;
            }).ConfigureAwait(false));

        var expectedFilteredRecords = records
            .Where(static record => record.Role == "student" && record.Age >= 40)
            .ToArray();
        var expectedFiltered = new GateQueryResult(
            expectedFilteredRecords.Length,
            expectedFilteredRecords.Sum(static record => record.MaterializedChecksum));
        report.Metrics.Add(await MeasureRepeatedAsync(
            "Broad range/conjunction query",
            "SegmentCore full-scan fallback",
            options.Iterations,
            async () =>
            {
                var result = await ScanSegmentAsync(
                    segment,
                    static record => record.Role == "student" && record.Age >= 40).ConfigureAwait(false);
                RequireResult(expectedFiltered, result, "SegmentCore filtered scan");
                return result.Checksum;
            }).ConfigureAwait(false));

        await AddFilteredComparisonAsync(
            "Prefix query without integrated SegmentCore index",
            segment,
            sqlite,
            records,
            options.Iterations,
            static record => record.Name.StartsWith("customer-00000", StringComparison.Ordinal),
            GateQueryKind.Prefix).ConfigureAwait(false);
        await AddFilteredComparisonAsync(
            "Suffix query without integrated SegmentCore index",
            segment,
            sqlite,
            records,
            options.Iterations,
            static record => record.Name.EndsWith("special-tail", StringComparison.Ordinal),
            GateQueryKind.Suffix).ConfigureAwait(false);
        await AddFilteredComparisonAsync(
            "Contains query without integrated SegmentCore index",
            segment,
            sqlite,
            records,
            options.Iterations,
            static record => record.Content.Contains("rare-needle", StringComparison.Ordinal),
            GateQueryKind.Contains).ConfigureAwait(false);

        async Task AddFilteredComparisonAsync(
            string scenario,
            SegmentCoreStore segmentStore,
            SqliteGateStore sqliteStore,
            IReadOnlyList<GateRecord> source,
            int iterations,
            Func<GateRecord, bool> predicate,
            GateQueryKind queryKind)
        {
            var expectedRecords = source.Where(predicate).ToArray();
            var expected = new GateQueryResult(
                expectedRecords.Length,
                expectedRecords.Sum(static record => record.MaterializedChecksum));
            report.Metrics.Add(await MeasureRepeatedAsync(
                scenario,
                "SegmentCore full-scan fallback",
                iterations,
                async () =>
                {
                    var result = await ScanSegmentAsync(segmentStore, predicate).ConfigureAwait(false);
                    RequireResult(expected, result, $"SegmentCore {queryKind} fallback");
                    return result.Checksum;
                }).ConfigureAwait(false));
            report.Metrics.Add(await MeasureRepeatedAsync(
                scenario,
                "SQLite query path",
                iterations,
                async () =>
                {
                    var result = await sqliteStore.QueryAsync(queryKind, string.Empty).ConfigureAwait(false);
                    RequireResult(expected, result, $"SQLite {queryKind} query");
                    return result.Checksum;
                }).ConfigureAwait(false));
        }
        report.Metrics.Add(await MeasureRepeatedAsync(
            "Broad range/conjunction query",
            "SQLite indexed",
            options.Iterations,
            async () =>
            {
                var result = await sqlite.QueryAsync(GateQueryKind.BroadConjunction, string.Empty).ConfigureAwait(false);
                RequireResult(expectedFiltered, result, "SQLite indexed query");
                return result.Checksum;
            }).ConfigureAwait(false));
    }

    private static async Task AddCapabilityChecksAsync(
        SegmentCoreStore segment,
        string segmentRoot,
        SegmentCoreOptions segmentOptions,
        string sqlitePath,
        GateRecord existing,
        SegmentCoreLimitsReport report)
    {
        string segmentMultiWriter;
        try
        {
            await using var second = await SegmentCoreStore.OpenAsync(segmentRoot, segmentOptions).ConfigureAwait(false);
            segmentMultiWriter = "Unexpectedly opened; multi-writer correctness is unqualified";
        }
        catch (IOException exception)
        {
            segmentMultiWriter = $"Rejected ({exception.GetType().Name})";
        }

        var sqliteMultiWriter = "Failed";
        await using (var second = new SqliteGateStore(
                         sqlitePath,
                         StorageDurabilityMode.Durable,
                         SqliteClientKind.MicrosoftDataSqlite,
                         createSecondaryIndexes: true))
        {
            await second.InitializeAsync().ConfigureAwait(false);
            var result = await second.QueryAsync(GateQueryKind.Exact, existing.Id).ConfigureAwait(false);
            sqliteMultiWriter = result.Count == 1 ? "Supported" : "Opened but visibility verification failed";
        }

        report.Capabilities.Add(new SegmentCoreCapability(
            "Independent concurrent store/writer instances",
            segmentMultiWriter,
            sqliteMultiWriter,
            "SegmentCore currently has one owning writer per store root; SQLite coordinates independent connections."));

        var first = GateRecord.Create(9_000_001, "atomic-rollback");
        var secondRecord = GateRecord.Create(9_000_002, "atomic-rollback");
        await using var sqliteAtomic = new SqliteGateStore(
            sqlitePath,
            StorageDurabilityMode.Durable,
            SqliteClientKind.MicrosoftDataSqlite,
            createSecondaryIndexes: true);
        await sqliteAtomic.InitializeAsync().ConfigureAwait(false);
        var rollbackPassed = await sqliteAtomic.VerifyAtomicRollbackAsync(first, secondRecord).ConfigureAwait(false);
        var segmentHasTransactionApi = typeof(SegmentCoreStore).GetMethods()
            .Any(static method => method.Name.Contains("Transaction", StringComparison.Ordinal)
                || method.Name.Contains("SaveMany", StringComparison.Ordinal));
        report.Capabilities.Add(new SegmentCoreCapability(
            "Atomic multi-object transaction",
            segmentHasTransactionApi ? "Supported; qualified by dedicated transaction suite" : "Not supported",
            rollbackPassed ? "Supported; forced rollback verified" : "Rollback verification failed",
            "Explicit SegmentCore transactions are atomic; concurrent standalone SaveAsync calls remain independent autocommits."));
        GC.KeepAlive(segment);
    }

    private static async Task AddSqliteCheckpointPressureMetricsAsync(
        SqliteGateStore sqlite,
        IReadOnlyList<GateRecord> retained,
        SegmentCoreLimitsOptions options,
        SegmentCoreLimitsReport report)
    {
        var sample = retained.Take(Math.Min(256, retained.Count)).ToArray();
        if (sample.Length == 0)
        {
            return;
        }

        report.Metrics.Add(await MeasureConcurrentAsync(
            "Foreground updates without maintenance",
            "SQLite FULL",
            sample.Length,
            options.Concurrency,
            index => UpdateSqliteAsync(sqlite, sample[index] with { Revision = 50_001 }),
            collectGarbage: true).ConfigureAwait(false));

        using var stop = new CancellationTokenSource();
        var maintenance = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await sqlite.CheckpointWalAsync(truncate: false, stop.Token).ConfigureAwait(false);
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        });
        try
        {
            report.Metrics.Add(await MeasureConcurrentAsync(
                "Foreground updates during checkpoint maintenance",
                "SQLite FULL",
                sample.Length,
                options.Concurrency,
                index => UpdateSqliteAsync(sqlite, sample[index] with { Revision = 50_002 }),
                collectGarbage: false).ConfigureAwait(false));
        }
        finally
        {
            stop.Cancel();
            await maintenance.ConfigureAwait(false);
        }
    }

    private static async Task AddSegmentCheckpointStallMetricsAsync(
        string runRoot,
        SegmentCoreLimitsOptions options,
        SegmentCoreLimitsReport report)
    {
        var root = Path.Combine(runRoot, "checkpoint-stall");
        var storeOptions = CreateSegmentOptions(options);
        await using var store = await SegmentCoreStore.OpenAsync(root, storeOptions).ConfigureAwait(false);
        var payload = PhaseZeroCodec.Encode(CreateRecord(0, "stall-payload", Math.Min(options.PayloadBytes, 512)));
        await ForEachConcurrentAsync(
            options.CheckpointStallRecords,
            options.Concurrency,
            index => store.SaveAsync($"stall-{index:D9}", payload).AsTask()).ConfigureAwait(false);
        _ = await store.CheckpointAsync().ConfigureAwait(false);

        const int foregroundWrites = 512;
        report.Metrics.Add(await MeasureConcurrentAsync(
            "Foreground updates without maintenance",
            "SegmentCore",
            foregroundWrites,
            options.Concurrency,
            async index =>
            {
                await store.SaveAsync($"stall-{index:D9}", payload).ConfigureAwait(false);
                return 1;
            },
            collectGarbage: true).ConfigureAwait(false));
        _ = await store.CheckpointAsync().ConfigureAwait(false);

        var checkpointTask = Task.Run(async () => await store.CheckpointAsync().ConfigureAwait(false));
        var observed = await WaitForCheckpointAsync(store, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!observed)
        {
            report.Failures.Add("Could not observe SegmentCore's checkpoint mutation barrier during the stall test.");
            _ = await checkpointTask.ConfigureAwait(false);
            return;
        }

        var metric = await MeasureConcurrentAsync(
            "Foreground updates during checkpoint maintenance",
            "SegmentCore",
            foregroundWrites,
            options.Concurrency,
            async index =>
            {
                await store.SaveAsync($"stall-{index:D9}", payload).ConfigureAwait(false);
                return 1;
            },
            collectGarbage: false).ConfigureAwait(false);
        var checkpoint = await checkpointTask.ConfigureAwait(false);
        report.Metrics.Add(metric with
        {
            Detail = $"A global mutation barrier was active; checkpoint elapsed {checkpoint.Elapsed.TotalMilliseconds:N3} ms."
        });
    }

    private static async Task<bool> WaitForCheckpointAsync(SegmentCoreStore store, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (store.Statistics.CheckpointInProgress)
            {
                return true;
            }

            await Task.Yield();
        }

        return false;
    }

    private static async ValueTask<GateQueryResult> ScanSegmentAsync(
        SegmentCoreStore store,
        Func<GateRecord, bool> predicate)
    {
        var materialized = new List<GateRecord>();
        foreach (var key in store.GetKeysSnapshot())
        {
            var payload = await store.GetAsync(key).ConfigureAwait(false)
                ?? throw new InvalidDataException($"SegmentCore key '{key}' disappeared during scan.");
            var record = PhaseZeroCodec.Decode(payload);
            if (predicate(record))
            {
                materialized.Add(record);
            }
        }

        var result = new GateQueryResult(
            materialized.Count,
            materialized.Sum(static record => record.MaterializedChecksum));
        GC.KeepAlive(materialized);
        return result;
    }

    private static async Task VerifyStoreAsync(
        SegmentCoreStore segment,
        SqliteGateStore sqlite,
        IReadOnlyList<GateRecord> expected)
    {
        if (segment.Count != expected.Count)
        {
            throw new InvalidDataException($"SegmentCore count {segment.Count:N0} != expected {expected.Count:N0}.");
        }

        var sqliteSnapshot = await sqlite.SnapshotAsync().ConfigureAwait(false);
        if (sqliteSnapshot.Count != expected.Count)
        {
            throw new InvalidDataException($"SQLite count {sqliteSnapshot.Count:N0} != expected {expected.Count:N0}.");
        }

        foreach (var record in expected)
        {
            var payload = await segment.GetAsync(record.Id).ConfigureAwait(false)
                ?? throw new InvalidDataException($"SegmentCore lost '{record.Id}'.");
            if (PhaseZeroCodec.Decode(payload) != record
                || !sqliteSnapshot.TryGetValue(record.Id, out var sqliteRecord)
                || sqliteRecord != record)
            {
                throw new InvalidDataException($"Materialized record mismatch for '{record.Id}'.");
            }
        }
    }

    private static async Task<SegmentCoreLimitMetric> MeasureSegmentReopenAsync(
        string scenario,
        string root,
        SegmentCoreOptions options,
        Action<SegmentCoreStore> publish)
    {
        CollectGarbage();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var reopened = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        publish(reopened);
        return SingleMetric(
            scenario,
            "SegmentCore",
            elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            $"Tail frames replayed: {reopened.Statistics.TailRecordsReplayed:N0}; full-recovery frames scanned: {reopened.Statistics.FullRecoveryFramesScanned:N0}.");
    }

    private static async Task<SegmentCoreLimitMetric> MeasureSingleAsync(
        string scenario,
        string engine,
        Func<ValueTask<long>> operation)
    {
        CollectGarbage();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        _ = await operation().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return SingleMetric(
            scenario,
            engine,
            elapsed,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            null);
    }

    private static SegmentCoreLimitMetric SingleMetric(
        string scenario,
        string engine,
        double elapsedMilliseconds,
        long allocatedBytes,
        string? detail) => new(
            scenario,
            engine,
            1,
            elapsedMilliseconds,
            elapsedMilliseconds <= 0 ? 0 : 1000d / elapsedMilliseconds,
            elapsedMilliseconds,
            elapsedMilliseconds,
            elapsedMilliseconds,
            allocatedBytes,
            detail);

    private static async Task<SegmentCoreLimitMetric> MeasureRepeatedAsync(
        string scenario,
        string engine,
        int iterations,
        Func<ValueTask<long>> operation)
    {
        CollectGarbage();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var latency = new double[iterations];
        long checksum = 0;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            checksum += await operation().ConfigureAwait(false);
            latency[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Array.Sort(latency);
        GC.KeepAlive(checksum);
        return CreateMetric(
            scenario,
            engine,
            iterations,
            elapsed,
            latency,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            null);
    }

    private static async Task<SegmentCoreLimitMetric> MeasureConcurrentAsync(
        string scenario,
        string engine,
        int operations,
        int concurrency,
        Func<int, ValueTask<long>> operation,
        bool collectGarbage)
    {
        if (collectGarbage)
        {
            CollectGarbage();
        }

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var latency = new double[operations];
        long checksum = 0;
        var next = -1;
        var started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, Math.Min(concurrency, operations))
            .Select(_ => WorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Array.Sort(latency);
        GC.KeepAlive(checksum);
        return CreateMetric(
            scenario,
            engine,
            operations,
            elapsed,
            latency,
            GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            null);

        async Task WorkerAsync()
        {
            await Task.Yield();
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= operations)
                {
                    return;
                }

                var operationStarted = Stopwatch.GetTimestamp();
                var value = await operation(index).ConfigureAwait(false);
                latency[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
                Interlocked.Add(ref checksum, value);
            }
        }
    }

    private static SegmentCoreLimitMetric CreateMetric(
        string scenario,
        string engine,
        int operations,
        double elapsedMilliseconds,
        double[] sortedLatency,
        long allocatedBytes,
        string? detail) => new(
            scenario,
            engine,
            operations,
            elapsedMilliseconds,
            elapsedMilliseconds <= 0 ? 0 : operations * 1000d / elapsedMilliseconds,
            Percentile(sortedLatency, 0.50),
            Percentile(sortedLatency, 0.95),
            Percentile(sortedLatency, 0.99),
            allocatedBytes,
            detail);

    private static async Task ForEachConcurrentAsync(
        int operations,
        int concurrency,
        Func<int, Task> operation)
    {
        var next = -1;
        var workers = Enumerable.Range(0, Math.Min(concurrency, operations))
            .Select(_ => WorkerAsync())
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        async Task WorkerAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= operations)
                {
                    return;
                }

                await operation(index).ConfigureAwait(false);
            }
        }
    }

    private static void AddStorageSample(
        SegmentCoreLimitsReport report,
        string stage,
        int cycle,
        string engine,
        string root,
        int liveRecords,
        long livePayloadBytes)
    {
        var stats = DirectoryStats(root);
        report.Storage.Add(new SegmentCoreStorageSample(
            stage,
            cycle,
            engine,
            liveRecords,
            livePayloadBytes,
            stats.Bytes,
            stats.Files,
            livePayloadBytes == 0 ? 0 : (double)stats.Bytes / livePayloadBytes));
    }

    private static void AddStorageSample(
        SegmentCoreLimitsReport report,
        string stage,
        int cycle,
        string engine,
        GateStorageStats stats,
        int liveRecords,
        long livePayloadBytes) => report.Storage.Add(new SegmentCoreStorageSample(
            stage,
            cycle,
            engine,
            liveRecords,
            livePayloadBytes,
            stats.Bytes,
            stats.Files,
            livePayloadBytes == 0 ? 0 : (double)stats.Bytes / livePayloadBytes));

    private static (long Bytes, int Files) DirectoryStats(string root)
    {
        if (!Directory.Exists(root))
        {
            return default;
        }

        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(path).Length;
            files++;
        }

        return (bytes, files);
    }

    private static SegmentCoreOptions CreateSegmentOptions(SegmentCoreLimitsOptions options) => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = options.StripeCount,
            MaximumBatchRecords = 64,
            QueueCapacityPerStripe = 1_024,
            QueueFullBehavior = ObjectLogQueueFullBehavior.Wait,
            MaxSegmentBytes = 64L * 1024 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
        }
    };

    private static GateRecord[] CreateRecords(int count, int contentBytes) =>
        Enumerable.Range(0, count)
            .Select(index => CreateRecord(index, "limits", contentBytes))
            .ToArray();

    private static GateRecord CreateRecord(int ordinal, string prefix, int contentBytes)
    {
        var baseline = GateRecord.Create(ordinal, prefix);
        var marker = baseline.Content + "|";
        return baseline with
        {
            Content = marker + new string((char)('a' + ordinal % 26), Math.Max(0, contentBytes - marker.Length))
        };
    }

    private static string MutateContent(string content, int revision)
    {
        if (content.Length == 0)
        {
            return content;
        }

        var characters = content.ToCharArray();
        characters[^1] = (char)('a' + revision % 26);
        return new string(characters);
    }

    private static async ValueTask<long> SaveSegmentAsync(SegmentCoreStore store, GateRecord record)
    {
        await store.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
        return 1;
    }

    private static async ValueTask<long> UpdateSegmentAsync(SegmentCoreStore store, GateRecord record)
    {
        await store.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
        return 1;
    }

    private static async ValueTask<long> DeleteSegmentAsync(SegmentCoreStore store, string key) =>
        await store.DeleteAsync(key).ConfigureAwait(false)
            ? 1
            : throw new InvalidDataException($"SegmentCore failed to delete '{key}'.");

    private static async ValueTask<long> InsertSqliteAsync(SqliteGateStore store, GateRecord record)
    {
        await store.InsertAsync(record).ConfigureAwait(false);
        return 1;
    }

    private static async ValueTask<long> UpdateSqliteAsync(SqliteGateStore store, GateRecord record) =>
        await store.UpdateAsync(record).ConfigureAwait(false)
            ? 1
            : throw new InvalidDataException($"SQLite failed to update '{record.Id}'.");

    private static async ValueTask<long> DeleteSqliteAsync(SqliteGateStore store, string key) =>
        await store.DeleteAsync(key).ConfigureAwait(false)
            ? 1
            : throw new InvalidDataException($"SQLite failed to delete '{key}'.");

    private static void RequireResult(GateQueryResult expected, GateQueryResult actual, string operation)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"{operation} returned count/checksum {actual.Count}/{actual.Checksum}; expected {expected.Count}/{expected.Checksum}.");
        }
    }

    private static void DeleteCheckpointDirectory(string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var checkpoint = Path.GetFullPath(Path.Combine(root, "checkpoints"));
        if (!checkpoint.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(checkpoint), "checkpoints", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to remove unexpected checkpoint path '{checkpoint}'.");
        }

        if (Directory.Exists(checkpoint))
        {
            Directory.Delete(checkpoint, recursive: true);
        }
    }

    private static void TryDeleteRunRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCoreLimits"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected limits path '{fullRoot}'.");
        }

        try
        {
            Directory.Delete(fullRoot, recursive: true);
        }
        catch
        {
            // Benchmark cleanup must not hide the measured result.
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static double Ratio(double numerator, double denominator) =>
        denominator == 0 ? 0 : numerator / denominator;

    private static void Analyze(SegmentCoreLimitsReport report)
    {
        AddComparisonFinding(report, "Durable object-at-a-time seed", higherIsBetter: true, "durable write throughput");
        AddComparisonFinding(report, "Warm exact read and materialization", higherIsBetter: true, "warm exact-read throughput");
        AddComparisonFinding(report, "Complete materialized scan", higherIsBetter: true, "complete-scan throughput");
        AddComparisonFinding(report, "Broad range/conjunction query", higherIsBetter: true, "broad-query throughput");
        AddComparisonFinding(report, "Prefix query without integrated SegmentCore index", higherIsBetter: true, "prefix-query throughput without SegmentCore index integration");
        AddComparisonFinding(report, "Suffix query without integrated SegmentCore index", higherIsBetter: true, "suffix-query throughput without SegmentCore index integration");
        AddComparisonFinding(report, "Contains query without integrated SegmentCore index", higherIsBetter: true, "contains-query throughput without SegmentCore index integration");

        var segmentReopen = FindMetric(report, "Checkpoint reopen", "SegmentCore");
        var sqliteReopen = FindMetric(report, "Checkpoint reopen", "SQLite FULL");
        if (segmentReopen is not null && sqliteReopen is not null)
        {
            report.Findings.Add(
                $"WEAKNESS: checkpoint reopen was {segmentReopen.P50Milliseconds:N3} ms and allocated {segmentReopen.AllocatedBytes:N0} bytes; SQLite was {sqliteReopen.P50Milliseconds:N3} ms and allocated {sqliteReopen.AllocatedBytes:N0} bytes.");
        }

        var fullRecovery = FindMetric(report, "Full log recovery without checkpoint", "SegmentCore");
        if (segmentReopen is not null && fullRecovery is not null)
        {
            report.Findings.Add(
                $"WEAKNESS: without its checkpoint, SegmentCore required {fullRecovery.P50Milliseconds:N3} ms and {fullRecovery.AllocatedBytes:N0} allocated bytes to recover, versus {segmentReopen.P50Milliseconds:N3} ms and {segmentReopen.AllocatedBytes:N0} bytes with the checkpoint; full recovery remains O(total history)." );
        }

        var segmentBaseline = report.Storage.FirstOrDefault(static sample => sample.Engine == "SegmentCore");
        var segmentBeforeReclamation = report.Storage.LastOrDefault(static sample =>
            sample.Engine == "SegmentCore" && sample.Stage == "Half deleted; before space reclamation");
        var segmentFinal = report.Storage.LastOrDefault(static sample =>
            sample.Engine == "SegmentCore" && sample.Stage == "Half deleted; space reclaimed");
        var sqliteBaseline = report.Storage.FirstOrDefault(static sample => sample.Engine == "SQLite FULL");
        var sqliteBeforeReclamation = report.Storage.LastOrDefault(static sample =>
            sample.Engine == "SQLite FULL" && sample.Stage == "Half deleted; before space reclamation");
        var sqliteFinal = report.Storage.LastOrDefault(static sample =>
            sample.Engine == "SQLite FULL" && sample.Stage == "Half deleted; space reclaimed");
        if (segmentBaseline is not null
            && segmentBeforeReclamation is not null
            && segmentFinal is not null
            && sqliteBaseline is not null
            && sqliteBeforeReclamation is not null
            && sqliteFinal is not null)
        {
            report.Findings.Add(
                $"OBSERVATION: before space reclamation, SegmentCore grew from {segmentBaseline.StorageBytes:N0} to {segmentBeforeReclamation.StorageBytes:N0} bytes ({Ratio(segmentBeforeReclamation.StorageBytes, segmentBaseline.StorageBytes):N2}x) after churn/deletes; SQLite grew from {sqliteBaseline.StorageBytes:N0} to {sqliteBeforeReclamation.StorageBytes:N0} bytes ({Ratio(sqliteBeforeReclamation.StorageBytes, sqliteBaseline.StorageBytes):N2}x).");
            report.Findings.Add(
                $"OBSERVATION: bounded SegmentCore compaction reduced storage to {segmentFinal.StorageBytes:N0} bytes ({segmentFinal.StorageToLivePayloadRatio:N2}x live payload); SQLite VACUUM reduced storage to {sqliteFinal.StorageBytes:N0} bytes ({sqliteFinal.StorageToLivePayloadRatio:N2}x live payload).");
        }

        var segmentCompaction = FindMetric(report, "Space reclamation maintenance", "SegmentCore");
        var sqliteVacuum = FindMetric(report, "Space reclamation maintenance", "SQLite FULL");
        if (segmentCompaction is not null && sqliteVacuum is not null)
        {
            report.Findings.Add(
                $"OBSERVATION: SegmentCore bounded compaction took {segmentCompaction.P50Milliseconds:N3} ms; SQLite VACUUM plus WAL truncation took {sqliteVacuum.P50Milliseconds:N3} ms in this workload.");
        }

        var segmentNormal = FindMetric(report, "Foreground updates without maintenance", "SegmentCore");
        var segmentCheckpoint = FindMetric(report, "Foreground updates during checkpoint maintenance", "SegmentCore");
        if (segmentNormal is not null && segmentCheckpoint is not null)
        {
            var stallRatio = Ratio(segmentCheckpoint.P99Milliseconds, segmentNormal.P99Milliseconds);
            var classification = stallRatio >= 1.10 ? "WEAKNESS" : "OBSERVATION";
            report.Findings.Add(
                $"{classification}: SegmentCore checkpoint maintenance multiplied foreground p99 write latency by {stallRatio:N1}x ({segmentNormal.P99Milliseconds:N3} to {segmentCheckpoint.P99Milliseconds:N3} ms)." );
        }

        foreach (var capability in report.Capabilities.Where(static capability =>
                     capability.SegmentCore.StartsWith("Not", StringComparison.Ordinal)
                     || capability.SegmentCore.StartsWith("Rejected", StringComparison.Ordinal)
                     || capability.SegmentCore.Contains("O(total", StringComparison.Ordinal)))
        {
            report.Findings.Add($"LIMIT: {capability.Capability}: {capability.SegmentCore}.");
        }
    }

    private static void AddComparisonFinding(
        SegmentCoreLimitsReport report,
        string scenario,
        bool higherIsBetter,
        string label)
    {
        var segment = report.Metrics
            .Where(metric => metric.Scenario == scenario && metric.Engine.StartsWith("SegmentCore", StringComparison.Ordinal))
            .FirstOrDefault();
        var sqlite = report.Metrics
            .Where(metric => metric.Scenario == scenario && metric.Engine.StartsWith("SQLite", StringComparison.Ordinal))
            .FirstOrDefault();
        if (segment is null || sqlite is null)
        {
            return;
        }

        var ratio = higherIsBetter
            ? Ratio(segment.OperationsPerSecond, sqlite.OperationsPerSecond)
            : Ratio(sqlite.OperationsPerSecond, segment.OperationsPerSecond);
        var classification = ratio >= 1 ? "STRENGTH" : "WEAKNESS";
        report.Findings.Add(
            $"{classification}: {label}: SegmentCore {segment.OperationsPerSecond:N1} ops/s vs SQLite {sqlite.OperationsPerSecond:N1} ops/s ({ratio:N2}x relative)." );
    }

    private static SegmentCoreLimitMetric? FindMetric(
        SegmentCoreLimitsReport report,
        string scenario,
        string engine) => report.Metrics.FirstOrDefault(metric =>
            metric.Scenario == scenario && metric.Engine == engine);

    private static async Task WriteReportAsync(SegmentCoreLimitsReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.ChangeExtension(fullPath, ".md"),
            BuildMarkdown(report)).ConfigureAwait(false);
    }

    private static string BuildMarkdown(SegmentCoreLimitsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SegmentCore adversarial limits report");
        builder.AppendLine();
        builder.AppendLine($"- Execution and integrity: **{(report.Passed ? "PASS" : "FAIL")}** (this is not a production-admission result)");
        builder.AppendLine($"- Started: {report.StartedUtc:O}");
        builder.AppendLine($"- Runtime: {report.Runtime}");
        builder.AppendLine($"- OS: {report.OperatingSystem}");
        builder.AppendLine($"- Disk: {report.PhysicalDisk}");
        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }

        builder.AppendLine();
        builder.AppendLine("## Performance");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Engine | Operations | Ops/s | p50 ms | p95 ms | p99 ms | Allocated bytes |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var metric in report.Metrics)
        {
            builder.AppendLine(
                $"| {metric.Scenario} | {metric.Engine} | {metric.Operations:N0} | {metric.OperationsPerSecond:N1} | {metric.P50Milliseconds:N3} | {metric.P95Milliseconds:N3} | {metric.P99Milliseconds:N3} | {metric.AllocatedBytes:N0} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Storage growth");
        builder.AppendLine();
        builder.AppendLine("| Stage | Cycle | Engine | Live records | Storage bytes | Files | Storage/live-payload ratio |");
        builder.AppendLine("|---|---:|---|---:|---:|---:|---:|");
        foreach (var sample in report.Storage)
        {
            builder.AppendLine(
                $"| {sample.Stage} | {sample.Cycle} | {sample.Engine} | {sample.LiveRecords:N0} | {sample.StorageBytes:N0} | {sample.Files:N0} | {sample.StorageToLivePayloadRatio:N2}x |");
        }

        builder.AppendLine();
        builder.AppendLine("## Capability boundary");
        builder.AppendLine();
        builder.AppendLine("| Capability | SegmentCore | SQLite | Evidence |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var capability in report.Capabilities)
        {
            builder.AppendLine(
                $"| {capability.Capability} | {capability.SegmentCore} | {capability.Sqlite} | {capability.Evidence} |");
        }

        if (report.Failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Failures");
            builder.AppendLine();
            foreach (var failure in report.Failures)
            {
                builder.AppendLine($"- {failure}");
            }
        }

        return builder.ToString();
    }
}

internal sealed record SegmentCoreLimitsOptions
{
    public int Records { get; init; } = 2_000;
    public int PayloadBytes { get; init; } = 512;
    public int Concurrency { get; init; } = 16;
    public int Iterations { get; init; } = 3;
    public int ChurnCycles { get; init; } = 5;
    public int StripeCount { get; init; } = 8;
    public int CheckpointStallRecords { get; init; } = 50_000;
    public bool KeepData { get; init; }
    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "segment-core-limits",
        $"segment-core-limits-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public static SegmentCoreLimitsOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        var stripeCount = ParseInt(args, "--stripes", 8);
        if (stripeCount is < 1 or > 64 || !System.Numerics.BitOperations.IsPow2((uint)stripeCount))
        {
            throw new ArgumentOutOfRangeException("--stripes", "Stripe count must be a power of two from 1 through 64.");
        }

        return new SegmentCoreLimitsOptions
        {
            Records = ParseInt(args, "--records", quick ? 500 : 2_000),
            PayloadBytes = ParseInt(args, "--payload-bytes", 512),
            Concurrency = ParseInt(args, "--concurrency", quick ? 8 : 16),
            Iterations = ParseInt(args, "--iterations", quick ? 1 : 3),
            ChurnCycles = ParseInt(args, "--churn-cycles", quick ? 3 : 5),
            StripeCount = stripeCount,
            CheckpointStallRecords = ParseInt(args, "--checkpoint-records", quick ? 10_000 : 50_000),
            KeepData = HasFlag(args, "--keep-data"),
            OutputPath = PhaseTwoOptions.ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "segment-core-limits",
                $"segment-core-limits-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
        };
    }

    private static int ParseInt(string[] args, string name, int fallback)
    {
        var text = PhaseTwoOptions.ParseString(args, name);
        var value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record SegmentCoreLimitMetric(
    string Scenario,
    string Engine,
    int Operations,
    double WallMilliseconds,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long AllocatedBytes,
    string? Detail);

internal sealed record SegmentCoreStorageSample(
    string Stage,
    int Cycle,
    string Engine,
    int LiveRecords,
    long LivePayloadBytes,
    long StorageBytes,
    int Files,
    double StorageToLivePayloadRatio);

internal sealed record SegmentCoreCapability(
    string Capability,
    string SegmentCore,
    string Sqlite,
    string Evidence);

internal sealed record SegmentCoreLimitsReport
{
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Processor { get; init; }
    public required string PhysicalDisk { get; init; }
    public required SegmentCoreLimitsOptions Options { get; init; }
    public List<SegmentCoreLimitMetric> Metrics { get; init; } = [];
    public List<SegmentCoreStorageSample> Storage { get; init; } = [];
    public List<SegmentCoreCapability> Capabilities { get; init; } = [];
    public List<string> Findings { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    public bool Passed => Failures.Count == 0;
}
