using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;

namespace StorageBenchmarks.Notifications;

internal static class MicroBenchmarks
{
    internal static async Task RunAsync(string[] args)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex < 0 ? "notification-micro.json" : args[outputIndex + 1];
        var rows = new List<object>();
        Console.WriteLine($"Tiered compilation override: {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}");
#if NOTIFICATION_BASELINE
        const string build = "before";
        string[] modes = ["disabled"];
#else
        const string build = "after";
        string[] modes = ["disabled", "empty"];
#endif
        foreach (var mode in modes)
        {
            var policy = StoragePolicy.Default;
#if !NOTIFICATION_BASELINE
            policy = policy with { RepositoryNotifications = mode == "empty" ? new() : null };
#endif
            var config = new Program.Config(Path.GetTempPath(), policy, ContractRepositoryType.FileSystem);
            var store = new TrivialStore();
            var lifecycle = new EnvelopeLifecycle<Program.IRecord>(
                () => new FileObjectEnvelope<Program.IRecord> { EnvelopeCacheKey = "", VersionTag = "1.0.0" },
                new FlatFileNameFactory<Program.IRecord>(config), new EncryptionNone());
            IRepository<Program.IRecord> repository = new InterfaceRepositoryDb<Program.IRecord>(
                new PassthroughIndexManager<Program.IRecord>(store), config, policy, lifecycle, store, StorageDurabilityMode.Performance);
            var entity = new Program.Record { Id = "cpu-only" };
            _ = Exercise(repository, entity, 500_000);
            for (var repetition = 0; repetition < 9; repetition++)
            {
                const int count = 3_000_000;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var sum = Exercise(repository, entity, count);
                var ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                rows.Add(new { Mode = mode, Repetition = repetition, NanosecondsPerSave = ns, BytesPerSave = (double)bytes / count, Checksum = sum });
                Console.WriteLine($"{build} {mode} #{repetition + 1}: {ns:F3} ns/save; {(double)bytes/count:F3} bytes/save");
            }
            (repository as IDisposable)?.Dispose();
        }
#if !NOTIFICATION_BASELINE
        var serializer = new BinaryObjectSerializer<Program.IRecord>();
        var configForSnapshot = new Program.Config(Path.GetTempPath(), StoragePolicy.Default, ContractRepositoryType.FileSystem);
        var snapshotLifecycle = new EnvelopeLifecycle<Program.IRecord>(
            () => new FileObjectEnvelope<Program.IRecord> { EnvelopeCacheKey = "", VersionTag = "1.0.0" },
            new FlatFileNameFactory<Program.IRecord>(configForSnapshot), new EncryptionNone());
        foreach (var payloadLength in new[] { 512, 65_536 })
        {
            var first = new Program.Record { Id = "micro", Payload = new byte[payloadLength], Value = 1 };
            var second = new Program.Record { Id = "micro", Payload = new byte[payloadLength], Value = 2 };
            var envelope = new FileObjectEnvelope<Program.IRecord> { EnvelopeCacheKey = "micro", VersionTag = "1.0.0", ObjectInstance = first };
            var snapshot = new CommittedEnvelopeSnapshot<Program.IRecord>(serializer.Serialize(envelope), serializer, snapshotLifecycle, "micro", "micro");
            var comparer = new NotificationComparison<Program.IRecord>(new RepositoryNotificationOptions());
            var filter = NotificationFilterValidator.Compile<Program.IRecord>(record => record.Value >= 0)!;
            foreach (var stage in new[] { "snapshot-materialization", "detached-materialization", "property-diff", "predicate" })
            {
              for (var repetition = 0; repetition < 5; repetition++)
              {
                const int count = 10_000;
                int Act() => stage switch
                {
                    "snapshot-materialization" => snapshot.Materialize().Value,
                    "detached-materialization" => snapshot.Materialize(false).Value,
                    "property-diff" => comparer.Changes(first, second).Count,
                    _ => filter(first) ? 1 : 0
                };
                for (var warmup = 0; warmup < 1000; warmup++) _ = Act();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var checksum = 0;
                for (var index = 0; index < count; index++) checksum += Act();
                var ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                rows.Add(new { Stage = stage, Repetition = repetition, PayloadBytes = payloadLength, NanosecondsPerOperation = ns, BytesPerOperation = (double)bytes / count, Checksum = checksum });
                Console.WriteLine($"{stage}, {payloadLength} bytes: {ns:F3} ns/op; {(double)bytes/count:F3} allocated bytes/op");
              }
            }
        }
#endif
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Build = build, TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default", Results = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Exercise(IRepository<Program.IRecord> repository, Program.IRecord entity, int count)
    {
        long sum = 0;
        for (var index = 0; index < count; index++) sum += repository.SaveAsync(entity).GetAwaiter().GetResult().Value;
        return sum;
    }

    private sealed class TrivialStore : IEnvelopeRepository<Program.IRecord>
#if !NOTIFICATION_BASELINE
        , ICommittedEnvelopeSnapshotSource<Program.IRecord>
#endif
    {
        public ValueTask<Program.IRecord> WriteAsync(Program.IRecord entity, CancellationToken token = default)
        { entity.Value++; return ValueTask.FromResult(entity); }
        public ValueTask InitializeAsync(CancellationToken token = default) => ValueTask.CompletedTask;
        public IReaderAndWriter<Program.IRecord> ReaderAndWriter => throw new NotSupportedException();
        public bool Exists(string key) => throw new NotSupportedException();
        public bool TryGet(string key, out IReadOnlyList<IObjectEnvelope<Program.IRecord>> envelopes) => throw new NotSupportedException();
        public IAsyncEnumerable<IObjectEnvelope<Program.IRecord>> GetAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask<IObjectEnvelope<Program.IRecord>?> GetPhysicalAsync(string key, CancellationToken token = default) => throw new NotSupportedException();
        public IAsyncEnumerable<IObjectEnvelope<Program.IRecord>> EnumerateAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IAsyncEnumerable<IObjectEnvelope<Program.IRecord>> EnumerateByImplementationTypeAsync<T>(CancellationToken token = default) where T : class, Program.IRecord => throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(Program.IRecord entity, CancellationToken token = default) => throw new NotSupportedException();
        public IContractSubscription SubscribeToAdded(Func<IObjectEnvelope<Program.IRecord>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => throw new NotSupportedException();
        public IContractSubscription SubscribeToStored(Func<IObjectEnvelope<Program.IRecord>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => throw new NotSupportedException();
        public IContractSubscription SubscribeToDeleted(Func<IObjectEnvelope<Program.IRecord>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => throw new NotSupportedException();
        public IContractSubscription SubscribeToRemoved(Func<IObjectEnvelope<Program.IRecord>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => throw new NotSupportedException();
        public ValueTask ContractAdded(IObjectEnvelope<Program.IRecord> item, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ContractStored(IObjectEnvelope<Program.IRecord> item, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ContractDeleted(IObjectEnvelope<Program.IRecord> item, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask ContractRemoved(IObjectEnvelope<Program.IRecord> item, CancellationToken token = default) => throw new NotSupportedException();
#if !NOTIFICATION_BASELINE
        public ValueTask<ICommittedEnvelopeSnapshot<Program.IRecord>?> CaptureCommittedAsync(IObjectEnvelope<Program.IRecord> identity, int maximumBytes, CancellationToken token = default)
            => throw new InvalidOperationException("Zero-subscriber path must not capture snapshots.");
#endif
    }
}
