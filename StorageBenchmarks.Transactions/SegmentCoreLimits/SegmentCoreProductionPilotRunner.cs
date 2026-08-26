using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

internal static class SegmentCoreProductionPilotRunner
{
    public static async Task RunAsync(string[] args)
    {
        var options = SegmentCoreProductionPilotOptions.Parse(args);
        var parent = Path.Combine(Path.GetTempPath(), "InterfaceDbSegmentCorePilot");
        var runRoot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var report = new SegmentCoreProductionPilotReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Options = options
        };

        try
        {
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
            await WriteReportAsync(options.OutputPath, report).ConfigureAwait(false);
            if (options.KeepData)
            {
                report.DataRoot = runRoot;
                Console.WriteLine($"Pilot data retained at: {runRoot}");
                await WriteReportAsync(options.OutputPath, report).ConfigureAwait(false);
            }
            else
            {
                TryDeleteRunRoot(parent, runRoot);
            }
        }

        Console.WriteLine(report.Passed
            ? "SegmentCore repository production pilot: PASS"
            : "SegmentCore repository production pilot: FAIL");
        foreach (var metric in report.Metrics)
        {
            Console.WriteLine(
                $"  {metric.Operation}: {metric.Operations:N0} operations in {metric.ElapsedMilliseconds:N3} ms ({metric.OperationsPerSecond:N1} ops/s)"
                + (metric.Detail is null ? string.Empty : $"; {metric.Detail}"));
        }

        Console.WriteLine($"Report: {Path.GetFullPath(options.OutputPath)}");
        if (!report.Passed)
        {
            Environment.ExitCode = 1;
        }
    }

    private static async Task ExecuteAsync(
        string runRoot,
        SegmentCoreProductionPilotOptions options,
        SegmentCoreProductionPilotReport report)
    {
        var liveDataRoot = Path.Combine(runRoot, "live");
        var backupRoot = Path.Combine(runRoot, "backup");
        var restoredDataRoot = Path.Combine(runRoot, "restored");
        var repositoryOptions = SegmentCoreRepositoryOptions
            .ForHardwareProfile(options.HardwareProfile) with
        {
            EnableBackgroundMaintenance = false,
            CheckpointOnDispose = true
        };
        var expected = Enumerable.Range(0, options.Records)
            .Select(index => GateRecord.Create(index, "pilot"))
            .ToDictionary(static record => record.Id, StringComparer.Ordinal);
        SegmentCorePilotStore? store = new(liveDataRoot, repositoryOptions);

        try
        {
            report.Metrics.Add(await MeasureAsync(
                "Initial repository open",
                1,
                () => store.InitializeAsync()).ConfigureAwait(false));
            report.Metrics.Add(await MeasureAsync(
                "Atomic batched seed",
                expected.Count,
                () => store.SaveManyAsync(expected.Values, options.BatchSize)).ConfigureAwait(false));
            report.Metrics.Add(await ValidateQueriesAsync(store, expected, "Seed query validation").ConfigureAwait(false));

            for (var cycle = 1; cycle <= options.Cycles; cycle++)
            {
                var updates = expected.Values
                    .OrderBy(static record => record.Id, StringComparer.Ordinal)
                    .Where((_, index) => index % 7 == cycle % 7)
                    .Take(options.UpdatesPerCycle)
                    .Select(record => record with
                    {
                        Age = 18 + (record.Age + cycle) % 63,
                        Content = record.Content + $" revision-{cycle}",
                        Revision = cycle
                    })
                    .ToArray();
                report.Metrics.Add(await MeasureAsync(
                    $"Cycle {cycle} atomic updates",
                    updates.Length,
                    () => store.UpdateManyAsync(updates, options.BatchSize)).ConfigureAwait(false));
                foreach (var update in updates)
                {
                    expected[update.Id] = update;
                }

                var insertStart = options.Records + ((cycle - 1) * options.InsertsPerCycle);
                var inserts = Enumerable.Range(insertStart, options.InsertsPerCycle)
                    .Select(index => GateRecord.Create(index, "pilot-new"))
                    .Select(record => record with { Revision = cycle })
                    .ToArray();
                report.Metrics.Add(await MeasureAsync(
                    $"Cycle {cycle} atomic inserts",
                    inserts.Length,
                    () => store.SaveManyAsync(inserts, options.BatchSize)).ConfigureAwait(false));
                foreach (var insert in inserts)
                {
                    expected.Add(insert.Id, insert);
                }

                var deleteIds = expected.Keys
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .Where(id => !id.StartsWith("pilot-new", StringComparison.Ordinal))
                    .Take(options.DeletesPerCycle)
                    .ToArray();
                report.Metrics.Add(await MeasureAsync(
                    $"Cycle {cycle} indexed deletes",
                    deleteIds.Length,
                    () => store.DeleteManyAsync(deleteIds)).ConfigureAwait(false));
                foreach (var id in deleteIds)
                {
                    _ = expected.Remove(id);
                }

                report.Metrics.Add(await ValidateQueriesAsync(
                    store,
                    expected,
                    $"Cycle {cycle} indexed query validation").ConfigureAwait(false));
                report.Metrics.Add(await MeasureAsync(
                    $"Cycle {cycle} checkpoint",
                    1,
                    async () =>
                    {
                        var checkpoint = await store.CheckpointAsync().ConfigureAwait(false);
                        report.LastCheckpointGeneration = checkpoint.Generation;
                    }).ConfigureAwait(false));
            }

            await ValidateSnapshotAsync(store, expected).ConfigureAwait(false);
            var integrityBefore = await store.VerifyIntegrityAsync().ConfigureAwait(false);
            RequireIntegrity(integrityBefore, expected.Count, "before compaction");
            report.StorageBeforeCompaction = store.GetStorageStats();

            SegmentCoreCompactionResult compaction = default;
            var compactionMetric = await MeasureAsync(
                "Bounded compaction",
                expected.Count,
                async () =>
                {
                    compaction = await store.CompactAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            report.Metrics.Add(compactionMetric with
            {
                Detail = $"Reclaimed {compaction.BytesReclaimed:N0} bytes in {compaction.Slices:N0} slices; longest exclusive slice {compaction.LongestExclusiveSlice.TotalMilliseconds:N3} ms."
            });
            report.StorageAfterCompaction = store.GetStorageStats();

            SegmentCoreBackupResult backup = default;
            var backupMetric = await MeasureAsync(
                "Verified online backup",
                expected.Count,
                async () =>
                {
                    backup = await store.BackupAsync(backupRoot).ConfigureAwait(false);
                }).ConfigureAwait(false);
            report.Metrics.Add(backupMetric with
            {
                Detail = $"{backup.Files:N0} files, {backup.Bytes:N0} bytes, checkpoint {backup.CheckpointGeneration:N0}."
            });

            await store.DisposeAsync().ConfigureAwait(false);
            store = new SegmentCorePilotStore(liveDataRoot, repositoryOptions);
            report.Metrics.Add(await MeasureAsync(
                "Repository reopen with persisted indexes",
                1,
                () => store.InitializeAsync()).ConfigureAwait(false));
            await ValidateSnapshotAsync(store, expected).ConfigureAwait(false);
            report.Metrics.Add(await ValidateQueriesAsync(
                store,
                expected,
                "Reopen indexed query validation").ConfigureAwait(false));
            RequireIntegrity(
                await store.VerifyIntegrityAsync().ConfigureAwait(false),
                expected.Count,
                "after reopen");

            var restoredStoreRoot = Path.Combine(
                restoredDataRoot,
                repositoryOptions.DirectoryName,
                "1.0.0");
            report.Metrics.Add(await MeasureAsync(
                "Verified backup restore",
                expected.Count,
                async () =>
                {
                    _ = await SegmentCoreStore.RestoreBackupAsync(
                        backupRoot,
                        restoredStoreRoot,
                        repositoryOptions.StoreOptions).ConfigureAwait(false);
                }).ConfigureAwait(false));

            await using var restored = new SegmentCorePilotStore(restoredDataRoot, repositoryOptions);
            report.Metrics.Add(await MeasureAsync(
                "Restored repository open and index rebuild",
                1,
                () => restored.InitializeAsync()).ConfigureAwait(false));
            await ValidateSnapshotAsync(restored, expected).ConfigureAwait(false);
            report.Metrics.Add(await ValidateQueriesAsync(
                restored,
                expected,
                "Restored indexed query validation").ConfigureAwait(false));
            RequireIntegrity(
                await restored.VerifyIntegrityAsync().ConfigureAwait(false),
                expected.Count,
                "after restore");

            report.FinalRecords = expected.Count;
            report.FinalChecksum = expected.Values.Sum(static record => record.MaterializedChecksum);
            report.StorageAfterRestore = restored.GetStorageStats();
        }
        finally
        {
            if (store is not null)
            {
                await store.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<SegmentCoreProductionPilotMetric> ValidateQueriesAsync(
        SegmentCorePilotStore store,
        IReadOnlyDictionary<string, GateRecord> expected,
        string operation)
    {
        if (expected.Count == 0)
        {
            throw new InvalidOperationException("The pilot requires at least one live record.");
        }

        var exactId = expected.Keys.OrderBy(static id => id, StringComparer.Ordinal).Last();
        var started = Stopwatch.GetTimestamp();
        foreach (var kind in Enum.GetValues<GateQueryKind>())
        {
            var actual = await store.QueryAsync(kind, exactId).ConfigureAwait(false);
            var required = ExpectedQuery(expected.Values, kind, exactId);
            if (actual != required)
            {
                throw new InvalidDataException(
                    $"{operation} {kind} returned {actual.Count}/{actual.Checksum}; expected {required.Count}/{required.Checksum}.");
            }
        }

        return CreateMetric(operation, Enum.GetValues<GateQueryKind>().Length, Stopwatch.GetElapsedTime(started));
    }

    private static GateQueryResult ExpectedQuery(
        IEnumerable<GateRecord> records,
        GateQueryKind kind,
        string exactId)
    {
        var matches = kind switch
        {
            GateQueryKind.Exact => records.Where(record => record.Id == exactId),
            GateQueryKind.Range => records.Where(static record => record.Age >= 75),
            GateQueryKind.Prefix => records.Where(static record => record.Name.StartsWith("customer-00000", StringComparison.Ordinal)),
            GateQueryKind.Suffix => records.Where(static record => record.Name.EndsWith("special-tail", StringComparison.Ordinal)),
            GateQueryKind.Contains => records.Where(static record => record.Content.Contains("rare-needle", StringComparison.Ordinal)),
            GateQueryKind.BroadConjunction => records.Where(static record => record.Role == "student" && record.Age >= 40),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var materialized = matches.ToArray();
        return new GateQueryResult(
            materialized.Length,
            materialized.Sum(static record => record.MaterializedChecksum));
    }

    private static async Task ValidateSnapshotAsync(
        SegmentCorePilotStore store,
        IReadOnlyDictionary<string, GateRecord> expected)
    {
        var actual = await store.SnapshotAsync().ConfigureAwait(false);
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException($"Snapshot contained {actual.Count:N0} records; expected {expected.Count:N0}.");
        }

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var candidate) || candidate != pair.Value)
            {
                throw new InvalidDataException($"Snapshot did not preserve '{pair.Key}'.");
            }
        }
    }

    private static void RequireIntegrity(
        SegmentCoreIntegrityResult integrity,
        int expectedEntries,
        string stage)
    {
        if (!integrity.IsValid || integrity.EntriesChecked != expectedEntries)
        {
            throw new InvalidDataException(
                $"SegmentCore integrity failed {stage}: " + string.Join("; ", integrity.Failures));
        }
    }

    private static async Task<SegmentCoreProductionPilotMetric> MeasureAsync(
        string operation,
        int operations,
        Func<ValueTask> action)
    {
        var started = Stopwatch.GetTimestamp();
        await action().ConfigureAwait(false);
        return CreateMetric(operation, operations, Stopwatch.GetElapsedTime(started));
    }

    private static SegmentCoreProductionPilotMetric CreateMetric(
        string operation,
        int operations,
        TimeSpan elapsed) => new(
        operation,
        operations,
        elapsed.TotalMilliseconds,
        elapsed.TotalSeconds <= 0 ? 0 : operations / elapsed.TotalSeconds,
        null);

    private static async Task WriteReportAsync(
        string outputPath,
        SegmentCoreProductionPilotReport report)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static void TryDeleteRunRoot(string parent, string runRoot)
    {
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullRunRoot = Path.GetFullPath(runRoot);
        if (!fullRunRoot.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to remove unexpected pilot path '{fullRunRoot}'.");
        }

        try
        {
            Directory.Delete(fullRunRoot, recursive: true);
        }
        catch
        {
            // Cleanup must not replace the qualification result.
        }
    }
}

internal sealed class SegmentCorePilotStore : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly SegmentCoreRepositoryOptions _repositoryOptions;
    private readonly SegmentCorePilotContractOptions _contractOptions;
    private ServiceProvider? _provider;
    private IRepository<IProductionGateRecord>? _repository;
    private ISegmentCoreRepositoryMaintenance<IProductionGateRecord>? _maintenance;

    public SegmentCorePilotStore(string dataRoot, SegmentCoreRepositoryOptions repositoryOptions)
    {
        _dataRoot = dataRoot;
        _repositoryOptions = repositoryOptions;
        _contractOptions = new SegmentCorePilotContractOptions(dataRoot, repositoryOptions.HardwareProfile);
    }

    public async ValueTask InitializeAsync()
    {
        if (_provider is not null)
        {
            throw new InvalidOperationException("The pilot repository is already initialized.");
        }

        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IProductionGateRecord>(
            _contractOptions,
            _repositoryOptions,
            _contractOptions.StoragePolicy,
            new SegmentCorePilotIdentityFactory());
        _provider = services.BuildServiceProvider();
        _repository = _provider.GetRequiredService<IRepository<IProductionGateRecord>>();
        _maintenance = _provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProductionGateRecord>>();
        await _repository.InitializeAsync().ConfigureAwait(false);
    }

    public async ValueTask SaveManyAsync(IEnumerable<GateRecord> records, int batchSize)
    {
        foreach (var batch in records.Chunk(batchSize))
        {
            _ = await Repository.SaveManyAsync(batch.Select(ToStorageRecord)).ConfigureAwait(false);
        }
    }

    public async ValueTask UpdateManyAsync(IEnumerable<GateRecord> records, int batchSize)
    {
        foreach (var batch in records.Chunk(batchSize))
        {
            var tracked = new List<IProductionGateRecord>(batch.Length);
            foreach (var update in batch)
            {
                var existing = await Repository.QueryFirstByStorageKeyAsync(update.Id).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Pilot could not find '{update.Id}' for update.");
                existing.Age = update.Age;
                existing.Content = update.Content;
                existing.Revision = update.Revision;
                tracked.Add(existing);
            }

            _ = await Repository.SaveManyAsync(tracked).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteManyAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var existing = await Repository.QueryFirstByStorageKeyAsync(id).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Pilot could not find '{id}' for deletion.");
            if (!await Repository.DeleteAsync(existing).ConfigureAwait(false))
            {
                throw new InvalidDataException($"Pilot could not delete '{id}'.");
            }
        }
    }

    public async ValueTask<GateQueryResult> QueryAsync(GateQueryKind kind, string exactId)
    {
        var results = kind switch
        {
            GateQueryKind.Exact => Repository.SearchManyAsync(record => record.Id == exactId),
            GateQueryKind.Range => Repository.SearchManyAsync(record => record.Age >= 75),
            GateQueryKind.Prefix => Repository.SearchManyAsync(record => record.Name.StartsWith("customer-00000")),
            GateQueryKind.Suffix => Repository.SearchManyAsync(record => record.Name.EndsWith("special-tail")),
            GateQueryKind.Contains => Repository.SearchManyAsync(record => record.Content.Contains("rare-needle")),
            GateQueryKind.BroadConjunction => Repository.SearchManyAsync(record => record.Role == "student" && record.Age >= 40),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var count = 0;
        long checksum = 0;
        await foreach (var record in results.ConfigureAwait(false))
        {
            count++;
            checksum += MaterializedChecksum(record);
        }

        return new GateQueryResult(count, checksum);
    }

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync()
    {
        var result = new Dictionary<string, GateRecord>(StringComparer.Ordinal);
        await foreach (var record in Repository.QueryManyAsync(static _ => true).ConfigureAwait(false))
        {
            var materialized = ((ProductionGateRecord)record).ToGateRecord();
            if (!result.TryAdd(materialized.Id, materialized))
            {
                throw new InvalidDataException($"Pilot snapshot contained duplicate id '{materialized.Id}'.");
            }
        }

        return result;
    }

    public ValueTask<SegmentCoreCheckpointResult> CheckpointAsync() => Maintenance.CheckpointAsync();

    public ValueTask<SegmentCoreCompactionResult> CompactAsync() => Maintenance.CompactAsync();

    public ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsync() => Maintenance.VerifyIntegrityAsync();

    public ValueTask<SegmentCoreBackupResult> BackupAsync(string destination) => Maintenance.BackupAsync(destination);

    public GateStorageStats GetStorageStats()
    {
        if (!Directory.Exists(_dataRoot))
        {
            return default;
        }

        long bytes = 0;
        var files = 0;
        foreach (var path in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(path).Length;
            files++;
        }

        return new GateStorageStats(bytes, files);
    }

    public async ValueTask DisposeAsync()
    {
        _repository = null;
        _maintenance = null;
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            _provider = null;
        }
    }

    private IRepository<IProductionGateRecord> Repository =>
        _repository ?? throw new InvalidOperationException("The pilot repository is not initialized.");

    private ISegmentCoreRepositoryMaintenance<IProductionGateRecord> Maintenance =>
        _maintenance ?? throw new InvalidOperationException("The pilot repository is not initialized.");

    private static ProductionGateRecord ToStorageRecord(GateRecord record) => new(
        record.Id,
        record.Name,
        record.Age,
        record.Role,
        record.Content,
        record.Revision);

    private static long MaterializedChecksum(IProductionGateRecord record) =>
        record.Id.Length + record.Name.Length + record.Age + record.Role.Length + record.Content.Length + record.Revision;
}

internal sealed class SegmentCorePilotContractOptions : IDbContractConfigDto<IProductionGateRecord>
{
    public SegmentCorePilotContractOptions(string dataRoot, StorageHardwareProfile profile)
    {
        DataRoot = dataRoot;
        StoragePolicy = Idb.Libraries.Abstractions.Options.StoragePolicy.ForHardwareProfile(profile) with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            HydrateOnInitialize = false,
            RaiseNotifications = false
        };
    }

    public string EnvelopeDICacheKey { get; } = "SegmentCoreProductionPilot";
    public string ContractName { get; } = "SegmentCoreProductionPilot";
    public string FileExtension { get; } = ".mpk";
    public string? ImplementationsPath { get; } = null;
    public string? ContractPath { get; } = null;
    public string? MigrationsPath { get; } = null;
    public string? DataRoot { get; }
    public string? ActiveVersion { get; } = "1.0.0";
    public ContractRepositoryType RepositoryType { get; } = ContractRepositoryType.SegmentCore;
    public StorageObjectCardinality ObjectCardinality { get; } = StorageObjectCardinality.Multiple;
    public StoragePolicy? StoragePolicy { get; }
}

internal sealed class SegmentCorePilotIdentityFactory : IEnvelopeStorageIdentityFactory<IProductionGateRecord>
{
    public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;

    public void InitEnvelope(IObjectEnvelope<IProductionGateRecord> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var record = envelope.ObjectInstance
            ?? throw new InvalidOperationException("Envelope must contain a production-pilot record.");
        envelope.EnvelopeCacheKey = record.Id;
        envelope.Location ??= record.Id;
    }
}

internal sealed record SegmentCoreProductionPilotOptions
{
    public int Records { get; init; }
    public int Cycles { get; init; }
    public int BatchSize { get; init; }
    public int UpdatesPerCycle { get; init; }
    public int InsertsPerCycle { get; init; }
    public int DeletesPerCycle { get; init; }
    public StorageHardwareProfile HardwareProfile { get; init; }
    public bool KeepData { get; init; }
    public string OutputPath { get; init; } = string.Empty;

    public static SegmentCoreProductionPilotOptions Parse(string[] args)
    {
        var quick = HasFlag(args, "--quick");
        var profileText = PhaseTwoOptions.ParseString(args, "--profile") ?? "HighThroughput";
        if (!Enum.TryParse<StorageHardwareProfile>(profileText, ignoreCase: true, out var profile)
            || profile == StorageHardwareProfile.Custom)
        {
            throw new ArgumentException("--profile must be Portable, LowMemory, HighThroughput, or WindowsNvme.");
        }

        return new SegmentCoreProductionPilotOptions
        {
            Records = ParsePositive(args, "--records", quick ? 2_000 : 10_000),
            Cycles = ParsePositive(args, "--cycles", quick ? 2 : 5),
            BatchSize = ParsePositive(args, "--batch-size", 256),
            UpdatesPerCycle = ParsePositive(args, "--updates", quick ? 200 : 500),
            InsertsPerCycle = ParsePositive(args, "--inserts", quick ? 100 : 200),
            DeletesPerCycle = ParsePositive(args, "--deletes", quick ? 50 : 100),
            HardwareProfile = profile,
            KeepData = HasFlag(args, "--keep-data"),
            OutputPath = PhaseTwoOptions.ParseString(args, "--output") ?? Path.Combine(
                "artifacts",
                "segment-core",
                $"segment-core-production-pilot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json")
        };
    }

    private static int ParsePositive(string[] args, string name, int fallback)
    {
        var text = PhaseTwoOptions.ParseString(args, name);
        var value = text is null ? fallback : int.Parse(text, CultureInfo.InvariantCulture);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(name);
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed record SegmentCoreProductionPilotMetric(
    string Operation,
    int Operations,
    double ElapsedMilliseconds,
    double OperationsPerSecond,
    string? Detail);

internal sealed record SegmentCoreProductionPilotReport
{
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public required string Runtime { get; init; }
    public required string OperatingSystem { get; init; }
    public required SegmentCoreProductionPilotOptions Options { get; init; }
    public List<SegmentCoreProductionPilotMetric> Metrics { get; init; } = [];
    public List<string> Failures { get; init; } = [];
    public GateStorageStats StorageBeforeCompaction { get; set; }
    public GateStorageStats StorageAfterCompaction { get; set; }
    public GateStorageStats StorageAfterRestore { get; set; }
    public long LastCheckpointGeneration { get; set; }
    public int FinalRecords { get; set; }
    public long FinalChecksum { get; set; }
    public string? DataRoot { get; set; }
    public bool Passed => Failures.Count == 0;
}
