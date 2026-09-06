using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace StorageBenchmarks.Notifications;

public static partial class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Contains("--micro")) { await MicroBenchmarks.RunAsync(args); return; }
        if (args.Contains("--contention")) { await ContentionBenchmarks.RunAsync(args); return; }
        string Option(string key, string fallback)
        { var index = Array.IndexOf(args, "--" + key); return index < 0 ? fallback : args[index + 1]; }
        var iterations = int.Parse(Option("iterations", "1000"));
        var repetitions = int.Parse(Option("repetitions", "5"));
        var payload = int.Parse(Option("payload", "512"));
        var output = Path.GetFullPath(Option("output", "notification-results.json"));
        var modes = Option("modes", "disabled,empty,create-off,created,delete-off,deleted,noop-off,noop,changed,filtered,changed10,changed100,changed1000").Split(',');
        var providers = Option("providers", "file,segment").Split(',');
#if NOTIFICATION_BASELINE
        const string build = "before";
        modes = modes.Where(mode => mode == "disabled" || mode.EndsWith("-off", StringComparison.Ordinal)).ToArray();
#else
        const string build = "after";
#endif
        var results = new List<Result>();
        Console.WriteLine($"{build}: {iterations} operations, {repetitions} repetitions, {payload}-byte payload");
        for (var repetition = -1; repetition < repetitions; repetition++)
        {
            // Rotate order to reduce provider/mode drift; repetition -1 is discarded warm-up.
            var cases = providers.SelectMany(provider => modes.Select(mode => (provider, mode))).ToArray();
            var offset = repetition < 0 ? 0 : repetition % cases.Length;
            foreach (var (provider, mode) in cases.Skip(offset).Concat(cases.Take(offset)))
            {
                var result = await Run(provider, mode, repetition < 0 ? Math.Min(iterations, 150) : iterations, payload);
                if (repetition < 0) continue;
                results.Add(result with { Repetition = repetition });
                Console.WriteLine($"{provider,-7} {mode,-11} #{repetition + 1} save={result.SaveMs:F3} ms end={result.EndToEndMs:F3} ms allocated={result.AllocatedBytes} delivered={result.Delivered}");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Build = build, CapturedUtc = DateTime.UtcNow, Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription, ProcessorCount = Environment.ProcessorCount,
            Iterations = iterations, PayloadBytes = payload, Repetitions = repetitions,
            Durability = "Durable; WindowsNvme provider profiles; maintenance disabled",
            Cache = "StrongObject", Indexes = "Id exact", Results = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved {output}");
    }

    private static async Task<Result> Run(string providerName, string mode, int count, int payloadLength)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-NotificationBench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
            {
                DurabilityMode = StorageDurabilityMode.Durable, ObjectCacheMode = StorageObjectCacheMode.StrongObject,
                RaiseNotifications = false, WatchStorageChanges = false, HydrateOnInitialize = false,
                UseSingleWriteBuffer = true
            };
#if !NOTIFICATION_BASELINE
            policy = policy with { RepositoryNotifications = mode == "disabled" || mode.EndsWith("-off", StringComparison.Ordinal) ? null : new()
            { QueueCapacity = count + 1, MaximumSubscribers = 1100, MaximumConcurrentCallbacks = 8 } };
#endif
            var config = new Config(root, policy, providerName == "segment" ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
            var services = new ServiceCollection();
            RegisterProvider(services, config, providerName);
            await using var serviceProvider = services.BuildServiceProvider();
            var repository = serviceProvider.GetRequiredService<IRepository<IRecord>>();
            await repository.InitializeAsync();
            var creates = mode is "created" or "create-off";
            var deletes = mode is "deleted" or "delete-off";
            var noOp = mode is "noop" or "noop-off";
            var recordCount = deletes ? count : 128;
            var records = (await repository.SaveManyAsync(Enumerable.Range(0, recordCount).Select(index => NewRecord(index, payloadLength)))).ToArray();
            long checksum = 0;
            var eventLatencies = new double[count];
            var started = new long[count];
#if !NOTIFICATION_BASELINE
            var notifications = serviceProvider.GetRequiredService<IRepositoryNotifications<IRecord>>();
            var subscribers = new List<IRepositorySubscription>();
            var sums = new long[1100];
            ValueTask Receive(IRecord entity, int receiver)
            {
                sums[receiver] += entity.Id.Length + entity.Payload.Length + entity.Value;
                var index = entity.Sequence;
                if (receiver == 0 && (uint)index < (uint)started.Length && Volatile.Read(ref started[index]) is var start && start != 0)
                    eventLatencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return ValueTask.CompletedTask;
            }
            var subscriberCount = mode switch { "changed10" => 10, "changed100" => 100, "changed1000" => 1000, "disabled" or "empty" or "create-off" or "delete-off" or "noop-off" => 0, _ => 1 };
            for (var index = 0; index < subscriberCount; index++)
            {
                var receiver = index;
                subscribers.Add(mode switch
                {
                    "created" => notifications.SubscribeCreated(entity => Receive(entity, receiver)),
                    "deleted" => notifications.SubscribeDeleted(entity => Receive(entity, receiver)),
                    "filtered" => notifications.SubscribeChanged(change => Receive(change.Entity, receiver), entity => entity.Value < 0),
                    _ => notifications.SubscribeChanged(change => Receive(change.Entity, receiver), entity => entity.Value >= 0)
                });
            }
#endif
            var latencies = new double[count];
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var memoryBefore = GC.GetTotalMemory(false);
            var peakMemory = memoryBefore;
            var gen0 = GC.CollectionCount(0);
            var begin = Stopwatch.GetTimestamp();
            for (var index = 0; index < count; index++)
            {
                var entity = creates ? NewRecord(index + 100_000, payloadLength) : records[index % records.Length];
                if (!noOp && !deletes) { entity.Value++; entity.Sequence = index; }
                var start = Stopwatch.GetTimestamp();
                Volatile.Write(ref started[index], start);
                if (deletes) await repository.DeleteAsync(entity);
                else await repository.SaveAsync(entity);
                latencies[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if ((index & 63) == 0) peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(false));
            }
            var saveMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            long delivered = 0, filtered = 0;
#if !NOTIFICATION_BASELINE
            foreach (var subscriber in subscribers)
            {
                await subscriber.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromMinutes(2));
                if (subscriber.IsDisposed) throw new InvalidOperationException($"Benchmark subscriber failed: {await subscriber.Completion}");
                delivered += subscriber.DeliveredCount; filtered += subscriber.FilteredCount;
            }
            var expected = mode is "filtered" or "noop" ? 0L : (long)count * subscriberCount;
            if (delivered != expected) throw new InvalidOperationException($"Expected {expected} notifications, received {delivered}.");
#endif
            var totalMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(false));
            var gen0Delta = GC.CollectionCount(0) - gen0;
            var retained = GC.GetTotalMemory(true) - memoryBefore;
#if !NOTIFICATION_BASELINE
            checksum = sums.Sum();
            foreach (var subscriber in subscribers) subscriber.Dispose();
#endif
            Array.Sort(latencies);
            var events = eventLatencies.Where(value => value > 0).Order().ToArray();
            GC.KeepAlive(checksum);
            return new(providerName, mode, 0, count, saveMs, totalMs, totalMs - saveMs,
                Percentile(latencies, .5), Percentile(latencies, .95), Percentile(latencies, .99),
                Percentile(events, .95), allocated, peakMemory - memoryBefore, retained, gen0Delta, delivered, filtered);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static void RegisterProvider(IServiceCollection services, Config config, string provider)
    {
        var handled = false;
        RegisterExternalProvider(services, config, provider, ref handled);
        if (handled) return;
        if (provider == "segment")
            services.AddSegmentCoreRepository(config, SegmentCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
            { EnableBackgroundMaintenance = false, CheckpointOnDispose = false });
        else if (provider == "file") services.AddFileRepository(config);
        else throw new ArgumentException($"Unknown benchmark provider: {provider}");
    }

    // A separately built, non-packable harness can link this driver and add a provider.
    static partial void RegisterExternalProvider(IServiceCollection services, Config config, string provider, ref bool handled);

    private static double Percentile(double[] values, double percentile)
        => values.Length == 0 ? 0 : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];
    private static Record NewRecord(int index, int payload) => new()
    { Id = "record-" + index.ToString(CultureInfo.InvariantCulture), Payload = new byte[payload], Sequence = index };
    public sealed record Result(string Provider, string Mode, int Repetition, int Operations, double SaveMs,
        double EndToEndMs, double DrainMs, double SaveP50Ms, double SaveP95Ms, double SaveP99Ms,
        double NotificationP95Ms, long AllocatedBytes, long PeakManagedDeltaBytes, long RetainedManagedDeltaBytes,
        int Gen0Collections, long Delivered, long Filtered);
    public sealed record Config(string DataRoot, StoragePolicy StoragePolicy, ContractRepositoryType RepositoryType) : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey => "notification-bench";
        public string ContractName => "notification-bench";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
    }
    [StorageScope(VersionTag = "1.0.0")]
    [SearchIndex]
    public interface IRecord : IStoredContract<IRecord>
    {
        [SearchIndex(SearchIndexMode.Exact)] string Id { get; set; }
        int Value { get; set; }
        int Sequence { get; set; }
        byte[] Payload { get; set; }
    }
    public sealed class Record : IRecord
    {
        public string Id { get; set; } = "";
        public int Value { get; set; }
        public int Sequence { get; set; }
        public byte[] Payload { get; set; } = [];
        public Idb.Libraries.Abstractions.Services.Serialization.IObjectEnvelope<IRecord>? Envelope { get; private set; }
    }
}
