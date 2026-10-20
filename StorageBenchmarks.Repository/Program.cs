using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore;

if (args.Contains("--review-baseline"))
{
    await ReviewBaseline.RunAsync(args);
    return;
}

if (!args.Contains("--run") && !args.Contains("--smoke"))
{
    Console.WriteLine("Use --smoke for correctness only or --run for timings. Options: --records 2000 --iterations 7 --warmup 2 --batch 128 --payload-bytes 512 --read-concurrency 4 --engines FileSystem,SegmentCore --cache-modes MetadataOnly,WeakObject,StrongObject --label current --output report.json --data-root directory --keep-data");
    return;
}

var options = RunOptions.Parse(args);
await using var telemetry = new BenchmarkTelemetry();
var parent = options.DataRoot ?? Path.GetTempPath();
Directory.CreateDirectory(parent);
var dataRoot = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(parent), $"repository-benchmark-{Guid.NewGuid():N}")).FullName;
var metrics = new List<Measurement>();
try
{
    foreach (var engine in options.Engines)
        foreach (var cache in options.CacheModes)
        {
            Console.Error.WriteLine($"Validating {engine}/{cache} ({options.Records} complete objects)...");
            var root = Path.Combine(dataRoot, engine.ToString(), cache.ToString());
            var config = new BenchConfig(root, engine, cache, options, telemetry.Service);
            var expected = Enumerable.Range(0, options.Records).Select(index => BenchRecord.Create(index, options.PayloadBytes)).ToArray();
            await using (var seed = await BenchSession.OpenAsync(config))
            {
                var written = await seed.Repository.SaveManyAsync(expected);
                Check(Consume(written), Consume(expected), "seed");
            }

            BenchSession? opening = null;
            await MeasureAsync("repository-open", async () =>
            {
                opening = await BenchSession.OpenAsync(config);
                return new ReadResult(0, 0);
            }, new ReadResult(0, 0), async () =>
            {
                if (opening is not null) { await opening.DisposeAsync(); opening = null; }
            });

            await using var session = await BenchSession.OpenAsync(config);
            var repository = session.Repository;
            var threshold = options.Records * 3 / 4;
            var rangeExpected = Consume(expected.Where(record => record.Ordinal >= threshold));
            await MeasureAsync("indexed-range-fresh-expression", () => ReadAsync(repository.SearchManyAsync(record => record.Ordinal >= threshold)), rangeExpected);
            var upperBound = options.Records;
            await MeasureAsync("indexed-bounded-range-fresh-expression", () => ReadAsync(repository.SearchManyAsync(
                record => record.Ordinal >= threshold && record.Ordinal < upperBound)), rangeExpected);
            Expression<Func<IBenchRecord, bool>> reusedRange = record => record.Ordinal >= threshold && record.Ordinal < upperBound;
            await MeasureAsync("indexed-bounded-range-reused-expression", () => ReadAsync(repository.SearchManyAsync(reusedRange)), rangeExpected);
            await MeasureAsync("indexed-first-exact", async () =>
            {
                var record = await repository.SearchFirstAsync(record => record.Ordinal == threshold)
                    ?? throw new InvalidOperationException("First-result query lost its expected row.");
                return Consume([record]);
            }, Consume([expected[threshold]]));
            await MeasureAsync("scan-all", () => ReadAsync(repository.QueryManyAsync(static _ => true)), Consume(expected));
            await MeasureAsync("scan-implementation", () => ReadAsync(repository.QueryManyAsync<BenchRecord>(static _ => true)), Consume(expected));

            if (cache == StorageObjectCacheMode.WeakObject)
            {
                var pins = new List<IBenchRecord>();
                await foreach (var record in repository.SearchManyAsync(record => record.Ordinal >= threshold)) { pins.Add(record); }
                await MeasureAsync("indexed-range-weak-pinned", () => ReadAsync(repository.SearchManyAsync(record => record.Ordinal >= threshold)), rangeExpected);
                GC.KeepAlive(pins);
            }

            var updates = new List<IBenchRecord>();
            await foreach (var record in repository.SearchManyAsync(record => record.Ordinal < options.BatchSize)) { updates.Add(record); }
            var revision = 0;
            await MeasureUpdateAsync("save-batch-unchanged-indexes", changeIndexedField: false);
            await MeasureUpdateAsync("save-batch-changed-indexes", changeIndexedField: true);

            // Reopen after all measured writes and verify every complete object outside timing.
            await session.DisposeAsync();
            await using (var verify = await BenchSession.OpenAsync(config))
            {
                var finalExpected = expected.Cast<IBenchRecord>().ToDictionary(static record => record.Ordinal);
                foreach (var updated in updates) { finalExpected[updated.Ordinal] = updated; }
                Check(await ReadAsync(verify.Repository.QueryManyAsync(static _ => true)), Consume(finalExpected.Values), "final durable reopen");
            }

            async Task MeasureUpdateAsync(string operation, bool changeIndexedField)
            {
                for (var iteration = -options.Warmup; iteration < options.Iterations; iteration++)
                {
                    foreach (var record in updates)
                    {
                        record.Revision = ++revision;
                        if (changeIndexedField) { record.Group = (record.Group + 1) % 17; }
                    }
                    var expectedWrite = Consume(updates);
                    await CaptureAsync(operation, iteration, async () => Consume(await repository.SaveManyAsync(updates)), expectedWrite);
                }
            }

            async Task MeasureAsync(string operation, Func<Task<ReadResult>> action, ReadResult expectedResult, Func<Task>? after = null)
            {
                for (var iteration = -options.Warmup; iteration < options.Iterations; iteration++)
                {
                    if (cache == StorageObjectCacheMode.WeakObject) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                    await CaptureAsync(operation, iteration, action, expectedResult);
                    if (after is not null) { await after(); }
                }
            }

            async Task CaptureAsync(string operation, int iteration, Func<Task<ReadResult>> action, ReadResult expectedResult)
            {
                var allocationStart = options.Smoke ? 0 : GC.GetTotalAllocatedBytes(precise: true);
                var start = options.Smoke ? 0 : Stopwatch.GetTimestamp();
                var result = await action();
                double? milliseconds = options.Smoke ? null : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long? allocated = options.Smoke ? null : GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
                Check(result, expectedResult, operation);
                if (iteration >= 0) { metrics.Add(new(engine.ToString(), cache.ToString(), operation, iteration, result.Count, result.Checksum, milliseconds, allocated)); }
            }
        }

    await telemetry.DisposeAsync();
    var report = new
    {
        Label = options.Label,
        Telemetry = telemetry.Statistics,
        TimingsRecorded = !options.Smoke,
        Runtime = RuntimeInformation.FrameworkDescription,
        OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        StartedDataRoot = dataRoot,
        Options = options,
        Assemblies = new[] { typeof(IRepository<>).Assembly, typeof(SegmentCoreStore).Assembly }
            .Select(assembly => new { assembly.GetName().Name, Mvid = assembly.ManifestModule.ModuleVersionId, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
        Notes = new[]
        {
            "Full IRepository + configured indexes + envelope serialization + durable storage. Every result consumes every domain field and payload byte; checksum/count failures abort.",
            "Each case is sequential. File durability uses durable proposals/sidecar locks; SegmentCore uses mutation WAL + explicit flush. Commit boundaries differ: file bulk is per-object, segment engines transact the batch.",
            "Repository-open includes DI construction, store recovery and repository/index initialization; disposal/checkpoint outside timing. OS page cache is not evicted.",
            "Bounded-range cases use identical values/results with a fresh expression per call versus one reused expression. Syntax caching is not result caching; execution still consumes full objects.",
            "Weak-object read cases force full GC before timing, except external pins intentionally survive for indexed-range-weak-pinned. Strong-object cases reuse hydrated objects; MetadataOnly materializes fresh objects.",
            "Save inputs and field changes are constructed outside timing. Save results consume full fields inside timing. Background maintenance and file watching are disabled. Allocation uses process-wide counters because operations are asynchronous.",
            "Percentiles are nearest-rank; few samples cannot establish tail guarantees. Run on an otherwise idle machine, alternate baseline/current order, preserve each report's assembly fingerprints. No SQLite comparison is claimed."
        },
        Metrics = metrics,
        Summary = metrics.GroupBy(metric => new { metric.Engine, metric.CacheMode, metric.Operation }).Select(group => new
        {
            group.Key.Engine,
            group.Key.CacheMode,
            group.Key.Operation,
            Samples = group.Count(),
            ObjectsPerSample = group.First().Count,
            P50Milliseconds = Percentile(group.Select(metric => metric.Milliseconds), .50),
            P95Milliseconds = Percentile(group.Select(metric => metric.Milliseconds), .95),
            P99Milliseconds = Percentile(group.Select(metric => metric.Milliseconds), .99),
            MeanAllocatedBytes = options.Smoke ? (double?)null : group.Average(metric => metric.AllocatedBytes!.Value)
        }).ToArray()
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (options.Output is not null)
    {
        var output = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, json);
        Console.WriteLine(output);
    }
    else { Console.WriteLine(json); }
}
finally
{
    if (!options.KeepData && Directory.Exists(dataRoot)) { Directory.Delete(dataRoot, recursive: true); }
}

static async Task<ReadResult> ReadAsync(IAsyncEnumerable<IBenchRecord> source)
{
    var materialized = new List<IBenchRecord>();
    await foreach (var record in source) { materialized.Add(record); }
    var result = Consume(materialized);
    GC.KeepAlive(materialized);
    return result;
}

static ReadResult Consume(IEnumerable<IBenchRecord> records)
{
    var count = 0;
    long checksum = 0;
    foreach (var record in records) { count++; checksum += BenchRecord.Checksum(record); }
    return new(count, checksum);
}

static void Check(ReadResult actual, ReadResult expected, string operation)
{
    if (actual != expected) { throw new InvalidDataException($"{operation}: complete-object result {actual} != expected {expected}."); }
}

static double? Percentile(IEnumerable<double?> source, double percentile)
{
    var values = source.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
    return values.Length == 0 ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];
}

internal readonly record struct ReadResult(int Count, long Checksum);
internal sealed record Measurement(string Engine, string CacheMode, string Operation, int Iteration, int Count, long Checksum, double? Milliseconds, long? AllocatedBytes);
