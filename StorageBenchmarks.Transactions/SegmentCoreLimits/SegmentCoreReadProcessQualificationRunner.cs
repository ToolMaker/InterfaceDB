using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Data.Sqlite;

internal static class SegmentCoreReadProcessQualificationRunner
{
    private const string SegmentCoreMeterName = "InterfaceDB.Storage.SegmentCore";
    private const string OpenStageInstrumentName = "interfacedb.segmentcore.open.stage.duration";
    private const string FullMaterializationContract = "GateRecord/PhaseZeroCodec-v1/all-fields";
    private const string ParallelReadersQualification = "all-processes-materialize";
    private const string SingleOwnerQualification = "one-materialized-reader-plus-ownership-rejections";
    private const string MaterializedOutcome = "materialized";
    private const string OwnershipRejectedOutcome = "ownership-rejected";
    private const string FailedOutcome = "failed";
    private const int MinimumPayloadBytes = 77;
    private static readonly TimeSpan WorkerBarrierTimeout = TimeSpan.FromSeconds(30);
    private const int PhaseZeroFixedPayloadBytes =
        sizeof(int) // codec version
        + (4 * sizeof(int)) // Id, Name, Role, and Content byte lengths
        + sizeof(int) // Age
        + sizeof(long); // Revision
    private const ulong FnvOffsetBasis = 14_695_981_039_346_656_037UL;
    private const ulong FnvPrime = 1_099_511_628_211UL;

    public static async Task RunAsync(string[] args)
    {
        var options = ReadProcessOptions.Parse(args);
        var root = Path.Combine(
            Path.GetTempPath(),
            "InterfaceDbReadProcessQualification",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new ReadProcessReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Options = options
        };

        try
        {
            var segmentRoot = Path.Combine(root, "segment-core");
            var sqlitePath = Path.Combine(root, "sqlite.db");
            await SeedSegmentCoreAsync(segmentRoot, options).ConfigureAwait(false);
            await SeedSqliteAsync(sqlitePath, options).ConfigureAwait(false);

            report.FreshProcessSegmentCore = await RunChildAsync(
                root,
                "segment-core",
                segmentRoot,
                0,
                options.ReadsPerProcess,
                options.Records,
                options.PayloadBytes,
                captureOpenStages: true).ConfigureAwait(false);
            report.FreshProcessSqlite = await RunChildAsync(
                root,
                "sqlite",
                sqlitePath,
                0,
                options.ReadsPerProcess,
                options.Records,
                options.PayloadBytes).ConfigureAwait(false);

            report.MultiProcessSegmentCore = await RunChildrenAsync(
                root,
                "segment-core",
                segmentRoot,
                options.Processes,
                options.ReadsPerProcess,
                options.Records,
                options.PayloadBytes)
                .ConfigureAwait(false);
            report.MultiProcessSqlite = await RunChildrenAsync(
                root,
                "sqlite",
                sqlitePath,
                options.Processes,
                options.ReadsPerProcess,
                options.Records,
                options.PayloadBytes)
                .ConfigureAwait(false);
            report.PostContentionSegmentCore = await RunChildAsync(
                root,
                "segment-core",
                segmentRoot,
                options.Processes + 1,
                options.ReadsPerProcess,
                options.Records,
                options.PayloadBytes)
                .ConfigureAwait(false);

            EnsurePassed("Fresh process SegmentCore", report.FreshProcessSegmentCore);
            EnsurePassed("Fresh process SQLite", report.FreshProcessSqlite);
            EnsurePassed("Multi-process SegmentCore", report.MultiProcessSegmentCore);
            EnsurePassed("Multi-process SQLite", report.MultiProcessSqlite);
            EnsurePassed("Post-contention SegmentCore", report.PostContentionSegmentCore);
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            await WriteJsonAsync(options.OutputPath, report).ConfigureAwait(false);
            TryDeleteOwnedRoot(root);
        }

        Console.WriteLine("SegmentCore/SQLite fresh-process and multi-process read qualification complete.");
        Print("Fresh process SegmentCore", report.FreshProcessSegmentCore);
        Print("Fresh process SQLite", report.FreshProcessSqlite);
        Print("Multi-process SegmentCore", report.MultiProcessSegmentCore);
        Print("Multi-process SQLite", report.MultiProcessSqlite);
        Print("Post-contention SegmentCore", report.PostContentionSegmentCore);
        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
    }

    public static async Task RunWorkerAsync(string[] args)
    {
        var engine = Required(args, "--engine");
        var root = Path.GetFullPath(Required(args, "--root"));
        var output = Path.GetFullPath(Required(args, "--output"));
        var reads = ParseInt(args, "--reads", 10_000, 1, 10_000_000);
        var records = ParseInt(args, "--records", 100_000, 1, 10_000_000);
        var payloadBytes = ParseInt(args, "--payload-bytes", 256, MinimumPayloadBytes, 1024 * 1024);
        var processIndex = ParseInt(args, "--process-index", 0, 0, 1_000);
        var captureOpenStages = args.Contains("--capture-open-stages", StringComparer.OrdinalIgnoreCase);
        var synchronizationRoot = PhaseTwoOptions.ParseString(args, "--synchronization-root");
        var synchronizedWorkers = synchronizationRoot is null
            ? 0
            : ParseInt(args, "--synchronized-workers", 0, 2, 32);
        var result = new ReadProcessResult
        {
            Engine = engine,
            ProcessIndex = processIndex,
            Reads = reads,
            MaterializationContract = FullMaterializationContract,
            Outcome = FailedOutcome
        };

        try
        {
            if (synchronizationRoot is not null)
            {
                await ArriveAndWaitForReleaseAsync(
                    synchronizationRoot,
                    processIndex,
                    synchronizedWorkers).ConfigureAwait(false);
            }

            if (string.Equals(engine, "segment-core", StringComparison.OrdinalIgnoreCase))
            {
                await ReadSegmentCoreAsync(
                    root,
                    reads,
                    processIndex,
                    records,
                    payloadBytes,
                    captureOpenStages,
                    result,
                    synchronizationRoot,
                    synchronizedWorkers).ConfigureAwait(false);
            }
            else if (string.Equals(engine, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await ReadSqliteAsync(
                    root,
                    reads,
                    processIndex,
                    records,
                    payloadBytes,
                    result,
                    synchronizationRoot,
                    synchronizedWorkers).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentOutOfRangeException("--engine", engine, "Expected segment-core or sqlite.");
            }

            result.Outcome = MaterializedOutcome;
            result.Passed = true;
        }
        catch (Exception exception)
        {
            SignalOpenAttempt(synchronizationRoot, processIndex);
            result.ExceptionType = exception.GetType().FullName;
            result.Outcome = IsOwnershipRejection(engine, exception)
                ? OwnershipRejectedOutcome
                : FailedOutcome;
            result.Error = $"{exception.GetType().Name}: {exception.Message}";
            Environment.ExitCode = 1;
        }
        finally
        {
            await WriteJsonAsync(output, result).ConfigureAwait(false);
        }
    }

    private static async Task SeedSegmentCoreAsync(string root, ReadProcessOptions options)
    {
        await using var store = await SegmentCoreStore.OpenAsync(root, StoreOptions()).ConfigureAwait(false);
        for (var first = 0; first < options.Records; first += options.BatchSize)
        {
            await using var transaction = await store.BeginTransactionAsync().ConfigureAwait(false);
            var end = Math.Min(options.Records, first + options.BatchSize);
            for (var ordinal = first; ordinal < end; ordinal++)
            {
                await transaction.SaveAsync(Key(ordinal), Payload(ordinal, options.PayloadBytes)).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        _ = await store.CheckpointAsync().ConfigureAwait(false);
    }

    private static async Task SeedSqliteAsync(string path, ReadProcessOptions options)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var initialize = connection.CreateCommand())
        {
            initialize.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE Records(Id TEXT NOT NULL PRIMARY KEY, Payload BLOB NOT NULL);
                """;
            _ = await initialize.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO Records(Id, Payload) VALUES($id, $payload)";
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var payload = insert.Parameters.Add("$payload", SqliteType.Blob);
        await insert.PrepareAsync().ConfigureAwait(false);
        for (var first = 0; first < options.Records; first += options.BatchSize)
        {
            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            insert.Transaction = (SqliteTransaction)transaction;
            var end = Math.Min(options.Records, first + options.BatchSize);
            for (var ordinal = first; ordinal < end; ordinal++)
            {
                id.Value = Key(ordinal);
                payload.Value = Payload(ordinal, options.PayloadBytes);
                _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
        }

        insert.Transaction = null;
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        _ = await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ReadSegmentCoreAsync(
        string root,
        int reads,
        int processIndex,
        int records,
        int payloadBytes,
        bool captureOpenStages,
        ReadProcessResult result,
        string? synchronizationRoot,
        int synchronizedWorkers)
    {
        using var stageCollector = captureOpenStages ? new OpenStageCollector() : null;
        ForceGc();
        var openAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var open = Stopwatch.StartNew();
        await using var store = await SegmentCoreStore.OpenAsync(root, StoreOptions()).ConfigureAwait(false);
        open.Stop();
        result.OpenMilliseconds = open.Elapsed.TotalMilliseconds;
        result.OpenAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - openAllocatedBefore;
        result.OpenStagesMilliseconds = stageCollector?.Snapshot();
        await SignalOpenAttemptAndWaitAsync(
            synchronizationRoot,
            processIndex,
            synchronizedWorkers).ConfigureAwait(false);
        ForceGc();
        var readAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var read = Stopwatch.StartNew();
        long checksum = 0;
        long materializedObjects = 0;
        for (var index = 0; index < reads; index++)
        {
            var ordinal = Ordinal(index, processIndex, records);
            var key = Key(ordinal);
            var payload = await store.GetAsync(key).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Missing SegmentCore key {ordinal:N0}.");
            var record = PhaseZeroCodec.Decode(payload);
            Validate(record, ordinal, payloadBytes, key);
            checksum = unchecked(checksum + MaterializedChecksum(record));
            materializedObjects++;
        }

        read.Stop();
        result.ReadMilliseconds = read.Elapsed.TotalMilliseconds;
        result.ReadAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - readAllocatedBefore;
        result.MaterializedObjects = materializedObjects;
        result.Checksum = checksum;
    }

    private static async Task ReadSqliteAsync(
        string path,
        int reads,
        int processIndex,
        int records,
        int payloadBytes,
        ReadProcessResult result,
        string? synchronizationRoot,
        int synchronizedWorkers)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        ForceGc();
        var openAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var open = Stopwatch.StartNew();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        open.Stop();
        result.OpenMilliseconds = open.Elapsed.TotalMilliseconds;
        result.OpenAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - openAllocatedBefore;
        await SignalOpenAttemptAndWaitAsync(
            synchronizationRoot,
            processIndex,
            synchronizedWorkers).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT Payload FROM Records WHERE Id = $id";
        var id = select.Parameters.Add("$id", SqliteType.Text);
        await select.PrepareAsync().ConfigureAwait(false);
        ForceGc();
        var readAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var read = Stopwatch.StartNew();
        long checksum = 0;
        long materializedObjects = 0;
        for (var index = 0; index < reads; index++)
        {
            var ordinal = Ordinal(index, processIndex, records);
            var key = Key(ordinal);
            id.Value = key;
            var value = await select.ExecuteScalarAsync().ConfigureAwait(false) as byte[]
                ?? throw new InvalidDataException($"Missing SQLite key {ordinal:N0}.");
            var record = PhaseZeroCodec.Decode(value);
            Validate(record, ordinal, payloadBytes, key);
            checksum = unchecked(checksum + MaterializedChecksum(record));
            materializedObjects++;
        }

        read.Stop();
        result.ReadMilliseconds = read.Elapsed.TotalMilliseconds;
        result.ReadAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - readAllocatedBefore;
        result.MaterializedObjects = materializedObjects;
        result.Checksum = checksum;
    }

    private static async Task<ReadProcessGroupResult> RunChildrenAsync(
        string parentRoot,
        string engine,
        string storeRoot,
        int processes,
        int reads,
        int records,
        int payloadBytes)
    {
        var synchronizationRoot = Path.Combine(
            parentRoot,
            "worker-synchronization",
            $"{engine}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(synchronizationRoot);
        var stopwatch = Stopwatch.StartNew();
        var childTasks = Enumerable.Range(0, processes)
            .Select(index => RunChildAsync(
                parentRoot,
                engine,
                storeRoot,
                index,
                reads,
                records,
                payloadBytes,
                applyParentInvariants: false,
                synchronizationRoot: synchronizationRoot,
                synchronizedWorkers: processes))
            .ToArray();
        await WaitForMarkerCountAsync(
            synchronizationRoot,
            "ready-*.marker",
            processes,
            "workers to become ready").ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(synchronizationRoot, "release.marker"),
            string.Empty).ConfigureAwait(false);
        var results = await Task.WhenAll(childTasks).ConfigureAwait(false);
        stopwatch.Stop();
        var singleOwnerQualification = IsSegmentCore(engine);
        for (var processIndex = 0; processIndex < results.Length; processIndex++)
        {
            var processResult = results[processIndex];
            if (singleOwnerQualification
                && string.Equals(processResult.Outcome, OwnershipRejectedOutcome, StringComparison.Ordinal))
            {
                ApplyOwnershipRejectionInvariants(processResult, engine, processIndex, reads);
            }
            else
            {
                ApplyParentInvariants(
                    processResult,
                    engine,
                    processIndex,
                    reads,
                    records,
                    payloadBytes);
            }
        }

        var successfulResults = results
            .Where(static item => item.Passed && item.QualificationPassed)
            .ToArray();
        var ownershipRejections = results.Count(static item =>
            item.QualificationPassed
            && string.Equals(item.Outcome, OwnershipRejectedOutcome, StringComparison.Ordinal));
        var result = new ReadProcessGroupResult
        {
            Engine = engine,
            MaterializationContract = FullMaterializationContract,
            QualificationContract = singleOwnerQualification
                ? SingleOwnerQualification
                : ParallelReadersQualification,
            ReportsAggregateReadThroughput = !singleOwnerQualification,
            Processes = processes,
            ExpectedSuccessfulProcesses = singleOwnerQualification ? 1 : processes,
            ExpectedOwnershipRejections = singleOwnerQualification ? processes - 1 : 0,
            SuccessfulProcesses = successfulResults.Length,
            OwnershipRejectedProcesses = ownershipRejections,
            QualifiedProcesses = results.Count(static item => item.QualificationPassed),
            FailedProcesses = results.Length - successfulResults.Length - ownershipRejections,
            SuccessfulReads = successfulResults.Sum(static item => item.Reads),
            MaterializedObjects = results.Sum(static result => result.MaterializedObjects),
            Checksum = AggregateChecksum(results),
            WallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Results = results
        };
        ApplyParentInvariants(result, engine, processes, reads, records, payloadBytes);
        return result;
    }

    private static async Task<ReadProcessResult> RunChildAsync(
        string parentRoot,
        string engine,
        string storeRoot,
        int processIndex,
        int reads,
        int records,
        int payloadBytes,
        bool captureOpenStages = false,
        bool applyParentInvariants = true,
        string? synchronizationRoot = null,
        int synchronizedWorkers = 0)
    {
        var outputDirectory = Path.Combine(parentRoot, "worker-results");
        Directory.CreateDirectory(outputDirectory);
        var output = Path.Combine(outputDirectory, $"{engine}-{processIndex}-{Guid.NewGuid():N}.json");
        var assembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot resolve the benchmark entry assembly.");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--segment-core-read-worker");
        start.ArgumentList.Add($"--engine={engine}");
        start.ArgumentList.Add($"--root={storeRoot}");
        start.ArgumentList.Add($"--reads={reads.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--records={records.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--payload-bytes={payloadBytes.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--process-index={processIndex.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--output={output}");
        if (synchronizationRoot is not null)
        {
            start.ArgumentList.Add($"--synchronization-root={synchronizationRoot}");
            start.ArgumentList.Add(
                $"--synchronized-workers={synchronizedWorkers.ToString(CultureInfo.InvariantCulture)}");
        }
        if (captureOpenStages)
        {
            start.ArgumentList.Add("--capture-open-stages");
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start a read worker process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var outputText = await stdout.ConfigureAwait(false);
        var errorText = await stderr.ConfigureAwait(false);
        if (File.Exists(output))
        {
            var result = JsonSerializer.Deserialize<ReadProcessResult>(await File.ReadAllTextAsync(output).ConfigureAwait(false))
                ?? throw new InvalidDataException($"Worker '{output}' produced an empty result.");
            result.ExitCode = process.ExitCode;
            result.ProcessOutput = string.Join(Environment.NewLine, new[] { outputText, errorText }
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
            if (applyParentInvariants)
                ApplyParentInvariants(result, engine, processIndex, reads, records, payloadBytes);
            return result;
        }

        var missingResult = new ReadProcessResult
        {
            Engine = engine,
            ProcessIndex = processIndex,
            Reads = reads,
            ExitCode = process.ExitCode,
            Error = "Worker exited without a result file.",
            ProcessOutput = string.Join(Environment.NewLine, outputText, errorText)
        };
        if (applyParentInvariants)
            ApplyParentInvariants(missingResult, engine, processIndex, reads, records, payloadBytes);
        return missingResult;
    }

    private static SegmentCoreOptions StoreOptions() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        PrimaryIndexOptions = new SegmentCorePrimaryIndexOptions
        {
            Mode = SegmentCorePrimaryIndexMode.Automatic,
            AutomaticPagingMinimumEntries = 100_000
        },
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 8,
            MaximumBatchRecords = 1_000,
            QueueCapacityPerStripe = 4_096,
            MaximumCombinedWriteBytes = 4 * 1024 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.WriteThrough
        }
    };

    private static int Ordinal(int read, int processIndex, int records) =>
        (int)(((long)read * 104_729 + processIndex * 7_919) % records);

    private static string Key(int ordinal) =>
        "read-" + ordinal.ToString("D10", CultureInfo.InvariantCulture);

    private static string Name(int ordinal) =>
        "read-record-" + ordinal.ToString("D10", CultureInfo.InvariantCulture);

    private static int Age(int ordinal) => 18 + (ordinal % 63);

    private static string Role(int ordinal) => (ordinal & 1) == 0 ? "student" : "employee";

    private static long Revision(int ordinal) => (ordinal * 17L) + 11L;

    private static byte[] Payload(int ordinal, int payloadBytes) =>
        PhaseZeroCodec.Encode(CreateRecord(ordinal, payloadBytes));

    private static GateRecord CreateRecord(int ordinal, int payloadBytes)
    {
        var id = Key(ordinal);
        var name = Name(ordinal);
        var role = Role(ordinal);
        var contentLength = ContentLength(payloadBytes, id, name, role);
        var content = string.Create(contentLength, ordinal, static (target, value) =>
        {
            for (var index = 0; index < target.Length; index++)
                target[index] = ContentCharacter(value, index);
        });
        return new GateRecord(id, name, Age(ordinal), role, content, Revision(ordinal));
    }

    private static int ContentLength(int payloadBytes, string id, string name, string role) =>
        Math.Max(0, payloadBytes - PhaseZeroFixedPayloadBytes - id.Length - name.Length - role.Length);

    private static char ContentCharacter(int ordinal, int index) =>
        (char)('a' + (int)(((long)ordinal * 31L + (long)index * 17L) % 26L));

    private static void Validate(GateRecord record, int ordinal, int payloadBytes, string expectedId)
    {
        if (!string.Equals(record.Id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException($"Record {ordinal:N0} has an invalid Id.");

        var expectedName = Name(ordinal);
        if (!string.Equals(record.Name, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException($"Record {ordinal:N0} has an invalid Name.");

        if (record.Age != Age(ordinal))
            throw new InvalidDataException($"Record {ordinal:N0} has an invalid Age.");

        var expectedRole = Role(ordinal);
        if (!string.Equals(record.Role, expectedRole, StringComparison.Ordinal))
            throw new InvalidDataException($"Record {ordinal:N0} has an invalid Role.");

        if (record.Revision != Revision(ordinal))
            throw new InvalidDataException($"Record {ordinal:N0} has an invalid Revision.");

        var expectedContentLength = ContentLength(payloadBytes, expectedId, expectedName, expectedRole);
        if (record.Content.Length != expectedContentLength)
        {
            throw new InvalidDataException(
                $"Record {ordinal:N0} has content length {record.Content.Length:N0}; "
                + $"expected {expectedContentLength:N0}.");
        }

        for (var index = 0; index < record.Content.Length; index++)
        {
            if (record.Content[index] != ContentCharacter(ordinal, index))
                throw new InvalidDataException($"Record {ordinal:N0} has corrupt Content at character {index:N0}.");
        }
    }

    private static long MaterializedChecksum(GateRecord record)
    {
        var hash = FnvOffsetBasis;
        hash = AppendString(hash, record.Id);
        hash = AppendString(hash, record.Name);
        hash = AppendInt64(hash, record.Age);
        hash = AppendString(hash, record.Role);
        hash = AppendString(hash, record.Content);
        hash = AppendInt64(hash, record.Revision);
        return unchecked((long)hash);
    }

    private static ulong AppendString(ulong hash, string value)
    {
        hash = AppendInt64(hash, value.Length);
        foreach (var character in value)
        {
            hash = AppendByte(hash, (byte)character);
            hash = AppendByte(hash, (byte)(character >> 8));
        }

        return hash;
    }

    private static ulong AppendInt64(ulong hash, long value)
    {
        var bits = unchecked((ulong)value);
        for (var index = 0; index < sizeof(long); index++)
        {
            hash = AppendByte(hash, (byte)bits);
            bits >>= 8;
        }

        return hash;
    }

    private static ulong AppendByte(ulong hash, byte value) => unchecked((hash ^ value) * FnvPrime);

    private static long ExpectedChecksum(int reads, int processIndex, int records, int payloadBytes)
    {
        long checksum = 0;
        for (var index = 0; index < reads; index++)
        {
            var ordinal = Ordinal(index, processIndex, records);
            checksum = unchecked(checksum + MaterializedChecksum(CreateRecord(ordinal, payloadBytes)));
        }

        return checksum;
    }

    private static long ExpectedGroupChecksum(int processes, int reads, int records, int payloadBytes)
    {
        long checksum = 0;
        for (var processIndex = 0; processIndex < processes; processIndex++)
            checksum = unchecked(checksum + ExpectedChecksum(reads, processIndex, records, payloadBytes));
        return checksum;
    }

    private static long ExpectedSuccessfulChecksum(
        IEnumerable<ReadProcessResult> results,
        int reads,
        int records,
        int payloadBytes)
    {
        long checksum = 0;
        foreach (var result in results.Where(static item => item.Passed && item.QualificationPassed))
        {
            checksum = unchecked(
                checksum + ExpectedChecksum(reads, result.ProcessIndex, records, payloadBytes));
        }

        return checksum;
    }

    private static long AggregateChecksum(IEnumerable<ReadProcessResult> results)
    {
        long checksum = 0;
        foreach (var result in results)
            checksum = unchecked(checksum + result.Checksum);
        return checksum;
    }

    private static bool IsSegmentCore(string engine) =>
        string.Equals(engine, "segment-core", StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnershipRejection(string engine, Exception exception)
    {
        if (!IsSegmentCore(engine) || exception is not IOException)
            return false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("mutation-wal.idbwal", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("writer.lock", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("already owns", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyParentInvariants(
        ReadProcessResult result,
        string engine,
        int processIndex,
        int reads,
        int records,
        int payloadBytes)
    {
        var workerPassed = result.Passed;
        var failures = new List<string>();
        if (result.ExitCode != 0)
            failures.Add($"exit code was {result.ExitCode}");
        if (!workerPassed)
            failures.Add("worker reported failure");
        if (!string.Equals(result.Outcome, MaterializedOutcome, StringComparison.Ordinal))
            failures.Add($"outcome was '{result.Outcome}', expected '{MaterializedOutcome}'");
        if (!string.Equals(result.Engine, engine, StringComparison.Ordinal))
            failures.Add($"engine was '{result.Engine}', expected '{engine}'");
        if (result.ProcessIndex != processIndex)
            failures.Add($"process index was {result.ProcessIndex}, expected {processIndex}");
        if (result.Reads != reads)
            failures.Add($"read count was {result.Reads:N0}, expected {reads:N0}");
        if (!string.Equals(result.MaterializationContract, FullMaterializationContract, StringComparison.Ordinal))
        {
            failures.Add(
                $"materialization contract was '{result.MaterializationContract}', "
                + $"expected '{FullMaterializationContract}'");
        }

        if (result.MaterializedObjects != reads)
        {
            failures.Add(
                $"materialized object count was {result.MaterializedObjects:N0}, expected {reads:N0}");
        }

        var expectedChecksum = ExpectedChecksum(reads, processIndex, records, payloadBytes);
        if (result.Checksum != expectedChecksum)
            failures.Add($"checksum was {result.Checksum}, expected {expectedChecksum}");

        result.Passed = failures.Count == 0;
        result.QualificationPassed = result.Passed;
        if (failures.Count > 0)
            result.Error = JoinErrors(result.Error, $"Parent invariant failure: {string.Join("; ", failures)}.");
    }

    private static void ApplyOwnershipRejectionInvariants(
        ReadProcessResult result,
        string engine,
        int processIndex,
        int reads)
    {
        var failures = new List<string>();
        if (!IsSegmentCore(engine) || !string.Equals(result.Engine, engine, StringComparison.Ordinal))
            failures.Add($"engine was '{result.Engine}', expected SegmentCore engine '{engine}'");
        if (result.ProcessIndex != processIndex)
            failures.Add($"process index was {result.ProcessIndex}, expected {processIndex}");
        if (result.Reads != reads)
            failures.Add($"read count was {result.Reads:N0}, expected {reads:N0}");
        if (result.ExitCode == 0)
            failures.Add("ownership rejection returned exit code 0");
        if (result.Passed)
            failures.Add("ownership-rejected worker reported a successful read outcome");
        if (!string.Equals(result.ExceptionType, typeof(IOException).FullName, StringComparison.Ordinal))
        {
            failures.Add(
                $"exception type was '{result.ExceptionType}', expected '{typeof(IOException).FullName}'");
        }

        if (!string.Equals(result.Outcome, OwnershipRejectedOutcome, StringComparison.Ordinal))
        {
            failures.Add(
                $"outcome was '{result.Outcome}', expected '{OwnershipRejectedOutcome}'");
        }

        if (!string.Equals(result.MaterializationContract, FullMaterializationContract, StringComparison.Ordinal))
        {
            failures.Add(
                $"materialization contract was '{result.MaterializationContract}', "
                + $"expected '{FullMaterializationContract}'");
        }

        if (result.MaterializedObjects != 0)
            failures.Add($"ownership-rejected worker materialized {result.MaterializedObjects:N0} objects");
        if (result.Checksum != 0)
            failures.Add($"ownership-rejected worker reported checksum {result.Checksum}, expected 0");

        result.QualificationPassed = failures.Count == 0;
        if (failures.Count > 0)
            result.Error = JoinErrors(result.Error, $"Parent ownership invariant failure: {string.Join("; ", failures)}.");
    }

    private static void ApplyParentInvariants(
        ReadProcessGroupResult result,
        string engine,
        int processes,
        int reads,
        int records,
        int payloadBytes)
    {
        var singleOwnerQualification = IsSegmentCore(engine);
        var expectedSuccessfulProcesses = singleOwnerQualification ? 1 : processes;
        var expectedOwnershipRejections = singleOwnerQualification ? processes - 1 : 0;
        var expectedQualificationContract = singleOwnerQualification
            ? SingleOwnerQualification
            : ParallelReadersQualification;
        var failures = new List<string>();
        if (!string.Equals(result.Engine, engine, StringComparison.Ordinal))
            failures.Add($"engine was '{result.Engine}', expected '{engine}'");
        if (!string.Equals(result.MaterializationContract, FullMaterializationContract, StringComparison.Ordinal))
        {
            failures.Add(
                $"materialization contract was '{result.MaterializationContract}', "
                + $"expected '{FullMaterializationContract}'");
        }

        if (!string.Equals(result.QualificationContract, expectedQualificationContract, StringComparison.Ordinal))
        {
            failures.Add(
                $"qualification contract was '{result.QualificationContract}', "
                + $"expected '{expectedQualificationContract}'");
        }

        if (result.ReportsAggregateReadThroughput == singleOwnerQualification)
        {
            failures.Add(
                $"aggregate-throughput reporting was {result.ReportsAggregateReadThroughput}, "
                + $"expected {!singleOwnerQualification}");
        }

        if (result.Processes != processes || result.Results.Length != processes)
        {
            failures.Add(
                $"process/result count was {result.Processes}/{result.Results.Length}, "
                + $"expected {processes}/{processes}");
        }

        if (result.ExpectedSuccessfulProcesses != expectedSuccessfulProcesses
            || result.SuccessfulProcesses != expectedSuccessfulProcesses
            || result.ExpectedOwnershipRejections != expectedOwnershipRejections
            || result.OwnershipRejectedProcesses != expectedOwnershipRejections
            || result.QualifiedProcesses != processes
            || result.FailedProcesses != 0)
        {
            failures.Add(
                "process outcomes "
                + $"successful={result.SuccessfulProcesses}/{result.ExpectedSuccessfulProcesses}, "
                + $"ownership-rejected={result.OwnershipRejectedProcesses}/{result.ExpectedOwnershipRejections}, "
                + $"qualified={result.QualifiedProcesses}/{processes}, failed={result.FailedProcesses}/0; "
                + $"expected successful={expectedSuccessfulProcesses}, "
                + $"ownership-rejected={expectedOwnershipRejections}");
            foreach (var failedResult in result.Results.Where(static item => !item.QualificationPassed))
            {
                failures.Add(
                    $"process {failedResult.ProcessIndex} failed: {failedResult.Error ?? "no error detail"}");
            }
        }

        var expectedObjects = checked((long)expectedSuccessfulProcesses * reads);
        if (result.SuccessfulReads != expectedObjects)
            failures.Add($"successful read count was {result.SuccessfulReads:N0}, expected {expectedObjects:N0}");
        if (result.MaterializedObjects != expectedObjects)
        {
            failures.Add(
                $"materialized object count was {result.MaterializedObjects:N0}, expected {expectedObjects:N0}");
        }

        var expectedChecksum = singleOwnerQualification
            ? ExpectedSuccessfulChecksum(result.Results, reads, records, payloadBytes)
            : ExpectedGroupChecksum(processes, reads, records, payloadBytes);
        if (result.Checksum != expectedChecksum)
            failures.Add($"checksum was {result.Checksum}, expected {expectedChecksum}");

        result.Passed = failures.Count == 0;
        if (failures.Count > 0)
            result.Error = $"Parent invariant failure: {string.Join("; ", failures)}.";
    }

    private static void EnsurePassed(string label, ReadProcessResult? result)
    {
        if (result is null || !result.Passed)
            throw new InvalidDataException($"{label} qualification failed: {result?.Error ?? "no result"}");
    }

    private static void EnsurePassed(string label, ReadProcessGroupResult? result)
    {
        if (result is null || !result.Passed)
            throw new InvalidDataException($"{label} qualification failed: {result?.Error ?? "no result"}");
    }

    private static string JoinErrors(string? first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Print(string label, ReadProcessResult? result)
    {
        if (result is null) return;
        Console.WriteLine(
            $"  {label}: {(result.Passed ? "PASS" : "FAIL")}; open {result.OpenMilliseconds:N2} ms; "
            + $"reads {result.ReadsPerSecond:N1}/s{ErrorSuffix(result.Error)}");
    }

    private static void Print(string label, ReadProcessGroupResult? result)
    {
        if (result is null) return;
        if (result.ReportsAggregateReadThroughput)
        {
            Console.WriteLine(
                $"  {label}: {(result.Passed ? "PASS" : "FAIL")}; "
                + $"{result.SuccessfulProcesses}/{result.Processes} materialized readers; "
                + $"aggregate {result.ReadsPerSecond:N1} reads/s");
        }
        else
        {
            Console.WriteLine(
                $"  {label}: {(result.Passed ? "PASS" : "FAIL")}; single-owner boundary; "
                + $"{result.SuccessfulProcesses}/{result.ExpectedSuccessfulProcesses} materialized reader; "
                + $"{result.OwnershipRejectedProcesses}/{result.ExpectedOwnershipRejections} "
                + "ownership rejections; aggregate multi-reader throughput not applicable");
        }

        foreach (var failure in result.Results.Where(static item => !item.QualificationPassed))
            Console.WriteLine($"    process {failure.ProcessIndex}: {failure.Error}");
    }

    private static string ErrorSuffix(string? error) => string.IsNullOrWhiteSpace(error) ? string.Empty : $"; {error}";

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static string Required(string[] args, string name) =>
        PhaseTwoOptions.ParseString(args, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static int ParseInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var text = PhaseTwoOptions.ParseString(args, name);
        var value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
        return value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name);
    }

    private static async Task ArriveAndWaitForReleaseAsync(
        string synchronizationRoot,
        int processIndex,
        int synchronizedWorkers)
    {
        Directory.CreateDirectory(synchronizationRoot);
        await File.WriteAllTextAsync(
            Path.Combine(synchronizationRoot, $"ready-{processIndex:D3}.marker"),
            string.Empty).ConfigureAwait(false);

        var releasePath = Path.Combine(synchronizationRoot, "release.marker");
        var started = Stopwatch.StartNew();
        while (!File.Exists(releasePath))
        {
            if (started.Elapsed >= WorkerBarrierTimeout)
            {
                throw new TimeoutException(
                    $"Timed out waiting to release {synchronizedWorkers} synchronized read workers.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static async Task SignalOpenAttemptAndWaitAsync(
        string? synchronizationRoot,
        int processIndex,
        int synchronizedWorkers)
    {
        if (synchronizationRoot is null)
        {
            return;
        }

        SignalOpenAttempt(synchronizationRoot, processIndex);
        await WaitForMarkerCountAsync(
            synchronizationRoot,
            "open-attempt-*.marker",
            synchronizedWorkers,
            "all workers to attempt opening the store").ConfigureAwait(false);
    }

    private static void SignalOpenAttempt(string? synchronizationRoot, int processIndex)
    {
        if (synchronizationRoot is null)
        {
            return;
        }

        Directory.CreateDirectory(synchronizationRoot);
        File.WriteAllText(
            Path.Combine(synchronizationRoot, $"open-attempt-{processIndex:D3}.marker"),
            string.Empty);
    }

    private static async Task WaitForMarkerCountAsync(
        string synchronizationRoot,
        string searchPattern,
        int expectedCount,
        string purpose)
    {
        var started = Stopwatch.StartNew();
        while (Directory.EnumerateFiles(synchronizationRoot, searchPattern).Take(expectedCount).Count()
               < expectedCount)
        {
            if (started.Elapsed >= WorkerBarrierTimeout)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {purpose}; expected {expectedCount} marker files.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static void TryDeleteOwnedRoot(string root)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbReadProcessQualification"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(root);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to clean unexpected read qualification root '{target}'.");
        try { Directory.Delete(target, recursive: true); } catch { }
    }

    internal sealed record ReadProcessOptions
    {
        public int Records { get; init; } = 100_000;
        public int PayloadBytes { get; init; } = 256;
        public int BatchSize { get; init; } = 1_000;
        public int Processes { get; init; } = 4;
        public int ReadsPerProcess { get; init; } = 25_000;
        public string OutputPath { get; init; } = "segment-core-read-process.json";

        public static ReadProcessOptions Parse(string[] args)
        {
            var options = new ReadProcessOptions
            {
                Records = ParseInt(args, "--records", 100_000, 1_000, 10_000_000),
                PayloadBytes = ParseInt(args, "--payload-bytes", 256, MinimumPayloadBytes, 1024 * 1024),
                BatchSize = ParseInt(args, "--batch-size", 1_000, 1, 16_384),
                Processes = ParseInt(args, "--processes", 4, 2, 32),
                ReadsPerProcess = ParseInt(args, "--reads-per-process", 25_000, 1, 10_000_000),
                OutputPath = PhaseTwoOptions.ParseString(args, "--output") ?? "segment-core-read-process.json"
            };
            return options;
        }
    }

    internal sealed record ReadProcessResult
    {
        public required string Engine { get; init; }
        public required int ProcessIndex { get; init; }
        public required int Reads { get; init; }
        public string MaterializationContract { get; init; } = string.Empty;
        public long MaterializedObjects { get; set; }
        public string Outcome { get; set; } = FailedOutcome;
        public string? ExceptionType { get; set; }
        public bool QualificationPassed { get; set; }
        public bool Passed { get; set; }
        public int ExitCode { get; set; }
        public double OpenMilliseconds { get; set; }
        public long OpenAllocatedBytes { get; set; }
        public double ReadMilliseconds { get; set; }
        public long ReadAllocatedBytes { get; set; }
        public IReadOnlyDictionary<string, double>? OpenStagesMilliseconds { get; set; }
        public double ReadsPerSecond => ReadMilliseconds <= 0 ? 0 : Reads / (ReadMilliseconds / 1000d);
        public long Checksum { get; set; }
        public string? Error { get; set; }
        public string? ProcessOutput { get; set; }
    }

    private sealed class OpenStageCollector : IDisposable
    {
        private readonly ConcurrentDictionary<string, double> _measurements =
            new(StringComparer.Ordinal);
        private readonly MeterListener _listener = new();

        public OpenStageCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, SegmentCoreMeterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, OpenStageInstrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>(OnMeasurement);
            _listener.Start();
        }

        public IReadOnlyDictionary<string, double> Snapshot() => _measurements
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        public void Dispose() => _listener.Dispose();

        private void OnMeasurement(
            Instrument instrument,
            double measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, "stage", StringComparison.Ordinal)
                    && tag.Value is string stage)
                {
                    _measurements[stage] = measurement;
                    return;
                }
            }
        }
    }

    internal sealed record ReadProcessGroupResult
    {
        public required string Engine { get; init; }
        public string MaterializationContract { get; init; } = string.Empty;
        public string QualificationContract { get; init; } = string.Empty;
        public bool ReportsAggregateReadThroughput { get; init; }
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public int Processes { get; init; }
        public int ExpectedSuccessfulProcesses { get; init; }
        public int ExpectedOwnershipRejections { get; init; }
        public int SuccessfulProcesses { get; init; }
        public int OwnershipRejectedProcesses { get; init; }
        public int QualifiedProcesses { get; init; }
        public int FailedProcesses { get; init; }
        public int SuccessfulReads { get; init; }
        public long MaterializedObjects { get; init; }
        public long Checksum { get; init; }
        public double WallMilliseconds { get; init; }
        public double? ReadsPerSecond => ReportsAggregateReadThroughput
            ? WallMilliseconds <= 0 ? 0 : SuccessfulReads / (WallMilliseconds / 1000d)
            : null;
        public required ReadProcessResult[] Results { get; init; }
    }

    internal sealed record ReadProcessReport
    {
        public string MaterializationContract { get; init; } = FullMaterializationContract;
        public required DateTimeOffset StartedUtc { get; init; }
        public DateTimeOffset CompletedUtc { get; set; }
        public required string Runtime { get; init; }
        public required string OperatingSystem { get; init; }
        public required ReadProcessOptions Options { get; init; }
        public ReadProcessResult? FreshProcessSegmentCore { get; set; }
        public ReadProcessResult? FreshProcessSqlite { get; set; }
        public ReadProcessGroupResult? MultiProcessSegmentCore { get; set; }
        public ReadProcessGroupResult? MultiProcessSqlite { get; set; }
        public ReadProcessResult? PostContentionSegmentCore { get; set; }
    }
}
