using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Transport;

var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var rows = new List<object>();
string? dataPath = null;
if (args[0] == "micro")
{
    foreach (var (total, tenants) in new[] { (10_000, 100), (100_000, 100), (1_000_000, 1000), (100_000, 1) })
    {
        var prefixes = Enumerable.Range(0, tenants).Select(Prefix).ToArray();
        var data = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        for (int i = 0; i < total; i++) data.TryAdd(Key(prefixes[i % tenants], i / tenants), 0);
        int perTenant = total / tenants;
        string selected = prefixes[0];
        for (int round = 0; round < 4; round++)
        foreach (var variant in Order(round))
        {
            IPartitionKeyDirectory? directory = null;
            long memoryBefore = GC.GetTotalMemory(true);
            var build = Stopwatch.GetTimestamp();
            if (variant != SelectionVariant.Scan)
            {
                directory = variant == SelectionVariant.Lazy
                    ? new LazyPartitionKeyDirectory(() => data.Keys) : new PartitionKeyDirectory(variant);
                if (variant != SelectionVariant.Lazy) directory.Rebuild(data.Keys);
            }
            double buildMs = Stopwatch.GetElapsedTime(build).TotalMilliseconds;
            long memory = GC.GetTotalMemory(true) - memoryBefore;
            var cursors = new string?[] { null, Key(selected, Math.Max(0, perTenant / 2)), Key(selected, perTenant - 2) };
            int cursor = 0;
            Func<Task> action = () =>
            {
                var after = cursors[cursor++ % cursors.Length];
                var page = directory is null ? Scan(data, selected, after, 65) : directory.Select(selected, after, 65);
                int first = after is null ? 0 : int.Parse(after[^9..]) + 1;
                Check(page.Count == perTenant && page.Keys.Length == Math.Min(65, perTenant - first), "Micro count");
                for (int n = 0; n < page.Keys.Length; n++) Check(page.Keys[n] == Key(selected, first + n), "Micro key");
                return Task.CompletedTask;
            };
            // Correctness above intentionally uses strings; timed selection below only consumes a checksum.
            var coldStart = Stopwatch.GetTimestamp(); await action();
            double firstSelectMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
            await action(); await action();
            long retainedAfterFirstSelect = GC.GetTotalMemory(true) - memoryBefore;
            long checksum = 0;
            var timing = await Measure(() =>
            {
                var after = cursors[cursor++ % cursors.Length];
                var page = directory is null ? Scan(data, selected, after, 65) : directory.Select(selected, after, 65);
                checksum += page.Count + page.Keys.Length;
                return Task.CompletedTask;
            }, variant == SelectionVariant.Scan || (variant == SelectionVariant.Lazy && perTenant > 10_000) ? 30 : 3000);
            Timing? edit = null;
            if (directory is not null)
            {
                EncodedMutation[] insert = [new(Key(selected, perTenant + 1), [1])];
                EncodedMutation[] delete = [new(Key(selected, perTenant + 1), null)];
                edit = await Measure(() => { directory.Apply(insert); directory.Apply(delete); return Task.CompletedTask; }, 200);
            }
            rows.Add(new { kind = "micro", total, tenants, perTenant, round, variant = variant.ToString(),
                buildMs, firstSelectMs, retainedDirectoryBytes = memory, retainedAfterFirstSelect, page = timing, insertDeleteIndexOnly = edit, checksum });
            GC.KeepAlive(directory);
            Console.WriteLine($"micro {total}/{tenants} {round} {variant}: {timing.P50Ms:F5} ms, {memory} bytes");
        }
    }
}
else if (args[0] == "storage")
{
    int total = int.Parse(args[2]), tenants = 100, perTenant = total / tenants;
    var path = Path.Combine(Path.GetDirectoryName(output)!, "Data", Guid.NewGuid().ToString("N"));
    dataPath = path;
    var limits = new TransportLimits { MaximumBatchItems = 1024, MaximumPageItems = 1024 };
    Environment.SetEnvironmentVariable("IDB_EXPERIMENT_INDEX", "Scan");
    await using (var seed = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, limits, total + 1000, retainedChangeCount: 64, maximumPartitionKeys: perTenant + 100))
    {
        for (int tenant = 0; tenant < tenants; tenant++)
        for (int start = 0; start < perTenant; start += 512)
        {
            var prefix = Prefix(tenant);
            await seed.ExecuteAsync(new() { Command = EncodedStoreCommand.ApplyBatch, Partition = prefix,
                Mutations = Enumerable.Range(start, Math.Min(512, perTenant - start)).Select(n => new EncodedMutation(Key(prefix, n), Payload(tenant, n))).ToArray() });
        }
        await seed.ExecuteAsync(new() { Command = EncodedStoreCommand.Checkpoint });
    }
    Console.WriteLine($"seeded durable {total} records: {path}");
    for (int round = 0; round < 4; round++)
    foreach (var variant in Order(round))
    {
        Environment.SetEnvironmentVariable("IDB_EXPERIMENT_INDEX", variant.ToString());
        var opened = Stopwatch.GetTimestamp();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, limits, total + 1000, retainedChangeCount: 64, maximumPartitionKeys: perTenant + 100);
        double openMs = Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
        string partition = Prefix(0);
        var cursors = new string?[] { null, Key(partition, perTenant / 2), Key(partition, perTenant - 2) };
        int cursor = 0;
        async Task Page()
        {
            string? after = cursors[cursor++ % cursors.Length];
            var response = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, Partition = partition, AfterKey = after, PageSize = 64 });
            int first = after is null ? 0 : int.Parse(after[^9..]) + 1;
            Validate(response, partition, 0, first, Math.Min(64, perTenant - first), perTenant);
        }
        async Task Full()
        {
            int first = 0; string? after = null;
            do
            {
                var response = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, Partition = partition, AfterKey = after, PageSize = 128 });
                int count = Math.Min(128, perTenant - first);
                Validate(response, partition, 0, first, count, perTenant);
                first += count; after = response.NextAfterKey;
            } while (after is not null);
            Check(first == perTenant, "Full enumeration count");
        }
        var coldStart = Stopwatch.GetTimestamp(); await Page();
        double firstPageMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        var page = await Measure(Page, 90);
        var full = await Measure(Full, 12);
        int updateAt = 0;
        var updates = await Measure(async () =>
        {
            int n = updateAt++ % perTenant;
            await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Put, Partition = partition, Key = Key(partition, n), Payload = Payload(0, n) });
        }, 24);
        var mutationKey = Key(partition, perTenant + 10);
        var insertDelete = await Measure(async () =>
        {
            await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Put, Partition = partition, Key = mutationKey, Payload = Payload(0, perTenant + 10) });
            await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Delete, Partition = partition, Key = mutationKey });
        }, 24);
        Check(!(await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Get, Partition = partition, Key = mutationKey })).Found, "Delete remains deleted");
        await Full();
        // Check another tenant with identical logical keys and different payloads.
        var other = await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, Partition = Prefix(1), PageSize = 64 });
        Validate(other, Prefix(1), 1, 0, Math.Min(64, perTenant), perTenant);
        rows.Add(new { kind = "durable-adapter", total, tenants, perTenant, round, variant = variant.ToString(),
            openMs, firstPageMs, page, fullTenant = full, update = updates, insertDeletePair = insertDelete });
        Console.WriteLine($"durable {total} {round} {variant}: page {page.P50Ms:F4} ms; full {full.P50Ms:F3}; update {updates.P50Ms:F3}");
    }
    // An independent final reopen verifies the repeated updates/deletes persisted.
    Environment.SetEnvironmentVariable("IDB_EXPERIMENT_INDEX", "Scan");
    await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, limits, total + 1000, retainedChangeCount: 64, maximumPartitionKeys: perTenant + 100);
    for (int tenant = 0; tenant < tenants; tenant++)
    {
        var prefix = Prefix(tenant);
        var response = await reopened.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, Partition = prefix, PageSize = 1 });
        Validate(response, prefix, tenant, 0, 1, perTenant);
    }
}
else if (args[0] == "memory")
{
    int total = int.Parse(args[2]);
    string path = args[3], variant = args[4];
    Environment.SetEnvironmentVariable("IDB_EXPERIMENT_INDEX", variant);
    long before = GC.GetTotalMemory(true);
    var started = Stopwatch.GetTimestamp();
    await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path,
        new TransportLimits { MaximumBatchItems = 1024, MaximumPageItems = 1024 }, total + 1000,
        retainedChangeCount: 64, maximumPartitionKeys: total / 100 + 100);
    double openMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    long retainedOpenBytes = GC.GetTotalMemory(true) - before;
    for (int tenant = 0; tenant < 2; tenant++)
        await store.ExecuteAsync(new() { Command = EncodedStoreCommand.Enumerate, Partition = Prefix(tenant), PageSize = 1 });
    long retainedAfterTwoPartitionsBytes = GC.GetTotalMemory(true) - before;
    rows.Add(new { kind = "fresh-process-memory", total, variant, retainedOpenBytes, retainedAfterTwoPartitionsBytes, openMs });
    GC.KeepAlive(store);
    Console.WriteLine($"memory {variant}: {retainedOpenBytes} bytes");
}
else throw new ArgumentException("Use micro, storage OUTPUT COUNT, or memory OUTPUT COUNT STORE_PATH VARIANT.");
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { timestampUtc = DateTime.UtcNow, framework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, processors = Environment.ProcessorCount, payloadBytes = 256, dataPath,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
    transportMvid = typeof(TcpStoreHost).Assembly.ManifestModule.ModuleVersionId,
    note = "Isolated experiment. Raw samples include validation in durable runs. No TCP/network/JWT timing. No production change.", rows }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);

static SelectionVariant[] Order(int round) => Enumerable.Range(0, 4).Select(n => (SelectionVariant)((n + round) % 4)).ToArray();
static string Prefix(int tenant) => DataAccessScope.Partitioned("experiment-tenant", tenant.ToString()).Partition;
static string Key(string prefix, int n) => prefix + "item-" + n.ToString("D9");
static byte[] Payload(int tenant, int n)
{
    var result = new byte[256];
    BitConverter.TryWriteBytes(result.AsSpan(), tenant);
    BitConverter.TryWriteBytes(result.AsSpan(4), n);
    result.AsSpan(8).Fill((byte)(tenant + n)); return result;
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
static void Validate(EncodedStoreResponse response, string prefix, int tenant, int first, int count, int total)
{
    Check(response.Records?.Length == count, "Page count");
    for (int n = 0; n < count; n++)
    {
        var record = response.Records![n];
        Check(record.Key == Key(prefix, first + n), "Page key/tenant");
        Check(record.Payload.AsSpan().SequenceEqual(Payload(tenant, first + n)), "Payload");
    }
    Check(response.NextAfterKey == (first + count < total && count > 0 ? Key(prefix, first + count - 1) : null), "Cursor");
}
static DirectoryPage Scan(ConcurrentDictionary<string, byte> keys, string prefix, string? after, int take)
{
    var selected = keys.Keys.ToArray().Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    Array.Sort(selected, StringComparer.Ordinal);
    int start = after is null ? 0 : Array.BinarySearch(selected, after, StringComparer.Ordinal);
    if (after is not null) start = start < 0 ? ~start : start + 1;
    return new(selected.Length, selected.AsSpan(start, Math.Min(take, selected.Length - start)).ToArray());
}
static async Task<Timing> Measure(Func<Task> action, int repetitions)
{
    for (int n = 0; n < 6; n++) await action();
    var samples = new double[repetitions];
    long allocated = GC.GetTotalAllocatedBytes(true);
    var total = Stopwatch.GetTimestamp();
    for (int n = 0; n < repetitions; n++)
    {
        var start = Stopwatch.GetTimestamp(); await action();
        samples[n] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    double elapsed = Stopwatch.GetElapsedTime(total).TotalSeconds;
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    var sorted = samples.Order().ToArray();
    return new(sorted[repetitions / 2], sorted[(int)(repetitions * .95)], repetitions / elapsed, (double)allocated / repetitions, samples);
}
internal sealed record Timing(double P50Ms, double P95Ms, double OpsPerSecond, double BytesPerOperation, double[] SamplesMs);
