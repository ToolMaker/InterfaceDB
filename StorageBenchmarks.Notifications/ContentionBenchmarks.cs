using System.Diagnostics;
using System.Text.Json;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace StorageBenchmarks.Notifications;

internal static class ContentionBenchmarks
{
    internal static async Task RunAsync(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex < 0 ? "notification-contention.json" : args[outputIndex + 1];
        var rows = new List<object>();
        const int count = 1000;
        var providerIndex = Array.IndexOf(args, "--providers");
        var providers = providerIndex < 0 ? new[] { "file", "segment" } : args[providerIndex + 1].Split(',');
        var repeatIndex = Array.IndexOf(args, "--repetitions");
        var repetitions = repeatIndex < 0 ? 3 : int.Parse(args[repeatIndex + 1]);
        for (var repetition = -1; repetition < repetitions; repetition++)
        foreach (var provider in providers)
        foreach (var shape in new[] { "parallel16", "batch100" })
        foreach (var mode in repetition % 2 == 0 ? new[] { "changed", "empty", "disabled" } : new[] { "disabled", "empty", "changed" })
        {
#if NOTIFICATION_BASELINE
            if (mode != "disabled") continue;
#endif
            var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-NotificationBench", Guid.NewGuid().ToString("N"));
            try
            {
                var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
                { DurabilityMode = StorageDurabilityMode.Durable, RaiseNotifications = false,
                  WatchStorageChanges = false, HydrateOnInitialize = false, ObjectCacheMode = StorageObjectCacheMode.StrongObject };
#if !NOTIFICATION_BASELINE
                policy = policy with { RepositoryNotifications = mode == "disabled" ? null : new() { QueueCapacity = count + 1 } };
#endif
                var config = new Program.Config(root, policy, provider == "segment" ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
                var services = new ServiceCollection();
                Program.RegisterProvider(services, config, provider);
                await using var di = services.BuildServiceProvider();
                var repo = di.GetRequiredService<IRepository<Program.IRecord>>();
                await repo.InitializeAsync();
                var entities = (await repo.SaveManyAsync(Enumerable.Range(0, count).Select(i =>
                    new Program.Record { Id = "probe-" + i, Payload = new byte[512] }))).ToArray();
                long delivered = 0;
#if !NOTIFICATION_BASELINE
                using var sub = mode == "changed"
                    ? di.GetRequiredService<IRepositoryNotifications<Program.IRecord>>().SubscribeChanged(change =>
                    { Interlocked.Add(ref delivered, change.Entity.Value); return ValueTask.CompletedTask; }, _ => true)
                    : null;
#endif
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var start = Stopwatch.GetTimestamp();
                if (shape == "parallel16")
                    await Task.WhenAll(Enumerable.Range(0, 16).Select(async worker =>
                    {
                        for (var i = worker; i < count; i += 16)
                        { entities[i].Value++; await repo.SaveAsync(entities[i]); }
                    }));
                else
                    for (var offset = 0; offset < count; offset += 100)
                    {
                        var batch = entities[offset..Math.Min(count, offset + 100)];
                        foreach (var entity in batch) entity.Value++;
                        await repo.SaveManyAsync(batch);
                    }
                var saveMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
#if !NOTIFICATION_BASELINE
                if (sub is not null)
                {
                    await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
                    if (sub.IsDisposed || delivered != count) throw new InvalidOperationException("Contention probe lost notifications.");
                }
#endif
                var endMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocated = GC.GetTotalAllocatedBytes(true) - allocated;
                if (repetition < 0) continue;
                rows.Add(new { Provider = provider, Shape = shape, Mode = mode, Repetition = repetition,
                    Operations = count, SaveMs = saveMs, EndToEndMs = endMs, AllocatedBytes = allocated, Delivered = delivered });
                Console.WriteLine($"{provider} {shape} {mode}: {endMs:F3} ms; {allocated} bytes");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Results = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
