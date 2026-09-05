using System.Collections.Concurrent;
using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FileBulkLeaseOptimizationTests
{
    [TestMethod]
    public async Task DurableBulkWritesShareOneLeasePerBoundedWindow()
    {
        await using var environment = new TestEnvironment();
        var observed = new ConcurrentDictionary<string, string>();
        var serializer = new CallbackSerializer((record, _) =>
        {
            var leases = Directory.GetFiles(environment.Root, ".idb-writer.*.lock");
            Assert.HasCount(1, leases);
            observed[record.Id] = leases[0];
            return ValueTask.CompletedTask;
        });
        var writer = environment.CreateWriter(serializer);
        var records = Enumerable.Range(0, 8).Select(index => new Record { Id = index.ToString() }).ToArray();
        var results = await writer.WriteManyAsync(records);

        Assert.HasCount(8, results);
        Assert.HasCount(1, observed.Where(item => int.Parse(item.Key) < 4).Select(item => item.Value).Distinct().ToArray());
        Assert.HasCount(1, observed.Where(item => int.Parse(item.Key) >= 4).Select(item => item.Value).Distinct().ToArray());
        Assert.HasCount(2, observed.Values.Distinct().ToArray());
        Assert.IsEmpty(Directory.GetFiles(environment.Root, ".idb-writer.*.lock"));
        Assert.IsEmpty(Directory.GetFiles(environment.Root, "*.pending.*"));
    }

    [TestMethod]
    public async Task MigrationWaitsUntilEveryWorkerInTheWindowHasFinished()
    {
        await using var environment = new TestEnvironment();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var writer = environment.CreateWriter(new CallbackSerializer(async (record, token) =>
        {
            if (Interlocked.Increment(ref active) == 2) { entered.TrySetResult(); }
            await (record.Id == "first" ? releaseFirst.Task : releaseSecond.Task).WaitAsync(token);
        }));
        var writes = writer.WriteManyAsync(new[] { new Record { Id = "first" }, new Record { Id = "second" } }).AsTask();
        Task<IAsyncDisposable>? migration = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            migration = environment.CreateCoordinator().AcquireMigrationScopeAsync().AsTask();
            await WaitUntil(() => File.Exists(Path.Combine(environment.Root, ".idb-migration.lock")));
            Assert.IsFalse(migration.IsCompleted);
            releaseFirst.TrySetResult();
            await WaitUntil(() => File.Exists(Path.Combine(environment.Root, "first.mpk")));
            Assert.IsFalse(migration.IsCompleted, "Finishing one worker must not release the window's writer lease.");
            releaseSecond.TrySetResult();
            await writes;
            await using var scope = await migration.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(File.Exists(Path.Combine(environment.Root, "second.mpk")));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await writes;
            if (migration is not null) { await (await migration).DisposeAsync(); }
        }
    }

    [TestMethod]
    public async Task CanceledWindowReleasesItsLeaseAfterAllWorkersExit()
    {
        await using var environment = new TestEnvironment();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var writer = environment.CreateWriter(new CallbackSerializer(async (_, token) =>
        {
            if (Interlocked.Increment(ref active) == 2) { entered.TrySetResult(); }
            await never.Task.WaitAsync(token);
        }));
        var writes = writer.WriteManyAsync(new[] { new Record { Id = "first" }, new Record { Id = "second" } }, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => writes);
            Assert.IsEmpty(Directory.GetFiles(environment.Root, ".idb-writer.*.lock"));
            Assert.IsEmpty(Directory.GetFiles(environment.Root, "*.pending.*"));
            Assert.AreEqual(0, FileObjectWriter<Record>.RetainedIdentitySemaphoreCount);
            await using var migration = await environment.CreateCoordinator().AcquireMigrationScopeAsync();
        }
        finally
        {
            cancellation.Cancel();
            never.TrySetResult();
            try { await writes; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task PartialFailureKeepsSuccessfulCommitsAndReleasesTheWindowLease()
    {
        await using var environment = new TestEnvironment();
        var writer = environment.CreateWriter(new CallbackSerializer((record, _) =>
            record.Id == "fail" ? ValueTask.FromException(new IOException("injected serialization failure")) : ValueTask.CompletedTask));
        var error = await Assert.ThrowsExactlyAsync<StorageBulkOperationException>(() => writer.WriteManyAsync(new[]
        {
            new Record { Id = "before" }, new Record { Id = "fail" }, new Record { Id = "after" }
        }).AsTask());

        CollectionAssert.AreEqual(new[] { 0, 2 }, error.SucceededIndexes.ToArray());
        Assert.HasCount(1, error.Failures);
        Assert.AreEqual(1, error.Failures[0].Index);
        Assert.IsTrue(File.Exists(Path.Combine(environment.Root, "before.mpk")));
        Assert.IsTrue(File.Exists(Path.Combine(environment.Root, "after.mpk")));
        Assert.IsFalse(File.Exists(Path.Combine(environment.Root, "fail.mpk")));
        Assert.IsEmpty(Directory.GetFiles(environment.Root, ".idb-writer.*.lock"));
        Assert.IsEmpty(Directory.GetFiles(environment.Root, "*.pending.*"));
        await using var migration = await environment.CreateCoordinator().AcquireMigrationScopeAsync();
    }

    [TestMethod]
    public async Task CancellationWaitingForWriteSemaphoreDoesNotLeakIdentityLease()
    {
        await using var environment = new TestEnvironment();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var writer = environment.CreateWriter(new CallbackSerializer(async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 2) { entered.TrySetResult(); }
            await release.Task.WaitAsync(token);
        }));
        var first = writer.WriteAsync(new Record { Id = "first" }).AsTask();
        var second = writer.WriteAsync(new Record { Id = "second" }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var third = writer.WriteAsync(new Record { Id = "third" }, cancellation.Token).AsTask();
            await WaitUntil(() => FileObjectWriter<Record>.RetainedIdentitySemaphoreCount == 3);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => third);
            Assert.AreEqual(2, FileObjectWriter<Record>.RetainedIdentitySemaphoreCount);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }

        Assert.AreEqual(0, FileObjectWriter<Record>.RetainedIdentitySemaphoreCount);
    }

    [TestMethod]
    public async Task RecoveryInventoryCommitsReadyFilesAndCleansOnlyStaleWritingFiles()
    {
        await using var environment = new TestEnvironment();
        var ready = Path.Combine(environment.Root, "recover.mpk.pending.0001.ready");
        var deleted = Path.Combine(environment.Root, "deleted.mpk");
        var stale = Path.Combine(environment.Root, "stale.mpk.pending.0001.writing");
        var recent = Path.Combine(environment.Root, "recent.mpk.pending.0001.writing");
        var unrelated = Path.Combine(environment.Root, "other.txt.pending.0001.ready");
        await File.WriteAllTextAsync(ready, "committed");
        await File.WriteAllTextAsync(deleted, "deleted");
        await File.WriteAllTextAsync(deleted + ".pending.0001.delete.ready", string.Empty);
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(recent, "recent");
        await File.WriteAllTextAsync(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        await environment.Coordinator.DrainPendingWritesAsync(environment.Root, ".mpk");

        Assert.AreEqual("committed", await File.ReadAllTextAsync(Path.Combine(environment.Root, "recover.mpk")));
        Assert.IsFalse(File.Exists(ready));
        Assert.IsFalse(File.Exists(deleted));
        Assert.IsFalse(File.Exists(stale));
        Assert.IsTrue(File.Exists(recent));
        Assert.IsTrue(File.Exists(unrelated));
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) { await Task.Delay(5, timeout.Token); }
    }

    private sealed class TestEnvironment : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "InterfaceDbBulkLeases", Guid.NewGuid().ToString("N"));
        public StoragePolicy Policy { get; } = StoragePolicy.Default with
        {
            DurabilityMode = StorageDurabilityMode.Durable,
            MaxConcurrency = 2,
            BulkWriteBatchSize = 4,
            ObjectCacheMode = StorageObjectCacheMode.StrongObject,
            UseSingleWriteBuffer = false,
            WatchStorageChanges = false,
            WriteLockRetryDelay = TimeSpan.FromMilliseconds(5),
            WriteLockTimeout = TimeSpan.FromSeconds(10)
        };
        public CoalescingFileWriteCoordinator<Record> Coordinator { get; }
        private readonly Config _config;
        public TestEnvironment()
        {
            Directory.CreateDirectory(Root);
            _config = new Config(Root, Policy);
            Coordinator = CreateCoordinator();
        }

        public CoalescingFileWriteCoordinator<Record> CreateCoordinator()
            => new(new SidecarFileWriteLock<Record>(Policy), _config, Policy);

        public FileObjectWriter<Record> CreateWriter(IObjectSerializer<Record> serializer)
            => new(serializer, new Lifecycle(Root), _config, Policy,
                new EnvelopeConcurrentDictionary<string, IObjectEnvelope<Record>>(), Coordinator, new Identity());

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Record : IStoredContract<Record>
    {
        public required string Id { get; init; }
        public IObjectEnvelope<Record>? Envelope { get; set; }
    }

    private sealed class Lifecycle(string root) : IEnvelopeLifecycle<Record>
    {
        public ValueTask<IObjectEnvelope<Record>> WrapAsync(Record entity)
        {
            entity.Envelope ??= new FileObjectEnvelope<Record>
            {
                EnvelopeCacheKey = entity.Id,
                VersionTag = "1.0.0",
                Location = Path.Combine(root, entity.Id + ".mpk"),
                ObjectInstance = entity,
                State = StorageObjectState.Active
            };
            return ValueTask.FromResult(entity.Envelope);
        }
        public Record Unwrap(IObjectEnvelope<Record> envelope) => envelope.ObjectInstance!;
    }

    private sealed class Identity : IEnvelopeStorageIdentityFactory<Record>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public void InitEnvelope(IObjectEnvelope<Record> envelope) { }
    }

    private sealed class CallbackSerializer(Func<Record, CancellationToken, ValueTask> beforeWrite) : IObjectSerializer<Record>
    {
        public byte[] Serialize(IObjectEnvelope<Record> envelope) => Encoding.UTF8.GetBytes(envelope.ObjectInstance!.Id);
        public IObjectEnvelope<Record> Deserialize(byte[] data) => throw new NotSupportedException();
        public async ValueTask SerializeAsync(IObjectEnvelope<Record> envelope, Stream destination, CancellationToken cancellationToken = default)
        {
            await beforeWrite(envelope.ObjectInstance!, cancellationToken);
            await destination.WriteAsync(Serialize(envelope), cancellationToken);
        }
    }

    private sealed class Config(string root, StoragePolicy policy) : IDbContractConfigDto<Record>
    {
        public string EnvelopeDICacheKey => root;
        public string ContractName => "lease-test";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => policy;
    }
}
