using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

// Application-level comparison: full repository/index/envelope path, not raw engine calls.
internal static class RepositorySqlComparison
{
    internal sealed record Metric(string Engine, string Cache, string Operation, int Records,
        int ResultCount, double[] SamplesMs, double AllocatedBytesPerOperation,
        double P50Ms, double P95Ms, double MeanMs);

    public static async Task RunAsync(string[] args)
    {
        string? Arg(string name) => ProductionGateOptions.ParseString(args, name);
        int Number(string name, int fallback) =>
            int.TryParse(Arg(name) ?? fallback.ToString(), out var result) && result > 0
                ? result : throw new ArgumentException(name);
        var records = Number("--records", 1000);
        var iterations = Number("--iterations", 50);
        var mutations = Math.Min(records, Number("--mutation-limit", 1000));
        var caches = (Arg("--cache") ?? "MetadataOnly,StrongObject,WeakObject").Split(',')
            .Select(value => Enum.Parse<StorageObjectCacheMode>(value, true)).ToArray();
        var output = Path.GetFullPath(Arg("--output") ?? "sql-comparison.json");
        var label = Arg("--label") ?? "current";
        var reverse = args.Contains("--reverse");
        var onlyInterfaceDb = args.Contains("--only-interfacedb");
        if (onlyInterfaceDb && args.Contains("--include-sqlserver"))
            throw new ArgumentException("--only-interfacedb cannot be combined with --include-sqlserver.");
        var queriesOnly = args.Contains("--queries-only");
        var compareOrdinalStrings = args.Contains("--compare-ordinal-strings");
        var skipFile = args.Contains("--skip-file");
        if (!Enum.TryParse<ObjectLogDurabilityStrategy>(Arg("--segment-durability") ?? "ExplicitFlush", true, out var segmentDurability)
            || !Enum.IsDefined(segmentDurability))
            throw new ArgumentException("--segment-durability must be ExplicitFlush or WriteThrough.");
        var queryKinds = Enum.GetValues<GateQueryKind>();
        if (Arg("--query-kind") is { } requestedQuery)
        {
            if (!Enum.TryParse<GateQueryKind>(requestedQuery, true, out var queryKind) || !Enum.IsDefined(queryKind))
                throw new ArgumentException("--query-kind must name a GateQueryKind.");
            queryKinds = [queryKind];
        }
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDbSqlComparison", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Enumerable.Range(0, records).Select(index => GateRecord.Create(index)).ToArray();
        var metrics = new List<Metric>();
        var storage = new List<object>();
        var queryPlans = new List<object>();
        var failures = new List<string>();
        var started = DateTimeOffset.UtcNow;
        string? sqlServerVersion = null;
        string? sqlServerDurability = null;
        var factories = new List<Func<IProductionGateStore>>();
        foreach (var cache in caches)
        {
            if (!skipFile)
                factories.Add(() => new InterfaceDbGateStore(Path.Combine(root, $"file-{cache}"),
                    cache, StorageDurabilityMode.Durable));
            factories.Add(() => new InterfaceDbGateStore(Path.Combine(root, $"segment-{cache}"),
                cache, StorageDurabilityMode.Durable, register: (services, options) =>
                    services.AddSegmentCoreRepository<IProductionGateRecord>(options,
                        new SegmentCoreRepositoryOptions
                        {
                            HardwareProfile = StorageHardwareProfile.Custom,
                            EnableBackgroundMaintenance = false,
                            StoreOptions = new SegmentCoreOptions
                            {
                                TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
                                LogOptions = new StripedObjectLogProbeOptions
                                {
                                    StripeCount = 4,
                                    DurabilityStrategy = segmentDurability
                                }
                            }
                        }, storageIdentityFactory: new ProductionGateIdentityFactory(options)),
                engineName: "InterfaceDB SegmentCore", repositoryType: ContractRepositoryType.SegmentCore));
        }
        if (!onlyInterfaceDb)
        {
            factories.Add(() => new SqliteGateStore(Path.Combine(root, "direct.db"),
                StorageDurabilityMode.Durable, SqliteClientKind.MicrosoftDataSqlite, indexedRangeAndPrefix: true));
            factories.Add(() => new SqliteGateStore(Path.Combine(root, "dapper.db"),
                StorageDurabilityMode.Durable, SqliteClientKind.Dapper, indexedRangeAndPrefix: true));
        }
        if (args.Contains("--include-sqlserver"))
        {
            var connectionString = TransactionBenchmarks.EnsureConfigurationInitialized()["Benchmarks:SqlServer:ConnectionString"]
                ?? throw new InvalidOperationException("SQL Server connection is not configured.");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion')) + ' / ' + CONVERT(nvarchar(128),SERVERPROPERTY('Edition'))";
            sqlServerVersion = Convert.ToString(await command.ExecuteScalarAsync());
            command.CommandText = "SELECT delayed_durability_desc FROM sys.databases WHERE database_id = DB_ID()";
            sqlServerDurability = Convert.ToString(await command.ExecuteScalarAsync());
            if (!string.Equals(sqlServerDurability, "DISABLED", StringComparison.Ordinal))
                throw new InvalidOperationException("The durable comparison requires SQL Server delayed durability to be disabled.");
            factories.Add(() => new SqlServerGateStore(connectionString, dropOnDispose: true, indexAge: true));
        }
        if (reverse) factories.Reverse();
        try
        {
            foreach (var factory in factories)
            {
                await using var store = factory();
                Console.WriteLine($"{label}: {store.EngineName}/{store.CacheModeName}, {records} records");
                try { await MeasureStoreAsync(store); }
                catch (Exception exception)
                {
                    failures.Add($"{store.EngineName}/{store.CacheModeName}: {exception.GetType().Name}: {exception.Message}");
                    Console.Error.WriteLine(failures[^1]);
                    throw;
                }
            }
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var assemblies = new[] { typeof(RepositorySqlComparison).Assembly,
                typeof(Idb.Libraries.Abstractions.Services.Repository.IRepository<>).Assembly,
                typeof(SegmentCoreOptions).Assembly, typeof(SqliteConnection).Assembly,
                typeof(Dapper.SqlMapper).Assembly, typeof(SqlConnection).Assembly }.Distinct()
                .ToDictionary(assembly => assembly.GetName().Name!,
                    assembly => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))));
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                Label = label, StartedUtc = started, CompletedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Records = records, Iterations = iterations, MutationLimit = mutations,
                CacheModes = caches.Select(value => value.ToString()).ToArray(),
                OnlyInterfaceDb = onlyInterfaceDb, QueriesOnly = queriesOnly, CompareOrdinalStrings = compareOrdinalStrings,
                SkipFile = skipFile, SegmentDurability = segmentDurability.ToString(),
                QueryKinds = queryKinds.Select(kind => kind.ToString()).ToArray(),
                Reverse = reverse, MaterializationContract = GateRecordMaterialization.Contract,
                SqliteVersion = new SqliteConnection("Data Source=:memory:").ServerVersion,
                SqlServerVersion = sqlServerVersion, SqlServerDelayedDurability = sqlServerDurability, Assemblies = assemblies,
                SqlIndexProfile = "Id primary key; Age; Role+Age; Name; SQLite ordinal prefix GLOB. No SQL substring/suffix index.",
                Metrics = metrics, Storage = storage, QueryPlans = queryPlans,
                Failures = failures, Passed = failures.Count == 0
            }, new JsonSerializerOptions { WriteIndented = true }));
            // Only the unique directory created by this invocation is removed.
            Directory.Delete(root, recursive: true);
        }

        async Task MeasureStoreAsync(IProductionGateStore store)
        {
            var expected = source.ToDictionary(record => record.Id, StringComparer.Ordinal);
            await Measure("initialize-empty", 1, 0, _ => store.InitializeAsync());
            var warmup = GateRecord.Create(int.MaxValue, "warmup");
            await store.InsertAsync(warmup);
            if (!await store.DeleteAsync(warmup.Id)) throw new InvalidOperationException("Warmup delete failed.");
            await Measure("seed-object-per-operation", records, 1, index => store.InsertAsync(source[index]));
            await ValidateAsync();
            await Measure("provider-specific-startup", 1, records, _ => store.ReopenAsync());
            await ValidateAsync();

            var id = source[records / 2].Id;
            if (store is IQueryPlanGateStore planner)
            {
                var plans = await planner.ExplainQueriesAsync(id);
                if (!plans["Range"].Any(line => line.Contains("IX_Records_Age", StringComparison.Ordinal))
                    || !plans["Prefix"].Any(line => line.Contains("IX_Records_Name", StringComparison.Ordinal)))
                    throw new InvalidOperationException("SQLite range/prefix control did not use its index.");
                queryPlans.Add(new { store.EngineName, Plans = plans });
            }
            foreach (var kind in queryKinds)
            {
                var matches = source.Where(record => Matches(record, kind, id)).ToArray();
                var expectedQuery = new GateQueryResult(matches.Length, matches.Sum(record => record.MaterializedChecksum));
                for (var i = 0; i < 5; i++) VerifyQuery(await store.QueryAsync(kind, id), expectedQuery);
                await Measure("query-" + kind.ToString().ToLowerInvariant(), iterations, matches.Length,
                    async _ => VerifyQuery(await store.QueryAsync(kind, id), expectedQuery));
            }
            if (compareOrdinalStrings && store is IOrdinalStringGateStore ordinalStore)
            {
                queryPlans.Add(new { store.EngineName, StringPlans = ordinalStore.ExplainStringQueries() });
                foreach (var kind in new[] { GateQueryKind.Prefix, GateQueryKind.Suffix })
                {
                    var matches = source.Where(record => Matches(record, kind, id)).ToArray();
                    var expectedQuery = new GateQueryResult(matches.Length, matches.Sum(record => record.MaterializedChecksum));
                    for (var i = 0; i < 5; i++) VerifyQuery(await ordinalStore.QueryOrdinalAsync(kind), expectedQuery);
                    await Measure("query-ordinal-" + kind.ToString().ToLowerInvariant(), iterations, matches.Length,
                        async _ => VerifyQuery(await ordinalStore.QueryOrdinalAsync(kind), expectedQuery));
                }
            }
            if (queriesOnly)
            {
                await store.ReopenAsync();
                await ValidateAsync();
                return;
            }
            var probe = await ((IExistenceGateStore)store).PrepareExistenceProbeAsync(id);
            for (var i = 0; i < 16; i++) if (!await probe()) throw new InvalidOperationException("Missing existence probe.");
            await Measure("exists-known-key", 2000, 1, async _ =>
            {
                if (!await probe()) throw new InvalidOperationException("Existence result changed.");
            });

            var updates = Enumerable.Range(0, mutations).Select(i => source[(int)((long)i * records / mutations)]
                with { Age = 100, Content = $"updated content {i}", Revision = 1 }).ToArray();
            // First mutation warms the update path. Timed mutations cover distinct evenly spaced keys.
            var warmupUpdate = updates[0] with { Age = 99, Content = "update warmup", Revision = 0 };
            if (!await store.UpdateAsync(warmupUpdate)) throw new InvalidOperationException("Warmup update failed.");
            expected[updates[0].Id] = warmupUpdate;
            await Measure("update-indexed-fields", mutations, 1, async index =>
            {
                if (!await store.UpdateAsync(updates[index])) throw new InvalidOperationException("Update failed.");
            });
            foreach (var updated in updates) expected[updated.Id] = updated;
            await ValidateAsync();
            var rangeExpected = expected.Values.Where(record => Matches(record, GateQueryKind.Range, id)).ToArray();
            VerifyQuery(await store.QueryAsync(GateQueryKind.Range, id),
                new(rangeExpected.Length, rangeExpected.Sum(record => record.MaterializedChecksum)));
            // Verify updated fields from durable storage before deleting the changed rows.
            await store.ReopenAsync();
            await ValidateAsync();

            await Measure("delete-by-id", mutations, 1, async index =>
            {
                if (!await store.DeleteAsync(updates[index].Id)) throw new InvalidOperationException("Delete failed.");
            });
            foreach (var updated in updates) expected.Remove(updated.Id);
            if (expected.ContainsKey(id))
            {
                if (!await store.DeleteAsync(id)) throw new InvalidOperationException("Probe delete failed.");
                expected.Remove(id);
            }
            if (await probe()) throw new InvalidOperationException("Deleted key still exists.");
            await store.ReopenAsync();
            await ValidateAsync();
            storage.Add(new { store.EngineName, store.CacheModeName, RemainingRecords = expected.Count, Stats = store.GetStorageStats() });

            async Task ValidateAsync()
            {
                var actual = await store.SnapshotAsync();
                if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    throw new InvalidOperationException("Full snapshot/count/field verification failed.");
            }
            async Task Measure(string operation, int count, int resultCount, Func<int, ValueTask> action)
            {
                var samples = new double[count];
                var allocated = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < count; i++)
                {
                    var timestamp = Stopwatch.GetTimestamp();
                    await action(i);
                    samples[i] = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
                }
                allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                var sorted = samples.Order().ToArray();
                var metric = new Metric(store.EngineName, store.CacheModeName, operation, records,
                    resultCount, samples, (double)allocated / count,
                    sorted[(int)Math.Ceiling(count * .50) - 1], sorted[(int)Math.Ceiling(count * .95) - 1], samples.Average());
                metrics.Add(metric);
                Console.WriteLine($"  {operation}: p50 {metric.P50Ms:F4} ms; p95 {metric.P95Ms:F4} ms");
            }
        }
    }
    private static void VerifyQuery(GateQueryResult actual, GateQueryResult expected)
    {
        if (actual != expected) throw new InvalidOperationException($"Query returned {actual}; expected {expected}.");
    }
    private static bool Matches(GateRecord record, GateQueryKind kind, string id) => kind switch
    {
        GateQueryKind.Exact => record.Id == id,
        GateQueryKind.Range => record.Age >= 75,
        GateQueryKind.Prefix => record.Name.StartsWith("customer-00000", StringComparison.Ordinal),
        GateQueryKind.Suffix => record.Name.EndsWith("special-tail", StringComparison.Ordinal),
        GateQueryKind.Contains => record.Content.Contains("rare-needle", StringComparison.Ordinal),
        GateQueryKind.BroadConjunction => record.Role == "student" && record.Age >= 40,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
