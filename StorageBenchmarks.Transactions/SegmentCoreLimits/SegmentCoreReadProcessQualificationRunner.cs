using System.Buffers.Binary;
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
                captureOpenStages: true).ConfigureAwait(false);
            report.FreshProcessSqlite = await RunChildAsync(
                root, "sqlite", sqlitePath, 0, options.ReadsPerProcess, options.Records).ConfigureAwait(false);

            report.MultiProcessSegmentCore = await RunChildrenAsync(
                root, "segment-core", segmentRoot, options.Processes, options.ReadsPerProcess, options.Records)
                .ConfigureAwait(false);
            report.MultiProcessSqlite = await RunChildrenAsync(
                root, "sqlite", sqlitePath, options.Processes, options.ReadsPerProcess, options.Records)
                .ConfigureAwait(false);
            report.PostContentionSegmentCore = await RunChildAsync(
                root, "segment-core", segmentRoot, options.Processes + 1, options.ReadsPerProcess, options.Records)
                .ConfigureAwait(false);
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
        var processIndex = ParseInt(args, "--process-index", 0, 0, 1_000);
        var captureOpenStages = args.Contains("--capture-open-stages", StringComparer.OrdinalIgnoreCase);
        var result = new ReadProcessResult
        {
            Engine = engine,
            ProcessIndex = processIndex,
            Reads = reads
        };

        try
        {
            if (string.Equals(engine, "segment-core", StringComparison.OrdinalIgnoreCase))
            {
                await ReadSegmentCoreAsync(
                    root,
                    reads,
                    processIndex,
                    records,
                    captureOpenStages,
                    result).ConfigureAwait(false);
            }
            else if (string.Equals(engine, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await ReadSqliteAsync(root, reads, processIndex, records, result).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentOutOfRangeException("--engine", engine, "Expected segment-core or sqlite.");
            }

            result.Passed = true;
        }
        catch (Exception exception)
        {
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
        bool captureOpenStages,
        ReadProcessResult result)
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
        ForceGc();
        var readAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var read = Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < reads; index++)
        {
            var ordinal = Ordinal(index, processIndex, records);
            var payload = await store.GetAsync(Key(ordinal)).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Missing SegmentCore key {ordinal:N0}.");
            Validate(payload, ordinal);
            checksum += BinaryPrimitives.ReadInt32LittleEndian(payload);
        }

        read.Stop();
        result.ReadMilliseconds = read.Elapsed.TotalMilliseconds;
        result.ReadAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - readAllocatedBefore;
        result.Checksum = checksum;
    }

    private static async Task ReadSqliteAsync(
        string path,
        int reads,
        int processIndex,
        int records,
        ReadProcessResult result)
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
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT Payload FROM Records WHERE Id = $id";
        var id = select.Parameters.Add("$id", SqliteType.Text);
        await select.PrepareAsync().ConfigureAwait(false);
        ForceGc();
        var readAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var read = Stopwatch.StartNew();
        long checksum = 0;
        for (var index = 0; index < reads; index++)
        {
            var ordinal = Ordinal(index, processIndex, records);
            id.Value = Key(ordinal);
            var value = await select.ExecuteScalarAsync().ConfigureAwait(false) as byte[]
                ?? throw new InvalidDataException($"Missing SQLite key {ordinal:N0}.");
            Validate(value, ordinal);
            checksum += BinaryPrimitives.ReadInt32LittleEndian(value);
        }

        read.Stop();
        result.ReadMilliseconds = read.Elapsed.TotalMilliseconds;
        result.ReadAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - readAllocatedBefore;
        result.Checksum = checksum;
    }

    private static async Task<ReadProcessGroupResult> RunChildrenAsync(
        string parentRoot,
        string engine,
        string storeRoot,
        int processes,
        int reads,
        int records)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, processes)
            .Select(index => RunChildAsync(parentRoot, engine, storeRoot, index, reads, records)))
            .ConfigureAwait(false);
        stopwatch.Stop();
        return new ReadProcessGroupResult
        {
            Engine = engine,
            Processes = processes,
            SuccessfulProcesses = results.Count(static result => result.Passed),
            FailedProcesses = results.Count(static result => !result.Passed),
            SuccessfulReads = results.Where(static result => result.Passed).Sum(static result => result.Reads),
            WallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            Results = results
        };
    }

    private static async Task<ReadProcessResult> RunChildAsync(
        string parentRoot,
        string engine,
        string storeRoot,
        int processIndex,
        int reads,
        int records,
        bool captureOpenStages = false)
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
        start.ArgumentList.Add($"--process-index={processIndex.ToString(CultureInfo.InvariantCulture)}");
        start.ArgumentList.Add($"--output={output}");
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
            return result;
        }

        return new ReadProcessResult
        {
            Engine = engine,
            ProcessIndex = processIndex,
            Reads = reads,
            ExitCode = process.ExitCode,
            Error = "Worker exited without a result file.",
            ProcessOutput = string.Join(Environment.NewLine, outputText, errorText)
        };
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

    private static string Key(int ordinal) => $"read-{ordinal:D10}";

    private static byte[] Payload(int ordinal, int bytes)
    {
        var payload = new byte[bytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, ordinal);
        for (var index = sizeof(int); index < bytes; index++)
            payload[index] = (byte)((ordinal * 31 + index * 17) & 0xFF);
        return payload;
    }

    private static void Validate(byte[] payload, int ordinal)
    {
        if (payload.Length < sizeof(int) || BinaryPrimitives.ReadInt32LittleEndian(payload) != ordinal)
            throw new InvalidDataException($"Payload {ordinal:N0} has an invalid header.");
        for (var index = sizeof(int); index < payload.Length; index++)
            if (payload[index] != (byte)((ordinal * 31 + index * 17) & 0xFF))
                throw new InvalidDataException($"Payload {ordinal:N0} is corrupt at byte {index:N0}.");
    }

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
        Console.WriteLine(
            $"  {label}: {result.SuccessfulProcesses}/{result.Processes} processes; "
            + $"aggregate {result.ReadsPerSecond:N1} reads/s");
        foreach (var failure in result.Results.Where(static item => !item.Passed))
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
                PayloadBytes = ParseInt(args, "--payload-bytes", 256, sizeof(int), 1024 * 1024),
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
        public int Processes { get; init; }
        public int SuccessfulProcesses { get; init; }
        public int FailedProcesses { get; init; }
        public int SuccessfulReads { get; init; }
        public double WallMilliseconds { get; init; }
        public double ReadsPerSecond => WallMilliseconds <= 0 ? 0 : SuccessfulReads / (WallMilliseconds / 1000d);
        public required ReadProcessResult[] Results { get; init; }
    }

    internal sealed record ReadProcessReport
    {
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
