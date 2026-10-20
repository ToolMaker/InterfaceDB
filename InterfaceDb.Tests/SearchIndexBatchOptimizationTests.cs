using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchIndexBatchOptimizationTests
{
    [TestMethod]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    [DataRow(StorageObjectCacheMode.MetadataOnly)]
    public async Task CachedEditsMergeWithIndexCandidatesWithoutAStorageScan(StorageObjectCacheMode mode)
    {
        var records = Enumerable.Range(0, 200).Select(i => new DerivedRecord
            { Id = i.ToString(), Group = i == 1 ? "match" : "other", Label = i == 1 ? "match" : "other" }).ToArray();
        var repository = new CachedRepository(records);
        await using var manager = new SearchIndexManager<Record>(repository, StoragePolicy.Default with { ObjectCacheMode = mode });
        await manager.InitializeAsync();
        var initialScans = repository.Enumerations;
        // Both records move across the saved index boundary. A third, uncached result
        // still comes from the index. Only two objects need cache predicate checks.
        repository.CachedKeys.UnionWith(["0", "1"]);
        records[0].Group = records[0].Label = "match";
        records[1].Group = records[1].Label = "other";
        await manager.SaveAsync(new DerivedRecord { Id = "remote", Group = "match", Label = "match" });
        var plan = manager.ExplainSearch(r => r.Group == "match");
        Assert.IsTrue(plan.UsesIndexes);
        Assert.IsTrue(plan.RechecksCachedObjects);
        Assert.IsNull(plan.FallbackReason);
        CollectionAssert.AreEquivalent(new[] { "0", "remote" },
            (await Collect(manager.SearchAsync(r => r.Group == "match"))).Select(r => r.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "0", "remote" },
            (await Collect(manager.SearchAsync<DerivedRecord>(r => r.Label == "match"))).Select(r => r.Id).ToArray());
        Assert.AreEqual(initialScans, repository.Enumerations);
        Assert.AreEqual(2, repository.PhysicalReads);
        Assert.IsTrue(await manager.DeleteAsync(records[0]));
        CollectionAssert.AreEquivalent(new[] { "remote" },
            (await Collect(manager.SearchAsync(r => r.Group == "match"))).Select(r => r.Id).ToArray());
    }

    [TestMethod]
    public async Task ExplainReportsIndexFailureWithoutTelemetryAndMatchesExecution()
    {
        var repository = new MemoryRepository(CreateRecords(100));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        Assert.IsTrue(manager.ExplainSearch(r => r.Group == "new").UsesIndexes);
        repository.AfterWrite = record => record.ThrowOnGroupRead = true;
        var saved = await manager.SaveAsync(new Record { Id = "new", Group = "new" });
        saved.ThrowOnGroupRead = false;
        var plan = manager.ExplainSearch(r => r.Group == "new");
        Assert.IsFalse(plan.UsesIndexes);
        StringAssert.Contains(plan.FallbackReason!, "Index maintenance failed");
        var initialScans = repository.Enumerations;
        Assert.HasCount(1, await Collect(manager.SearchAsync(r => r.Group == "new")));
        Assert.AreEqual(initialScans + 1, repository.Enumerations);
    }

    [TestMethod]
    public async Task CustomProviderWithoutCacheCapabilityExplainsItsSafeFallback()
    {
        var repository = new MemoryRepository(CreateRecords(100));
        await using var manager = new SearchIndexManager<Record>(repository,
            StoragePolicy.Default with { ObjectCacheMode = StorageObjectCacheMode.StrongObject });
        await manager.InitializeAsync();
        var plan = manager.ExplainSearch(r => r.Group == "group");
        Assert.IsFalse(plan.UsesIndexes);
        StringAssert.Contains(plan.FallbackReason!, "cache-only enumeration");
        Assert.HasCount(100, await Collect(manager.SearchAsync(r => r.Group == "group")));
    }

    [TestMethod]
    public async Task OneStorageCachedTypeDoesNotDisableIndexesForOtherTypes()
    {
        var repository = new CachedRepository(CreateRecords(200));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        var cached = await manager.SaveAsync(new CachedRecord { Id = "cached", Group = "initial" });
        repository.CachedKeys.Add("cached");
        cached.Group = "edited";
        Assert.IsTrue(manager.ExplainSearch(r => r.Group == "unrelated").UsesIndexes);
        var scans = repository.Enumerations;
        Assert.HasCount(1, await Collect(manager.SearchAsync(r => r.Group == "edited")));
        Assert.IsEmpty(await Collect(manager.SearchAsync(r => r.Group == "unrelated")));
        Assert.IsTrue(await manager.DeleteAsync(cached));
        Assert.IsTrue(manager.ExplainSearch(r => r.Group == "unrelated").UsesIndexes);
        Assert.IsEmpty(await Collect(manager.SearchAsync(r => r.Group == "edited")));
        Assert.AreEqual(scans, repository.Enumerations);
    }

    [TestMethod]
    public async Task IndexedFirstResultDoesNotPrefetchABatch()
    {
        var repository = new BatchRepository(CreateRecords(150));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        await foreach (var row in manager.SearchAsync(row => row.Group == "group"))
        {
            Assert.IsNotNull(row.Envelope);
            break;
        }

        Assert.AreEqual(1, repository.PhysicalReads);
        Assert.IsEmpty(repository.BatchSizes);
    }

    [TestMethod]
    public async Task FullIndexedSearchUsesBoundedBatchesAndKeepsResidualFiltering()
    {
        var repository = new BatchRepository(CreateRecords(150));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        var rows = await Collect(manager.SearchAsync(row => row.Group == "group" && row.Number % 2 == 0));

        CollectionAssert.AreEquivalent(Enumerable.Range(0, 150).Where(number => number % 2 == 0).ToArray(),
            rows.Select(row => row.Number).ToArray());
        Assert.AreEqual(1, repository.PhysicalReads);
        CollectionAssert.AreEqual(new[] { 64, 64, 21 }, repository.BatchSizes);
        Assert.IsTrue(rows.All(row => row.Envelope is not null));
    }

    [TestMethod]
    public async Task RuntimeImplementationSearchUsesTheSameBoundedPath()
    {
        var records = Enumerable.Range(0, 130).Select(number => new DerivedRecord
        {
            Id = number.ToString(),
            Group = "group",
            Number = number,
            Label = "derived"
        });
        var repository = new BatchRepository(records);
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        var rows = await Collect(manager.SearchAsync<DerivedRecord>(row => row.Label == "derived"));

        Assert.HasCount(130, rows);
        Assert.AreEqual(1, repository.PhysicalReads);
        CollectionAssert.AreEqual(new[] { 64, 64, 1 }, repository.BatchSizes);
        Assert.IsTrue(rows.All(row => row is DerivedRecord));
    }

    [TestMethod]
    public async Task ExistingProvidersRetainSingleReadFallback()
    {
        var repository = new MemoryRepository(CreateRecords(80));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        var rows = await Collect(manager.SearchAsync(row => row.Group == "group"));

        Assert.HasCount(80, rows);
        Assert.AreEqual(80, repository.PhysicalReads);
    }

    [TestMethod]
    public async Task CancellationAfterFirstResultStopsBeforeBatchIo()
    {
        var repository = new BatchRepository(CreateRecords(150));
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await using var iterator = manager.SearchAsync(row => row.Group == "group", cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.AreEqual(1, repository.PhysicalReads);
        Assert.IsEmpty(repository.BatchSizes);
    }

    [TestMethod]
    public async Task RuntimeTypeReplacementAndDeleteRemoveOnlyTheRelevantEntries()
    {
        var first = new DerivedRecord { Id = "one", Group = "group", Label = "first" };
        var survivor = new OtherRecord { Id = "two", Group = "group", Label = "second" };
        var repository = new BatchRepository(new Record[] { first, survivor });
        await using var manager = CreateManager(repository);
        await manager.InitializeAsync();
        var replacement = new OtherRecord { Id = "one", Group = "group", Label = "replacement" };
        await manager.SaveAsync(replacement);
        Assert.IsEmpty(await Collect(manager.SearchAsync<DerivedRecord>(row => row.Label == "first")));
        Assert.HasCount(1, await Collect(manager.SearchAsync<OtherRecord>(row => row.Label == "replacement")));

        Assert.IsTrue(await manager.DeleteAsync(replacement));
        Assert.IsEmpty(await Collect(manager.SearchAsync<OtherRecord>(row => row.Label == "replacement")));
        Assert.HasCount(1, await Collect(manager.SearchAsync<OtherRecord>(row => row.Label == "second")));
        Assert.HasCount(1, await Collect(manager.SearchAsync(row => row.Group == "group")));
    }

    [TestMethod]
    public async Task PassThroughSearchObservesMutableClosureAcrossRepeatedEnumeration()
    {
        var repository = new MemoryRepository(CreateRecords(6));
        var manager = new PassthroughIndexManager<Record>(repository);
        var minimum = 2;
        var query = manager.SearchAsync(row => row.Number > minimum);
        Assert.HasCount(3, await Collect(query));
        minimum = 4;
        Assert.HasCount(1, await Collect(query));
    }

    private static SearchIndexManager<Record> CreateManager(MemoryRepository repository)
        => new(repository, StoragePolicy.Default with { RaiseNotifications = false });

    private static IEnumerable<Record> CreateRecords(int count)
        => Enumerable.Range(0, count).Select(number => new Record
        {
            Id = number.ToString(),
            Group = "group",
            Number = number
        });

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var row in source) { result.Add(row); }
        return result;
    }

    private class Record : IStoredContract<Record>
    {
        private string _group = "";
        public required string Id { get; init; }
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Group { get => ThrowOnGroupRead ? throw new InvalidOperationException("index getter") : _group; set => _group = value; }
        public bool ThrowOnGroupRead { get; set; }
        public int Number { get; init; }
        public IObjectEnvelope<Record>? Envelope { get; protected set; }
        public void Attach(IObjectEnvelope<Record> envelope) => Envelope = envelope;
    }

    private sealed class DerivedRecord : Record
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Label { get; set; }
    }

    private sealed class OtherRecord : Record
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Label { get; init; }
    }

    [StorageCached]
    private sealed class CachedRecord : Record { }

    private sealed class BatchRepository(IEnumerable<Record> records) : MemoryRepository(records), IBatchedEnvelopeRepository<Record>
    {
        public List<int> BatchSizes { get; } = new();
        public ValueTask<IReadOnlyList<IObjectEnvelope<Record>>> GetPhysicalManyAsync(
            IReadOnlyList<string> physicalStorageKeys, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchSizes.Add(physicalStorageKeys.Count);
            IReadOnlyList<IObjectEnvelope<Record>> result = physicalStorageKeys.Where(Envelopes.ContainsKey)
                .Select(key => Envelopes[key]).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CachedRepository(IEnumerable<Record> records) : MemoryRepository(records), ICachedEnvelopeSource<Record>
    {
        public HashSet<string> CachedKeys { get; } = new();
        public IEnumerable<KeyValuePair<string, Record>> EnumerateCachedObjects()
        {
            foreach (var key in CachedKeys)
                if (Envelopes.TryGetValue(key, out var envelope) && envelope.ObjectInstance is { } instance)
                    yield return new(key, instance);
        }
    }

    private class MemoryRepository : IEnvelopeRepository<Record>
    {
        protected readonly Dictionary<string, IObjectEnvelope<Record>> Envelopes = new(StringComparer.Ordinal);
        public int PhysicalReads { get; private set; }
        public int Enumerations { get; private set; }
        public Action<Record>? AfterWrite { get; set; }
        public IReaderAndWriter<Record> ReaderAndWriter => throw new NotSupportedException();

        public MemoryRepository(IEnumerable<Record> records)
        {
            foreach (var record in records) { Store(record); }
        }

        private void Store(Record record)
        {
            var envelope = new FileObjectEnvelope<Record>
            {
                EnvelopeCacheKey = record.Id,
                VersionTag = "1.0.0",
                ObjectInstance = record,
                ImplementationTypeName = record.GetType().AssemblyQualifiedName,
                State = StorageObjectState.Active
            };
            record.Attach(envelope);
            Envelopes[record.Id] = envelope;
        }

        public bool Exists(string storageKey) => Envelopes.ContainsKey(storageKey);
        public bool TryGet(string storageKey, out IReadOnlyList<IObjectEnvelope<Record>> envelopes)
        {
            envelopes = Envelopes.TryGetValue(storageKey, out var envelope) ? [envelope] : [];
            return envelopes.Count != 0;
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IObjectEnvelope<Record>?> GetPhysicalAsync(string physicalStorageKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalReads++;
            Envelopes.TryGetValue(physicalStorageKey, out var envelope);
            return ValueTask.FromResult(envelope);
        }

        public async IAsyncEnumerable<IObjectEnvelope<Record>> GetAsync(string storageKey, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var envelope = await GetPhysicalAsync(storageKey, cancellationToken);
            if (envelope is not null) { yield return envelope; }
        }

        public async IAsyncEnumerable<IObjectEnvelope<Record>> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Enumerations++;
            foreach (var envelope in Envelopes.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return envelope;
            }
            await Task.CompletedTask;
        }

        async IAsyncEnumerable<IObjectEnvelope<Record>> IEnvelopeRepository<Record>.EnumerateByImplementationTypeAsync<TImplementation>([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var envelope in EnumerateAsync(cancellationToken))
            {
                if (envelope.ObjectInstance is TImplementation) { yield return envelope; }
            }
        }

        public ValueTask<Record> WriteAsync(Record contractInstance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Store(contractInstance);
            AfterWrite?.Invoke(contractInstance);
            return ValueTask.FromResult(contractInstance);
        }

        public ValueTask<bool> DeleteAsync(Record contractInstance, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Envelopes.Remove(contractInstance.Id));

        public IContractSubscription SubscribeToAdded(Func<IObjectEnvelope<Record>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
        public IContractSubscription SubscribeToRemoved(Func<IObjectEnvelope<Record>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
        public IContractSubscription SubscribeToStored(Func<IObjectEnvelope<Record>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
        public IContractSubscription SubscribeToDeleted(Func<IObjectEnvelope<Record>, ValueTask> handler, Func<ValueTask<bool>>? keepAlive = null) => new Subscription();
        public ValueTask ContractAdded(IObjectEnvelope<Record> @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ContractRemoved(IObjectEnvelope<Record> @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ContractStored(IObjectEnvelope<Record> @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ContractDeleted(IObjectEnvelope<Record> @event, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Subscription : IContractSubscription
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
