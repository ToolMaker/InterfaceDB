using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;

// Diagnostic measurements, not wall-clock assertions. All operations verify complete results.
internal static class ReviewBaseline
{
    public static async Task RunAsync(string[] args)
    {
        var options = RunOptions.Parse(args);
        var limitIndex = Array.IndexOf(args, "--update-limit");
        var updateLimit = limitIndex < 0 ? 10_000 : int.Parse(args[limitIndex + 1]);
        if (updateLimit < 1) throw new ArgumentOutOfRangeException(nameof(updateLimit));
        var parent = Path.GetFullPath(options.DataRoot ?? Path.GetTempPath());
        var root = Path.Combine(parent, $"review-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var samples = new List<Sample>();
        var started = DateTimeOffset.UtcNow;
        try
        {
            foreach (var engine in options.Engines)
            foreach (var cache in options.CacheModes)
            {
                Console.Error.WriteLine($"{options.Label}: {engine}/{cache}, {options.Records} records");
                var config = new BenchConfig(Path.Combine(root, engine.ToString(), cache.ToString()), engine, cache, options);
                var counter = new CountingSerializer(new BinaryObjectSerializer<IBenchRecord>());
                await using var session = await BenchSession.OpenAsync(config, counter);
                var repository = session.Repository;
                var hashes = new uint[options.Records];
                List<IBenchRecord>? pins = await SeedAsync(repository, hashes, options);
                var target = options.Records / 2;
                var rangeEnd = Math.Min(options.Records, target + 10);
                long rangeHash = 0;
                for (var i = target; i < rangeEnd; i++) rangeHash += hashes[i];

                await ReadsAsync("point", async () =>
                {
                    var record = await repository.SearchFirstAsync(r => r.Ordinal == target)
                        ?? throw new InvalidDataException("Missing point result.");
                    return new(1, BenchRecord.Checksum(record));
                }, new(1, hashes[target]));
                await ReadsAsync("range-10", () => ConsumeAsync(repository.SearchManyAsync(
                    r => r.Ordinal >= target && r.Ordinal < rangeEnd)), new(rangeEnd - target, rangeHash));

                // The application holds these objects, so WeakObject is intentionally pinned here.
                var existsSamples = options.Smoke ? 16 : Math.Max(2_000, options.Iterations);
                for (var i = -16; i < existsSamples; i++)
                {
                    var record = pins[(int)((long)(i + 16) * 7919 % pins.Count)];
                    await CaptureAsync("exists", i, async () => new(
                        await repository.ExistsAsync(record) ? 1 : 0, 0), new(1, 0));
                }

                if (cache == StorageObjectCacheMode.MetadataOnly)
                {
                    // Warm code paths before measuring; do not warm the full working set away.
                    for (var i = 0; i < Math.Min(64, pins.Count); i++) await UpdateAsync(i, -1);
                    var count = Math.Min(updateLimit, pins.Count);
                    for (var i = 0; i < count; i++)
                        await UpdateAsync((int)((long)i * pins.Count / count), i);
                    async Task UpdateAsync(int index, int iteration)
                    {
                        var record = pins[index];
                        record.Group = (record.Group + 1) % 17;
                        record.Revision++;
                        hashes[index] = BenchRecord.Checksum(record);
                        await CaptureAsync("update-single", iteration, async () =>
                        {
                            pins[index] = await repository.SaveAsync(record);
                            return new(1, BenchRecord.Checksum(pins[index]));
                        }, new(1, hashes[index]));
                    }
                }
                if (cache == StorageObjectCacheMode.WeakObject)
                {
                    // A completed async seed operation can retain its returned list. Clear
                    // its contents as well as dropping our reference before measuring GC.
                    pins.Clear();
                    pins = null;
                    await session.DisposeAsync();
                    await ReadsAsync("point-collected", async () =>
                    {
                        var record = await repository.SearchFirstAsync(r => r.Ordinal == target)
                            ?? throw new InvalidDataException("Missing point result after GC.");
                        return new(1, BenchRecord.Checksum(record));
                    }, new(1, hashes[target]), collect: true);
                    await ReadsAsync("range-10-collected", () => ConsumeAsync(repository.SearchManyAsync(
                        r => r.Ordinal >= target && r.Ordinal < rangeEnd)), new(rangeEnd - target, rangeHash), collect: true);
                }
                GC.KeepAlive(pins);
                await session.DisposeAsync();
                await using var reopened = await BenchSession.OpenAsync(config);
                var final = await ConsumeAsync(reopened.Repository.QueryManyAsync(static _ => true));
                Check(final, new(options.Records, hashes.Sum(x => (long)x)), "durable reopen");

                async Task ReadsAsync(string name, Func<ValueTask<ReadResult>> action, ReadResult expected, bool collect = false)
                {
                    for (var i = -options.Warmup; i < options.Iterations; i++)
                    {
                        await using var fresh = collect ? await BenchSession.OpenAsync(config, counter) : null;
                        if (collect)
                        {
                            repository = fresh!.Repository;
                            await Task.Yield();
                            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        }
                        var decodes = counter.Deserializations;
                        await CaptureAsync(name, i, action, expected);
                        // A surviving weak-cache entry needs one caller copy; a collected
                        // entry can also materialize an independent private cache copy.
                        // Forced GC does not guarantee identical temporary-root lifetimes
                        // across JITs. Require fresh results without rejecting that copy.
                        var materializations = counter.Deserializations - decodes;
                        if (collect && (materializations < expected.Count || materializations > 2L * expected.Count))
                            throw new InvalidDataException($"{name}: {materializations} deserializations for {expected.Count} results at iteration {i}; expected one per result plus at most one private cache copy.");
                    }
                }

                async Task CaptureAsync(string operation, int iteration, Func<ValueTask<ReadResult>> action, ReadResult expected)
                {
                    var reads = counter.RevisionReads;
                    var decodes = counter.Deserializations;
                    var allocation = options.Smoke ? 0 : GC.GetTotalAllocatedBytes(precise: true);
                    var start = Stopwatch.GetTimestamp();
                    var actual = await action();
                    double? elapsed = options.Smoke ? null : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long? allocated = options.Smoke ? null : GC.GetTotalAllocatedBytes(precise: true) - allocation;
                    Check(actual, expected, operation);
                    if (iteration >= 0)
                        samples.Add(new(engine.ToString(), cache.ToString(), operation, iteration, elapsed, allocated,
                            counter.RevisionReads - reads, counter.Deserializations - decodes));
                }
            }

            var report = new
            {
                SchemaVersion = 1, options.Label, StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                Options = options, UpdateLimit = updateLimit, TimingsRecorded = !options.Smoke,
                Assemblies = new[] { typeof(ReviewBaseline).Assembly, typeof(IRepository<>).Assembly, typeof(SegmentCoreStore).Assembly }
                    .Select(a => new { a.GetName().Name, Mvid = a.ManifestModule.ModuleVersionId,
                        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
                Notes = new[]
                {
                    "Durable repository operations; background maintenance, notifications, file watching and telemetry disabled.",
                    "Point/range results consume every field/payload byte. Every save and final full-store reopen is verified.",
                    "Strong and pinned-weak caches retain all seeded objects. Collected-weak cases reopen and force GC outside timing before EACH query; require one materialization per result plus at most one independent private cache copy. GC does not guarantee every temporary root is dead.",
                    "Updates use MetadataOnly, warm 64 records, then save min(records,update-limit) evenly spaced distinct records once per process.",
                    "Search warmup/iterations are explicit. Exists has 16 warmups and at least 2000 measured calls (16 in smoke mode).",
                    "Allocations are process-wide; asynchronous/background runtime work can contribute. No OS cache eviction.",
                    "Nearest-rank p50/p95/p99 are within-run operation percentiles, not confidence intervals or production guarantees.",
                    "Use at least three separate process runs per build, alternating order, with no competing tests/builds."
                },
                Samples = samples,
                Summary = samples.GroupBy(s => new { s.Engine, s.CacheMode, s.Operation }).Select(g => new
                {
                    g.Key.Engine, g.Key.CacheMode, g.Key.Operation, Samples = g.Count(),
                    P50Milliseconds = Percentile(g.Select(s => s.Milliseconds), .5),
                    P95Milliseconds = Percentile(g.Select(s => s.Milliseconds), .95),
                    P99Milliseconds = Percentile(g.Select(s => s.Milliseconds), .99),
                    MeanMilliseconds = options.Smoke ? (double?)null : g.Average(s => s.Milliseconds),
                    MeanAllocatedBytes = options.Smoke ? (double?)null : g.Average(s => s.AllocatedBytes),
                    RevisionReads = g.Sum(s => s.RevisionReads), Deserializations = g.Sum(s => s.Deserializations)
                }).ToArray()
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (options.Output is { } output)
            {
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, json);
                Console.WriteLine(output);
            }
            else Console.WriteLine(json);
        }
        finally
        {
            if (!options.KeepData) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<List<IBenchRecord>> SeedAsync(IRepository<IBenchRecord> repository, uint[] hashes, RunOptions options)
    {
        var saved = new List<IBenchRecord>(options.Records);
        for (var offset = 0; offset < options.Records; offset += 1000)
        {
            var input = Enumerable.Range(offset, Math.Min(1000, options.Records - offset))
                .Select(i => (IBenchRecord)BenchRecord.Create(i, options.PayloadBytes)).ToArray();
            foreach (var record in input) hashes[record.Ordinal] = BenchRecord.Checksum(record);
            var result = await repository.SaveManyAsync(input);
            if (result.Count != input.Length) throw new InvalidDataException("Seed count mismatch.");
            foreach (var record in result)
                if (BenchRecord.Checksum(record) != hashes[record.Ordinal]) throw new InvalidDataException("Seed payload mismatch.");
            saved.AddRange(result);
        }
        return saved;
    }

    private static async ValueTask<ReadResult> ConsumeAsync(IAsyncEnumerable<IBenchRecord> source)
    {
        var count = 0;
        long checksum = 0;
        await foreach (var record in source) { count++; checksum += BenchRecord.Checksum(record); }
        return new(count, checksum);
    }

    private static void Check(ReadResult actual, ReadResult expected, string operation)
    {
        if (actual != expected) throw new InvalidDataException($"{operation}: {actual} != {expected}");
    }

    private static double? Percentile(IEnumerable<double?> source, double rank)
    {
        var sorted = source.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(rank * sorted.Length) - 1];
    }

    private sealed record Sample(string Engine, string CacheMode, string Operation, int Iteration,
        double? Milliseconds, long? AllocatedBytes, long RevisionReads, long Deserializations);

    private sealed class CountingSerializer(IRevisionObjectSerializer<IBenchRecord> inner) : IRevisionObjectSerializer<IBenchRecord>
    {
        private long _revisionReads, _deserializations;
        public long RevisionReads => Interlocked.Read(ref _revisionReads);
        public long Deserializations => Interlocked.Read(ref _deserializations);
        public byte[] Serialize(IObjectEnvelope<IBenchRecord> value) => inner.Serialize(value);
        public ValueTask SerializeAsync(IObjectEnvelope<IBenchRecord> value, Stream destination, CancellationToken cancellationToken = default)
            => inner.SerializeAsync(value, destination, cancellationToken);
        public long ReadRevision(ReadOnlyMemory<byte> payload)
        { Interlocked.Increment(ref _revisionReads); return inner.ReadRevision(payload); }
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _revisionReads); return inner.ReadRevisionAsync(source, cancellationToken); }
        public IObjectEnvelope<IBenchRecord> Deserialize(byte[] data)
        { Interlocked.Increment(ref _deserializations); return inner.Deserialize(data); }
        public ValueTask<IObjectEnvelope<IBenchRecord>> DeserializeAsync(Stream source, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _deserializations); return inner.DeserializeAsync(source, cancellationToken); }
    }
}
