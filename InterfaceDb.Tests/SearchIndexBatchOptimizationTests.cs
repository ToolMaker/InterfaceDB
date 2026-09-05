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
        public required string Id { get; init; }
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Group { get; init; }
        public int Number { get; init; }
        public IObjectEnvelope<Record>? Envelope { get; protected set; }
        public void Attach(IObjectEnvelope<Record> envelope) => Envelope = envelope;
    }

    private sealed class DerivedRecord : Record
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Label { get; init; }
    }

    private sealed class OtherRecord : Record
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public required string Label { get; init; }
    }

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

    private class MemoryRepository : IEnvelopeRepository<Record>
    {
        protected readonly Dictionary<string, IObjectEnvelope<Record>> Envelopes = new(StringComparer.Ordinal);
        public int PhysicalReads { get; private set; }
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
