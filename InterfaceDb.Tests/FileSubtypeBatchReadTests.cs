using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class FileSubtypeBatchReadTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScanSnapshotIsDeferredUntilEnumerationStarts(bool typed)
    {
        await using var fixture = new Fixture(0);
        var query = typed ? fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>()
            : fixture.Repository.EnumerateAsync();
        fixture.Add(1, target: true, unknown: false);

        Assert.HasCount(1, await Collect(query));
    }

    [TestMethod]
    public async Task MixedTypeScanPreservesCandidateOrderAndChecksUnknownTypesAfterLoading()
    {
        await using var fixture = new Fixture(70, 10, unknownTargets: 3, unknownOthers: 3);
        var candidates = fixture.Cache.Values.Where(envelope => envelope.ImplementationTypeName is null
            || envelope.ImplementationTypeName == typeof(TargetRecord).AssemblyQualifiedName).ToArray();
        var expected = candidates.Where(envelope => fixture.Reader.IsTarget(envelope.EnvelopeCacheKey))
            .Select(envelope => envelope.EnvelopeCacheKey).ToArray();

        var results = await Collect(fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>());

        CollectionAssert.AreEqual(expected, results.Select(envelope => envelope.EnvelopeCacheKey).ToArray());
        CollectionAssert.AreEquivalent(candidates.Select(envelope => envelope.EnvelopeCacheKey).ToArray(), fixture.Reader.ReadKeys.ToArray());
        Assert.IsTrue(results.All(envelope => envelope.ObjectInstance is TargetRecord));
        Assert.IsTrue(results.All(envelope => envelope.ImplementationTypeName == typeof(TargetRecord).AssemblyQualifiedName));
        Assert.IsTrue(fixture.Reader.PeakReaders <= Fixture.ReadConcurrency);
    }

    [TestMethod]
    public async Task FirstResultDoesNotStartSpeculativeReads()
    {
        await using var fixture = new Fixture(150);
        fixture.Reader.BlockAfterFirst = true;
        await using (var iterator = fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>().GetAsyncEnumerator())
        {
            Assert.IsTrue(await iterator.MoveNextAsync());
            Assert.AreEqual(1, fixture.Reader.ReadKeys.Count);
        }
        Assert.AreEqual(0, fixture.Reader.ActiveReaders);
        Assert.AreEqual(1, fixture.Reader.ReadKeys.Count);
    }

    [TestMethod]
    public async Task SecondWindowIsBoundedAndEarlyDisposalLeavesNoPendingReads()
    {
        await using var fixture = new Fixture(150);
        fixture.Reader.BlockAfterFirst = true;
        await using var iterator = fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>().GetAsyncEnumerator();
        Assert.IsTrue(await iterator.MoveNextAsync());
        var next = iterator.MoveNextAsync().AsTask();
        try
        {
            await fixture.Reader.AllReadersBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(Fixture.ReadConcurrency, fixture.Reader.ActiveReaders);
            fixture.Reader.Release.TrySetResult();
            Assert.IsTrue(await next);
            Assert.AreEqual(65, fixture.Reader.ReadKeys.Count, "One first read plus one 64-key window is sufficient.");
            Assert.AreEqual(0, fixture.Reader.ActiveReaders);
        }
        finally
        {
            fixture.Reader.Release.TrySetResult();
            await next;
        }
    }

    [TestMethod]
    public async Task CancellationDrainsEveryWorkerBeforeCompletingTheEnumerator()
    {
        await using var fixture = new Fixture(150);
        fixture.Reader.BlockAfterFirst = true;
        using var cancellation = new CancellationTokenSource();
        await using var iterator = fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>(cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await iterator.MoveNextAsync());
        var next = iterator.MoveNextAsync().AsTask();
        try
        {
            await fixture.Reader.AllReadersBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => next);
            Assert.AreEqual(0, fixture.Reader.ActiveReaders);
            Assert.AreEqual(1 + Fixture.ReadConcurrency, fixture.Reader.ReadKeys.Count);
        }
        finally
        {
            cancellation.Cancel();
            fixture.Reader.Release.TrySetResult();
            try { await next; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task ReadFailureIsPropagatedAndLeavesNoBackgroundReaders()
    {
        await using var fixture = new Fixture(90);
        var firstKey = fixture.Cache.Values.First().EnvelopeCacheKey;
        fixture.Reader.FailKeys = fixture.Cache.Keys.Where(key => key != firstKey).ToHashSet(StringComparer.Ordinal);

        await Assert.ThrowsAsync<IOException>(() => Collect(fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>()));

        Assert.AreEqual(0, fixture.Reader.ActiveReaders);
    }

    [TestMethod]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    public async Task FullyCachedTypeScanReusesObjectsWithoutDiskReads(StorageObjectCacheMode mode)
    {
        await using var fixture = new Fixture(80, 10, cacheMode: mode);
        var pins = fixture.PinAllObjects(mode);

        var results = await Collect(fixture.Repository.EnumerateByImplementationTypeAsync<TargetRecord>());

        Assert.HasCount(80, results);
        Assert.IsEmpty(fixture.Reader.ReadKeys);
        foreach (var envelope in results)
        {
            Assert.IsTrue(StorageObjectCachePolicy.TryGetObjectInstance(envelope, out var instance));
            Assert.AreSame(pins[envelope.EnvelopeCacheKey], instance);
        }
        GC.KeepAlive(pins);
    }

    [TestMethod]
    public async Task RealFilesAndPublicRepositoryPreserveSubtypeAndContractScansAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDbSubtypeRoundTrip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new Config(root, Policy(StorageObjectCacheMode.MetadataOnly));
        try
        {
            var services = new ServiceCollection();
            services.AddFileRepository(config);
            await using (var provider = services.BuildServiceProvider())
            {
                var repository = provider.GetRequiredService<IRepository<IRecord>>();
                await repository.SaveManyAsync(Enumerable.Range(0, 80).Select(index => (IRecord)new TargetRecord { Id = $"target-{index}" })
                    .Concat(Enumerable.Range(0, 10).Select(index => (IRecord)new OtherRecord { Id = $"other-{index}" })));
            }
            var reopenedServices = new ServiceCollection();
            reopenedServices.AddFileRepository(config);
            await using var reopened = reopenedServices.BuildServiceProvider();
            var repositoryView = reopened.GetRequiredService<IRepository<IRecord>>();
            await repositoryView.InitializeAsync();
            var targets = await Collect(repositoryView.QueryManyAsync<TargetRecord>(record => record.Id != "target-0"));
            Assert.HasCount(79, targets);
            Assert.IsTrue(targets.All(record => record.Id.StartsWith("target-", StringComparison.Ordinal)));
            Assert.HasCount(90, await Collect(repositoryView.QueryManyAsync<IRecord>(_ => true)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) { result.Add(item); }
        return result;
    }

    private static StoragePolicy Policy(StorageObjectCacheMode mode) => StoragePolicy.Default with
    {
        ObjectCacheMode = mode,
        HydrateOnInitialize = false,
        WatchStorageChanges = false,
        MaxReadConcurrency = Fixture.ReadConcurrency,
        UseSidecarWriteLocks = false
    };

    [StorageScope(VersionTag = "1.0.0")]
    public interface IRecord : IStoredContract<IRecord> { string Id { get; set; } }

    [MessagePackObject(true)]
    public sealed class TargetRecord : IRecord
    {
        public string Id { get; set; } = "";
        [IgnoreMember] public IObjectEnvelope<IRecord>? Envelope { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class OtherRecord : IRecord
    {
        public string Id { get; set; } = "";
        [IgnoreMember] public IObjectEnvelope<IRecord>? Envelope { get; set; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const int ReadConcurrency = 3;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "InterfaceDbSubtypeBatch", Guid.NewGuid().ToString("N"));
        internal EnvelopeConcurrentDictionary<string, IObjectEnvelope<IRecord>> Cache { get; } = new();
        internal ControlledReader Reader { get; } = new();
        internal FileEnvelopeRepository<IRecord> Repository { get; }

        internal Fixture(int targets, int others = 0, int unknownTargets = 0, int unknownOthers = 0,
            StorageObjectCacheMode cacheMode = StorageObjectCacheMode.MetadataOnly)
        {
            Directory.CreateDirectory(_root);
            Add(targets, true, false);
            Add(others, false, false);
            Add(unknownTargets, true, true);
            Add(unknownOthers, false, true);
            var policy = Policy(cacheMode);
            var config = new Config(_root, policy);
            var coordinator = new CoalescingFileWriteCoordinator<IRecord>(new SidecarFileWriteLock<IRecord>(policy), config, policy);
            Repository = new FileEnvelopeRepository<IRecord>(config,
                new ObjectReaderAndWriter<IRecord>(Reader, new RejectingWriter()), Cache, coordinator, policy,
                () => new FileObjectEnvelope<IRecord> { EnvelopeCacheKey = "", VersionTag = "1.0.0" });
        }

        internal void Add(int count, bool target, bool unknown)
        {
            for (var index = 0; index < count; index++)
            {
                var key = $"{target}-{unknown}-{index}";
                var envelope = new FileObjectEnvelope<IRecord>
                {
                    EnvelopeCacheKey = key,
                    VersionTag = "1.0.0",
                    Location = Path.Combine(_root, key + ".mpk"),
                    ImplementationTypeName = unknown ? null : (target ? typeof(TargetRecord) : typeof(OtherRecord)).AssemblyQualifiedName
                };
                Cache.Add(key, envelope);
                Reader.Targets.Add(key, target);
            }
        }

        internal Dictionary<string, IRecord> PinAllObjects(StorageObjectCacheMode mode)
        {
            var pins = new Dictionary<string, IRecord>(StringComparer.Ordinal);
            foreach (var envelope in Cache.Values)
            {
                var instance = Reader.CreateRecord(envelope.EnvelopeCacheKey);
                pins.Add(envelope.EnvelopeCacheKey, instance);
                if (mode == StorageObjectCacheMode.StrongObject) { envelope.ObjectInstance = instance; }
                else { envelope.ObjectInstanceWeakReference = new WeakReference<IRecord>(instance); }
            }
            return pins;
        }

        public async ValueTask DisposeAsync()
        {
            Reader.Release.TrySetResult();
            await Repository.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ControlledReader : IObjectReader<IRecord>
    {
        internal Dictionary<string, bool> Targets { get; } = new(StringComparer.Ordinal);
        internal ConcurrentQueue<string> ReadKeys { get; } = new();
        internal HashSet<string> FailKeys { get; set; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource AllReadersBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockAfterFirst { get; set; }
        private int _activeReaders;
        private int _reads;
        private int _peak;
        internal int ActiveReaders => Volatile.Read(ref _activeReaders);
        internal int PeakReaders => Volatile.Read(ref _peak);
        internal bool IsTarget(string key) => Targets[key];

        internal IRecord CreateRecord(string key)
        {
            IRecord instance = Targets[key] ? new TargetRecord { Id = key } : new OtherRecord { Id = key };
            var envelope = new FileObjectEnvelope<IRecord>
            {
                EnvelopeCacheKey = key,
                VersionTag = "1.0.0",
                ObjectInstance = instance,
                ImplementationTypeName = instance.GetType().AssemblyQualifiedName
            };
            if (instance is TargetRecord target) { target.Envelope = envelope; }
            else { ((OtherRecord)instance).Envelope = envelope; }
            return instance;
        }

        public async Task<IRecord> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(file.Name);
            ReadKeys.Enqueue(key);
            var ordinal = Interlocked.Increment(ref _reads);
            var active = Interlocked.Increment(ref _activeReaders);
            int prior;
            do { prior = _peak; } while (active > prior && Interlocked.CompareExchange(ref _peak, active, prior) != prior);
            try
            {
                if (BlockAfterFirst && ordinal > 1)
                {
                    if (active == Fixture.ReadConcurrency) { AllReadersBlocked.TrySetResult(); }
                    await Release.Task.WaitAsync(cancellationToken);
                }
                else { await Task.Yield(); }
                cancellationToken.ThrowIfCancellationRequested();
                if (FailKeys.Contains(key)) { throw new IOException("Injected read failure."); }
                return CreateRecord(key);
            }
            finally { Interlocked.Decrement(ref _activeReaders); }
        }
    }

    private sealed class Config(string root, StoragePolicy policy) : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey => "subtype-batch";
        public string ContractName => "SubtypeBatch";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => policy;
    }

    private sealed class RejectingWriter : IObjectWriter<IRecord>
    {
        public ValueTask<IRecord> WriteAsync(IRecord objectInstance, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
