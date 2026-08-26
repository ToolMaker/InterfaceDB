using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Idb.Storage.SegmentCore.ObjectLog;
using Idb.Storage.SegmentCore;
using Microsoft.Data.Sqlite;

internal static class SegmentCoreTransactionsRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = TransactionPerformanceOptions.Parse(args);
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbTransactionPerformance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new TransactionPerformanceReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Options = options
        };

        try
        {
            Console.WriteLine("InterfaceDB SegmentCore transaction performance suite");
            Console.WriteLine(
                $"Records: {options.Records:N0}; iterations: {options.Iterations}; "
                + $"batch sizes: {string.Join(", ", options.BatchSizes.Select(static size => size.ToString("N0", CultureInfo.InvariantCulture)))}; "
                + $"SegmentCore stripes: {options.StripeCount}; protocols: "
                + string.Join(", ", options.Protocols.Select(EngineName))
                + $"; durability: {options.DurabilityStrategy}");
            var records = Enumerable.Range(0, options.Records)
                .Select(index => GateRecord.Create(index, "transaction"))
                .ToArray();

            foreach (var batchSize in options.BatchSizes)
            {
                Console.WriteLine($"Atomic batch size {batchSize:N0}...");
                var segmentSamples = options.Protocols.ToDictionary(
                    static protocol => protocol,
                    _ => new List<TransactionPerformanceSample>(options.Iterations));
                var sqliteSamples = new List<TransactionPerformanceSample>(options.Iterations);
                for (var iteration = 0; iteration < options.Iterations; iteration++)
                {
                    var iterationRoot = Path.Combine(runRoot, $"batch-{batchSize}", $"iteration-{iteration}");
                    Directory.CreateDirectory(iterationRoot);
                    if ((iteration & 1) == 0)
                    {
                        foreach (var protocol in options.Protocols)
                        {
                            segmentSamples[protocol].Add(await MeasureSegmentCoreAsync(
                                Path.Combine(iterationRoot, ProtocolSlug(protocol)),
                                records,
                                batchSize,
                                explicitTransaction: true,
                                options.StripeCount,
                                protocol,
                                options.DurabilityStrategy).ConfigureAwait(false));
                        }

                        sqliteSamples.Add(await MeasureSqliteAsync(
                            Path.Combine(iterationRoot, "sqlite.db"),
                            records,
                            batchSize,
                            explicitTransaction: true).ConfigureAwait(false));
                    }
                    else
                    {
                        sqliteSamples.Add(await MeasureSqliteAsync(
                            Path.Combine(iterationRoot, "sqlite.db"),
                            records,
                            batchSize,
                            explicitTransaction: true).ConfigureAwait(false));
                        foreach (var protocol in options.Protocols.Reverse())
                        {
                            segmentSamples[protocol].Add(await MeasureSegmentCoreAsync(
                                Path.Combine(iterationRoot, ProtocolSlug(protocol)),
                                records,
                                batchSize,
                                explicitTransaction: true,
                                options.StripeCount,
                                protocol,
                                options.DurabilityStrategy).ConfigureAwait(false));
                        }
                    }
                }

                foreach (var protocol in options.Protocols)
                {
                    report.Metrics.Add(Summarize(
                        EngineName(protocol),
                        "Atomic transaction",
                        batchSize,
                        segmentSamples[protocol]));
                }

                report.Metrics.Add(Summarize("SQLite FULL/WAL", "Atomic transaction", batchSize, sqliteSamples));
            }

            var independentRecords = records.Take(Math.Min(records.Length, options.IndependentRecords)).ToArray();
            Console.WriteLine($"Independent autocommit writes ({independentRecords.Length:N0})...");
            var independentSegmentSamples = options.Protocols.ToDictionary(
                static protocol => protocol,
                _ => new List<TransactionPerformanceSample>(options.Iterations));
            var independentSqliteSamples = new List<TransactionPerformanceSample>(options.Iterations);
            for (var iteration = 0; iteration < options.Iterations; iteration++)
            {
                var iterationRoot = Path.Combine(runRoot, "autocommit", $"iteration-{iteration}");
                Directory.CreateDirectory(iterationRoot);
                foreach (var protocol in options.Protocols)
                {
                    independentSegmentSamples[protocol].Add(await MeasureSegmentCoreAsync(
                        Path.Combine(iterationRoot, ProtocolSlug(protocol)),
                        independentRecords,
                        batchSize: 1,
                        explicitTransaction: false,
                        options.StripeCount,
                        protocol,
                        options.DurabilityStrategy).ConfigureAwait(false));
                }

                independentSqliteSamples.Add(await MeasureSqliteAsync(
                    Path.Combine(iterationRoot, "sqlite.db"),
                    independentRecords,
                    batchSize: 1,
                    explicitTransaction: false).ConfigureAwait(false));
            }

            foreach (var protocol in options.Protocols)
            {
                report.Metrics.Add(Summarize(
                    EngineName(protocol),
                    "Independent autocommit",
                    1,
                    independentSegmentSamples[protocol]));
            }
            report.Metrics.Add(Summarize(
                "SQLite FULL/WAL",
                "Independent autocommit",
                1,
                independentSqliteSamples));
            await RunMaintenanceValidationAsync(runRoot, records, options, report).ConfigureAwait(false);
            await RunForcedTerminationValidationAsync(runRoot, report).ConfigureAwait(false);
            Analyze(report);
        }
        catch (Exception exception)
        {
            report.Failures.Add($"{exception.GetType().Name}: {exception.Message}");
            Console.WriteLine(exception);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
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

        PrintSummary(report);
        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (report.Failures.Count > 0)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunMaintenanceValidationAsync(
        string runRoot,
        IReadOnlyList<GateRecord> sourceRecords,
        TransactionPerformanceOptions benchmarkOptions,
        TransactionPerformanceReport report)
    {
        var records = sourceRecords.Take(Math.Min(5000, sourceRecords.Count)).ToArray();
        var root = Path.Combine(runRoot, "maintenance");
        var options = SegmentOptions(
            Math.Max(128, records.Length),
            benchmarkOptions.StripeCount,
            SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            benchmarkOptions.DurabilityStrategy) with
        {
            CheckpointGenerationsToKeep = 2,
            LogOptions = SegmentOptions(
                Math.Max(128, records.Length),
                benchmarkOptions.StripeCount,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog,
                benchmarkOptions.DurabilityStrategy).LogOptions with
            {
                MaxSegmentBytes = 256 * 1024
            }
        };
        await using var store = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
        await using (var transaction = await store.BeginTransactionAsync().ConfigureAwait(false))
        {
            foreach (var record in records)
            {
                await transaction.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        var firstFull = await store.CheckpointAsync().ConfigureAwait(false);
        var noOp = await store.CheckpointAsync().ConfigureAwait(false);
        if (noOp.Generation != firstFull.Generation)
        {
            throw new InvalidDataException("A checkpoint without new durable state published an unnecessary generation.");
        }

        var dirtyCount = Math.Max(1, records.Length / 100);
        async ValueTask WriteDirtySetAsync()
        {
            await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
            foreach (var record in records.Take(dirtyCount))
            {
                await transaction.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        await WriteDirtySetAsync().ConfigureAwait(false);
        var incremental = await store.CheckpointAsync().ConfigureAwait(false);
        var deltaFiles = Directory.EnumerateFiles(
                Path.Combine(root, "checkpoints"),
                "checkpoint-*.idbdlt",
                SearchOption.TopDirectoryOnly)
            .Count();
        SegmentCoreCheckpointResult? retainedBaselineRefresh = null;
        if (deltaFiles == 0)
        {
            // A store configured to retain multiple independent full generations can first
            // establish that fallback generation. The following dirty checkpoint must then
            // use the compact delta representation.
            retainedBaselineRefresh = incremental;
            await WriteDirtySetAsync().ConfigureAwait(false);
            incremental = await store.CheckpointAsync().ConfigureAwait(false);
            deltaFiles = Directory.EnumerateFiles(
                    Path.Combine(root, "checkpoints"),
                    "checkpoint-*.idbdlt",
                    SearchOption.TopDirectoryOnly)
                .Count();
        }

        if (deltaFiles == 0)
        {
            throw new InvalidDataException("Incremental checkpoint validation did not publish a delta generation.");
        }

        for (var revision = 1; revision <= 2; revision++)
        {
            await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
            foreach (var record in records)
            {
                await transaction.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        var compaction = await store.CompactAsync().ConfigureAwait(false);
        report.Findings.Add(
            $"Checkpoint maintenance ({records.Length:N0} live objects): first full {firstFull.Elapsed.TotalMilliseconds:N2} ms / {firstFull.Bytes / 1024d:N1} KiB; "
            + $"unchanged checkpoint reused generation {noOp.Generation} in {noOp.Elapsed.TotalMilliseconds:N2} ms; "
            + (retainedBaselineRefresh is { } baseline
                ? $"retained full fallback {baseline.Elapsed.TotalMilliseconds:N2} ms / {baseline.Bytes / 1024d:N1} KiB; "
                : string.Empty)
            + $"1% dirty delta {incremental.Elapsed.TotalMilliseconds:N2} ms / {incremental.Bytes / 1024d:N1} KiB.");
        report.Findings.Add(
            $"Copy-forward compaction ({records.Length:N0} live objects) took {compaction.Elapsed.TotalMilliseconds:N2} ms and reclaimed "
            + $"{compaction.BytesReclaimed / 1024d:N1} KiB ({compaction.BytesBefore / 1024d:N1} KiB to {compaction.BytesAfter / 1024d:N1} KiB) "
            + $"across {compaction.Slices:N0} bounded slices; longest exclusive slice {compaction.LongestExclusiveSlice.TotalMilliseconds:N2} ms "
            + $"(prepare {compaction.PreparationPause.TotalMilliseconds:N2}, copy {compaction.LongestCopySlice.TotalMilliseconds:N2}, "
            + $"checkpoint capture {compaction.LongestCheckpointCapturePause.TotalMilliseconds:N2}, finalize {compaction.FinalizationPause.TotalMilliseconds:N2} ms)." );
    }

    public static async Task RunCrashWriterAsync(string[] args)
    {
        var root = RequireArgument(args, "--root");
        var marker = RequireArgument(args, "--marker");
        var scenario = RequireArgument(args, "--scenario");
        IDurableFileSystem fileSystem = scenario.StartsWith("replace-", StringComparison.Ordinal)
            ? new CrashPauseDurableFileSystem(marker, scenario == "replace-after")
            : PlatformDurableFileSystem.Instance;
        var options = SegmentOptions(
            128,
            4,
            SegmentCoreTransactionProtocol.MutationWriteAheadLog,
            ObjectLogDurabilityStrategy.ExplicitFlush) with
        {
            DurableFileSystem = fileSystem,
            CheckpointGenerationsToKeep = 2,
            // These scenarios deliberately exercise the WAL replacement crash windows;
            // normal stores retain small WALs to avoid needless durable file replacement.
            WalRecycleMinimumReclaimBytes = 0
        };
        await using var store = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
        for (var index = 0; index < 128; index++)
        {
            var record = GateRecord.Create(index, "forced-termination");
            await transaction.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        if (scenario == "commit")
        {
            WriteMarker(marker);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }

        _ = await store.CheckpointAsync().ConfigureAwait(false);
        throw new InvalidOperationException("Crash checkpoint scenario did not pause at WAL replacement.");
    }

    private static async Task RunForcedTerminationValidationAsync(
        string runRoot,
        TransactionPerformanceReport report)
    {
        foreach (var scenario in new[] { "commit", "replace-before", "replace-after" })
        {
            var root = Path.Combine(runRoot, "forced-termination", scenario);
            var marker = Path.Combine(root, "crash-window.ready");
            Directory.CreateDirectory(root);
            using var process = StartCrashWriter(root, marker, scenario);
            var ready = await WaitForMarkerAsync(process, marker, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!ready)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                report.Failures.Add($"Forced-termination scenario '{scenario}' did not reach its crash window.");
                continue;
            }

            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try
            {
                var options = SegmentOptions(
                    128,
                    4,
                    SegmentCoreTransactionProtocol.MutationWriteAheadLog,
                    ObjectLogDurabilityStrategy.ExplicitFlush) with
                {
                    CheckpointGenerationsToKeep = 2
                };
                await using var reopened = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
                var records = await reopened.ReadAllAsync().ConfigureAwait(false);
                if (records.Count != 128)
                {
                    throw new InvalidDataException(
                        $"Recovered {records.Count:N0} records; expected 128.");
                }

                report.Findings.Add(
                    $"Forced process termination at '{scenario}' recovered all 128 committed objects: PASS.");
            }
            catch (Exception exception)
            {
                report.Failures.Add(
                    $"Forced-termination scenario '{scenario}' failed recovery: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static Process StartCrashWriter(string root, string marker, string scenario)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve process path.");
        var start = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in new[]
                 {
                     "--segment-core-crash-writer", "--root", root,
                     "--marker", marker, "--scenario", scenario
                 })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start crash writer.");
    }

    private static async Task<bool> WaitForMarkerAsync(Process process, string marker, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(marker))
            {
                return true;
            }

            if (process.HasExited)
            {
                return false;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string RequireArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        throw new ArgumentException($"Missing required argument {name}.");
    }

    private static void WriteMarker(string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(marker))!);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        using var stream = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Flush(flushToDisk: true);
    }

    private sealed class CrashPauseDurableFileSystem : IDurableFileSystem
    {
        private readonly string _marker;
        private readonly bool _afterReplace;

        public CrashPauseDurableFileSystem(string marker, bool afterReplace)
        {
            _marker = marker;
            _afterReplace = afterReplace;
        }

        public string DirectoryFlushStrategy => PlatformDurableFileSystem.Instance.DirectoryFlushStrategy;

        public void FlushFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
            PlatformDurableFileSystem.Instance.FlushFile(handle);

        public void FlushDirectory(string directoryPath) =>
            PlatformDurableFileSystem.Instance.FlushDirectory(directoryPath);

        public void PublishFile(string temporaryPath, string finalPath) =>
            PlatformDurableFileSystem.Instance.PublishFile(temporaryPath, finalPath);

        public void ReplaceFile(string temporaryPath, string finalPath)
        {
            if (_afterReplace)
            {
                PlatformDurableFileSystem.Instance.ReplaceFile(temporaryPath, finalPath);
            }

            WriteMarker(_marker);
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static async Task<TransactionPerformanceSample> MeasureSegmentCoreAsync(
        string root,
        IReadOnlyList<GateRecord> records,
        int batchSize,
        bool explicitTransaction,
        int stripeCount,
        SegmentCoreTransactionProtocol protocol,
        ObjectLogDurabilityStrategy durabilityStrategy)
    {
        var options = SegmentOptions(Math.Max(64, batchSize), stripeCount, protocol, durabilityStrategy);
        var transactionCount = explicitTransaction
            ? (records.Count + batchSize - 1) / batchSize
            : records.Count;
        await using (var store = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false))
        {
            ForceCollection();
            var stopwatch = Stopwatch.StartNew();
            if (explicitTransaction)
            {
                for (var offset = 0; offset < records.Count; offset += batchSize)
                {
                    await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
                    var end = Math.Min(records.Count, offset + batchSize);
                    for (var index = offset; index < end; index++)
                    {
                        var record = records[index];
                        await transaction.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync().ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var record in records)
                {
                    await store.SaveAsync(record.Id, PhaseZeroCodec.Encode(record)).ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            var writeElapsed = stopwatch.Elapsed;
            if (store.Count != records.Count)
            {
                throw new InvalidDataException(
                    $"SegmentCore retained {store.Count:N0} records; expected {records.Count:N0}.");
            }

            var preCheckpointStorageBytes = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            var checkpoint = Stopwatch.StartNew();
            _ = await store.CheckpointAsync().ConfigureAwait(false);
            checkpoint.Stop();
            var storageBytes = Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Sum(static path => new FileInfo(path).Length);
            await store.DisposeAsync().ConfigureAwait(false);
            var reopen = Stopwatch.StartNew();
            await using var reopened = await SegmentCoreStore.OpenAsync(root, options).ConfigureAwait(false);
            reopen.Stop();
            var materialization = Stopwatch.StartNew();
            long checksum = 0;
            foreach (var item in await reopened.ReadAllAsync().ConfigureAwait(false))
            {
                checksum += PhaseZeroCodec.Decode(item.Payload).MaterializedChecksum;
            }

            materialization.Stop();
            RequireChecksum("SegmentCore", records, checksum);
            return new TransactionPerformanceSample(
                writeElapsed.TotalMilliseconds,
                checkpoint.Elapsed.TotalMilliseconds,
                reopen.Elapsed.TotalMilliseconds,
                materialization.Elapsed.TotalMilliseconds,
                records.Count,
                transactionCount,
                preCheckpointStorageBytes,
                storageBytes,
                checksum);
        }
    }

    private static async Task<TransactionPerformanceSample> MeasureSqliteAsync(
        string databasePath,
        IReadOnlyList<GateRecord> records,
        int batchSize,
        bool explicitTransaction)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        var transactionCount = explicitTransaction
            ? (records.Count + batchSize - 1) / batchSize
            : records.Count;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using (var initialize = connection.CreateCommand())
            {
                initialize.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    PRAGMA wal_autocheckpoint=0;
                    CREATE TABLE Records(
                        Id TEXT NOT NULL PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Age INTEGER NOT NULL,
                        Role TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        Revision INTEGER NOT NULL
                    );
                    """;
                await initialize.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Records(Id, Name, Age, Role, Content, Revision)
                VALUES($id, $name, $age, $role, $content, $revision)
                """;
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var age = insert.Parameters.Add("$age", SqliteType.Integer);
            var role = insert.Parameters.Add("$role", SqliteType.Text);
            var content = insert.Parameters.Add("$content", SqliteType.Text);
            var revision = insert.Parameters.Add("$revision", SqliteType.Integer);
            await insert.PrepareAsync().ConfigureAwait(false);

            ForceCollection();
            var stopwatch = Stopwatch.StartNew();
            if (explicitTransaction)
            {
                for (var offset = 0; offset < records.Count; offset += batchSize)
                {
                    await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
                    insert.Transaction = (SqliteTransaction)transaction;
                    var end = Math.Min(records.Count, offset + batchSize);
                    for (var index = offset; index < end; index++)
                    {
                        Bind(records[index]);
                        _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    await transaction.CommitAsync().ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var record in records)
                {
                    Bind(record);
                    _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            insert.Transaction = null;
            var writeElapsed = stopwatch.Elapsed;
            var preCheckpointStorageBytes = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
                .Where(File.Exists)
                .Sum(static path => new FileInfo(path).Length);
            var checkpointElapsed = Stopwatch.StartNew();
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            checkpointElapsed.Stop();

            var storageBytes = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
                .Where(File.Exists)
                .Sum(static path => new FileInfo(path).Length);
            await connection.CloseAsync().ConfigureAwait(false);

            var reopen = Stopwatch.StartNew();
            await using var reopened = new SqliteConnection(connectionString);
            await reopened.OpenAsync().ConfigureAwait(false);
            reopen.Stop();
            var materialization = Stopwatch.StartNew();
            await using var select = reopened.CreateCommand();
            select.CommandText = "SELECT Id, Name, Age, Role, Content, Revision FROM Records";
            await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
            var count = 0;
            long checksum = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var record = new GateRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5));
                checksum += record.MaterializedChecksum;
                count++;
            }

            materialization.Stop();
            if (count != records.Count)
            {
                throw new InvalidDataException($"SQLite retained {count:N0} records; expected {records.Count:N0}.");
            }

            RequireChecksum("SQLite", records, checksum);

            return new TransactionPerformanceSample(
                writeElapsed.TotalMilliseconds,
                checkpointElapsed.Elapsed.TotalMilliseconds,
                reopen.Elapsed.TotalMilliseconds,
                materialization.Elapsed.TotalMilliseconds,
                records.Count,
                transactionCount,
                preCheckpointStorageBytes,
                storageBytes,
                checksum);

            void Bind(GateRecord record)
            {
                id.Value = record.Id;
                name.Value = record.Name;
                age.Value = record.Age;
                role.Value = record.Role;
                content.Value = record.Content;
                revision.Value = record.Revision;
            }
        }
    }

    private static SegmentCoreOptions SegmentOptions(
        int maximumBatchRecords,
        int stripeCount,
        SegmentCoreTransactionProtocol protocol,
        ObjectLogDurabilityStrategy durabilityStrategy) => new()
    {
        CheckpointGenerationsToKeep = 2,
        TransactionProtocol = protocol,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = stripeCount,
            MaximumBatchRecords = Math.Min(16_384, maximumBatchRecords),
            QueueCapacityPerStripe = 16_384,
            MaxSegmentBytes = 64L * 1024 * 1024,
            DurabilityStrategy = durabilityStrategy
        }
    };

    private static void RequireChecksum(
        string engine,
        IReadOnlyList<GateRecord> records,
        long actualChecksum)
    {
        var expectedChecksum = records.Sum(static record => record.MaterializedChecksum);
        if (actualChecksum != expectedChecksum)
        {
            throw new InvalidDataException(
                $"{engine} materialized checksum {actualChecksum:N0}; expected {expectedChecksum:N0}.");
        }
    }

    private static TransactionPerformanceMetric Summarize(
        string engine,
        string operation,
        int batchSize,
        IReadOnlyList<TransactionPerformanceSample> samples)
    {
        var orderedWrites = samples.Select(static sample => sample.WriteMilliseconds).Order().ToArray();
        var orderedReopen = samples.Select(static sample => sample.ReopenMilliseconds).Order().ToArray();
        var orderedCheckpoint = samples.Select(static sample => sample.CheckpointMilliseconds).Order().ToArray();
        var orderedMaterialization = samples.Select(static sample => sample.MaterializationMilliseconds).Order().ToArray();
        var medianWrite = Percentile(orderedWrites, 0.5);
        var records = samples[0].Records;
        var transactions = samples[0].Transactions;
        return new TransactionPerformanceMetric(
            engine,
            operation,
            batchSize,
            records,
            samples.Count,
            medianWrite,
            Percentile(orderedWrites, 0.95),
            records / (medianWrite / 1000d),
            transactions / (medianWrite / 1000d),
            Percentile(orderedCheckpoint, 0.5),
            records / ((medianWrite + Percentile(orderedCheckpoint, 0.5)) / 1000d),
            Percentile(orderedReopen, 0.5),
            Percentile(orderedMaterialization, 0.5),
            (long)samples.Select(static sample => sample.PreCheckpointStorageBytes).Order().ElementAt(samples.Count / 2),
            (long)samples.Select(static sample => sample.StorageBytes).Order().ElementAt(samples.Count / 2),
            samples[0].MaterializedChecksum);
    }

    private static void Analyze(TransactionPerformanceReport report)
    {
        foreach (var batch in report.Options.BatchSizes)
        {
            var sqlite = report.Metrics.Single(metric =>
                metric.Engine == "SQLite FULL/WAL" && metric.Operation == "Atomic transaction" && metric.BatchSize == batch);
            foreach (var protocol in report.Options.Protocols)
            {
                var segment = report.Metrics.Single(metric =>
                    metric.Engine == EngineName(protocol)
                    && metric.Operation == "Atomic transaction"
                    && metric.BatchSize == batch);
                report.Findings.Add(
                    $"Atomic batch {batch:N0}: {EngineName(protocol)} is "
                    + $"{RatioPhrase(segment.RecordsPerSecond, sqlite.RecordsPerSecond)} SQLite by record throughput.");
                report.Findings.Add(
                    $"Atomic batch {batch:N0} including one maintenance checkpoint after "
                    + $"{segment.Records:N0} records: {EngineName(protocol)} is "
                    + $"{RatioPhrase(segment.WritePlusCheckpointRecordsPerSecond, sqlite.WritePlusCheckpointRecordsPerSecond)} SQLite.");
            }
        }

        var sqliteAuto = report.Metrics.Single(metric =>
            metric.Engine == "SQLite FULL/WAL" && metric.Operation == "Independent autocommit");
        foreach (var protocol in report.Options.Protocols)
        {
            var segmentAuto = report.Metrics.Single(metric =>
                metric.Engine == EngineName(protocol) && metric.Operation == "Independent autocommit");
            report.Findings.Add(
                $"Independent autocommit: {EngineName(protocol)} is "
                + $"{RatioPhrase(segmentAuto.RecordsPerSecond, sqliteAuto.RecordsPerSecond)} SQLite.");
        }

        if (report.Options.Protocols.Contains(SegmentCoreTransactionProtocol.ParticipantCommit)
            && report.Options.Protocols.Contains(SegmentCoreTransactionProtocol.MutationWriteAheadLog))
        {
            foreach (var batch in report.Options.BatchSizes)
            {
                var participant = report.Metrics.Single(metric =>
                    metric.Engine == EngineName(SegmentCoreTransactionProtocol.ParticipantCommit)
                    && metric.Operation == "Atomic transaction"
                    && metric.BatchSize == batch);
                var wal = report.Metrics.Single(metric =>
                    metric.Engine == EngineName(SegmentCoreTransactionProtocol.MutationWriteAheadLog)
                    && metric.Operation == "Atomic transaction"
                    && metric.BatchSize == batch);
                report.Findings.Add(
                    $"Atomic batch {batch:N0}: mutation WAL is "
                    + $"{RatioPhrase(wal.RecordsPerSecond, participant.RecordsPerSecond)} participant commit.");
            }
        }
        report.Findings.Add(
            "Reopen follows an out-of-band SegmentCore index checkpoint / SQLite WAL checkpoint; materialization reads complete objects, not raw scalar values.");
        report.Findings.Add(
            "Write timing includes object encoding/binding and durable commit acknowledgement; maintenance checkpoints are outside write latency for both engines.");
    }

    private static string EngineName(SegmentCoreTransactionProtocol protocol) => protocol switch
    {
        SegmentCoreTransactionProtocol.ParticipantCommit => "SegmentCore participant",
        SegmentCoreTransactionProtocol.MutationWriteAheadLog => "SegmentCore mutation WAL",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static string ProtocolSlug(SegmentCoreTransactionProtocol protocol) => protocol switch
    {
        SegmentCoreTransactionProtocol.ParticipantCommit => "segment-participant",
        SegmentCoreTransactionProtocol.MutationWriteAheadLog => "segment-mutation-wal",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static string RatioPhrase(double left, double right)
    {
        var ratio = left / right;
        return ratio >= 1
            ? $"{ratio:F2}x faster than"
            : $"{1 / ratio:F2}x slower than";
    }

    private static void PrintSummary(TransactionPerformanceReport report)
    {
        Console.WriteLine();
        Console.WriteLine("Median results");
        Console.WriteLine("Engine                    Operation               Batch      records/s    +check/s       tx/s   checkpoint    reopen   objects");
        foreach (var metric in report.Metrics)
        {
            Console.WriteLine(
                $"{metric.Engine,-25} {metric.Operation,-23} {metric.BatchSize,6:N0} "
                + $"{metric.RecordsPerSecond,14:N0} {metric.WritePlusCheckpointRecordsPerSecond,11:N0} "
                + $"{metric.TransactionsPerSecond,10:N0} "
                + $"{metric.CheckpointP50Milliseconds,10:N2} ms {metric.ReopenP50Milliseconds,8:N2} ms "
                + $"{metric.MaterializationP50Milliseconds,8:N2} ms");
        }

        Console.WriteLine();
        foreach (var finding in report.Findings)
        {
            Console.WriteLine($"- {finding}");
        }
    }

    private static async Task WriteReportAsync(TransactionPerformanceReport report, string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        var markdownPath = Path.ChangeExtension(fullPath, ".md");
        var markdown = new StringBuilder()
            .AppendLine("# SegmentCore transaction performance")
            .AppendLine()
            .AppendLine($"Run: {report.StartedUtc:O} on {report.Runtime}")
            .AppendLine()
            .AppendLine("| Engine | Operation | Batch | Records/s | Records/s including one checkpoint | Transactions/s | Write p50 | Write p95 | Checkpoint p50 | Reopen p50 | Full object materialization p50 | Storage before checkpoint | Storage after checkpoint |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var metric in report.Metrics)
        {
            markdown.AppendLine(
                $"| {metric.Engine} | {metric.Operation} | {metric.BatchSize:N0} | {metric.RecordsPerSecond:N0} | "
                + $"{metric.WritePlusCheckpointRecordsPerSecond:N0} | {metric.TransactionsPerSecond:N0} | "
                + $"{metric.WriteP50Milliseconds:N2} ms | "
                + $"{metric.WriteP95Milliseconds:N2} ms | {metric.CheckpointP50Milliseconds:N2} ms | "
                + $"{metric.ReopenP50Milliseconds:N2} ms | "
                + $"{metric.MaterializationP50Milliseconds:N2} ms | "
                + $"{metric.PreCheckpointStorageBytes / 1024d:N1} KiB | "
                + $"{metric.StorageBytes / 1024d:N1} KiB |");
        }

        markdown.AppendLine().AppendLine("## Findings").AppendLine();
        foreach (var finding in report.Findings)
        {
            markdown.AppendLine($"- {finding}");
        }

        await File.WriteAllTextAsync(markdownPath, markdown.ToString()).ConfigureAwait(false);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        var rank = percentile * (ordered.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + ((ordered[upper] - ordered[lower]) * (rank - lower));
    }

    private static void ForceCollection()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static void TryDeleteRunRoot(string runRoot)
    {
        var fullRoot = Path.GetFullPath(runRoot);
        var expectedParent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbTransactionPerformance")) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected benchmark path '{fullRoot}'.");
        }

        try
        {
            Directory.Delete(fullRoot, recursive: true);
        }
        catch
        {
            // Benchmark cleanup must not hide measurements.
        }
    }
}

internal sealed record TransactionPerformanceOptions
{
    public int Records { get; init; } = 5_000;

    public int IndependentRecords { get; init; } = 1_000;

    public int Iterations { get; init; } = 3;

    public int StripeCount { get; init; } = 8;

    public SegmentCoreTransactionProtocol[] Protocols { get; init; } =
    [
        SegmentCoreTransactionProtocol.ParticipantCommit,
        SegmentCoreTransactionProtocol.MutationWriteAheadLog
    ];

    public ObjectLogDurabilityStrategy DurabilityStrategy { get; init; } =
        ObjectLogDurabilityStrategy.ExplicitFlush;

    public int[] BatchSizes { get; init; } = [1, 10, 100, 1_000];

    public string OutputPath { get; init; } = Path.Combine(
        "artifacts",
        "segment-core-transactions",
        $"segment-core-transactions-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

    public bool KeepData { get; init; }

    public static TransactionPerformanceOptions Parse(string[] args)
    {
        var quick = args.Any(argument => string.Equals(argument, "--quick", StringComparison.OrdinalIgnoreCase));
        return new TransactionPerformanceOptions
        {
            Records = ParsePositiveInt(args, "--records", quick ? 1_000 : 5_000),
            IndependentRecords = ParsePositiveInt(args, "--independent-records", quick ? 250 : 1_000),
            Iterations = ParsePositiveInt(args, "--iterations", quick ? 2 : 3),
            StripeCount = ParseStripeCount(args),
            Protocols = ParseProtocols(args),
            DurabilityStrategy = ParseDurability(args),
            BatchSizes = ParseBatchSizes(args, quick ? [1, 10, 100] : [1, 10, 100, 1_000]),
            OutputPath = ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "segment-core-transactions",
                $"segment-core-transactions-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
            KeepData = args.Any(argument => string.Equals(argument, "--keep-data", StringComparison.OrdinalIgnoreCase))
        };
    }

    private static int ParsePositiveInt(string[] args, string name, int fallback)
    {
        var value = ParseString(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }

    private static int[] ParseBatchSizes(string[] args, int[] fallback)
    {
        var value = ParseString(args, "--batch-sizes");
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0)
            .Where(static parsed => parsed > 0)
            .Distinct()
            .Order()
            .ToArray();
        return values.Length == 0 ? fallback : values;
    }

    private static int ParseStripeCount(string[] args)
    {
        var parsed = ParsePositiveInt(args, "--stripes", 8);
        return parsed is >= 1 and <= 64 && System.Numerics.BitOperations.IsPow2((uint)parsed)
            ? parsed
            : 8;
    }

    private static SegmentCoreTransactionProtocol[] ParseProtocols(string[] args)
    {
        var value = ParseString(args, "--protocols")?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "both", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                SegmentCoreTransactionProtocol.ParticipantCommit,
                SegmentCoreTransactionProtocol.MutationWriteAheadLog
            ];
        }

        if (string.Equals(value, "participant", StringComparison.OrdinalIgnoreCase))
        {
            return [SegmentCoreTransactionProtocol.ParticipantCommit];
        }

        if (string.Equals(value, "wal", StringComparison.OrdinalIgnoreCase))
        {
            return [SegmentCoreTransactionProtocol.MutationWriteAheadLog];
        }

        throw new ArgumentException("--protocols must be 'both', 'participant', or 'wal'.");
    }

    private static ObjectLogDurabilityStrategy ParseDurability(string[] args)
    {
        var value = ParseString(args, "--durability")?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "explicit", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectLogDurabilityStrategy.ExplicitFlush;
        }

        if (string.Equals(value, "write-through", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectLogDurabilityStrategy.WriteThrough;
        }

        throw new ArgumentException("--durability must be 'explicit' or 'write-through'.");
    }

    private static string? ParseString(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(name.Length + 1)..];
            }

            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal readonly record struct TransactionPerformanceSample(
    double WriteMilliseconds,
    double CheckpointMilliseconds,
    double ReopenMilliseconds,
    double MaterializationMilliseconds,
    int Records,
    int Transactions,
    long PreCheckpointStorageBytes,
    long StorageBytes,
    long MaterializedChecksum);

internal sealed record TransactionPerformanceMetric(
    string Engine,
    string Operation,
    int BatchSize,
    int Records,
    int Iterations,
    double WriteP50Milliseconds,
    double WriteP95Milliseconds,
    double RecordsPerSecond,
    double TransactionsPerSecond,
    double CheckpointP50Milliseconds,
    double WritePlusCheckpointRecordsPerSecond,
    double ReopenP50Milliseconds,
    double MaterializationP50Milliseconds,
    long PreCheckpointStorageBytes,
    long StorageBytes,
    long MaterializedChecksum);

internal sealed record TransactionPerformanceReport
{
    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset CompletedUtc { get; set; }

    public required string Runtime { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Processor { get; init; }

    public required TransactionPerformanceOptions Options { get; init; }

    public List<TransactionPerformanceMetric> Metrics { get; } = [];

    public List<string> Findings { get; } = [];

    public List<string> Failures { get; } = [];
}
