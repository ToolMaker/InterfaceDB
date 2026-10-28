using Idb.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class PartitionKeyIndexTests
{
    [TestMethod]
    public void DirectoryMatchesCommittedOracleAndBoundsMemoryAcrossEvictions()
    {
        var scopes = Enumerable.Range(0, 7).Select(n => DataAccessScope.Partitioned("index-test", n.ToString()).Partition).ToArray();
        var committed = new HashSet<string>(StringComparer.Ordinal);
        IPartitionKeyIndex index = new BoundedPartitionKeyIndexFactory(new() { MaximumKeys = 30, MaximumPartitions = 3, MaximumEstimatedBytes = 6000 }).Create();
        var random = new Random(8453);
        for (int n = 0; n < 800; n++)
        {
            var key = scopes[random.Next(scopes.Length)] + random.Next(40).ToString("D3");
            var mutation = new EncodedMutation(key, random.Next(3) == 0 ? null : [1]);
            if (mutation.Payload is null) committed.Remove(key); else committed.Add(key);
            index.ApplyCommitted([mutation]);
            if (n % 97 == 0) index.Invalidate();
            var scope = scopes[random.Next(scopes.Length)];
            string? after = n % 5 == 0 ? null : scope + random.Next(45).ToString("D3");
            var expected = committed.Where(k => k.StartsWith(scope, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            var page = index.Select(scope, after, 7, () => committed.ToArray(), default)!;
            Assert.AreEqual(expected.Length, page.PartitionCount);
            CollectionAssert.AreEqual(expected.Where(k => after is null || StringComparer.Ordinal.Compare(k, after) > 0).Take(7).ToArray(), page.Keys.ToArray());
            Assert.IsTrue(index.Statistics.CachedKeys <= 30);
            Assert.IsTrue(index.Statistics.CachedPartitions <= 3);
            Assert.IsTrue(index.Statistics.EstimatedBytes <= 6000);
        }
        Assert.IsTrue(index.Statistics.Evictions > 0);
    }

    [TestMethod]
    public void DisabledAndUnrestrictedPathsNeverLoadKeysAndDiCreatesIndependentIndexes()
    {
        using var services = new ServiceCollection().AddPartitionKeyIndex()
            .AddPartitionKeyIndex(new() { Mode = PartitionIndexMode.Disabled }, "off").BuildServiceProvider();
        var first = services.GetRequiredService<IPartitionKeyIndexFactory>().Create();
        var second = services.GetRequiredService<IPartitionKeyIndexFactory>().Create();
        var disabled = services.GetRequiredKeyedService<IPartitionKeyIndexFactory>("off").Create();
        var scope = DataAccessScope.Partitioned("test", "tenant").Partition;
        IReadOnlyList<string> Unexpected() => throw new AssertFailedException("No key scan was expected.");
        Assert.IsNull(first.Select("", null, 1, Unexpected, default));
        Assert.IsNull(disabled.Select(scope, null, 1, Unexpected, default));
        first.Select(scope, null, 2, () => [scope + "one"], default);
        first.Select(scope, null, 2, Unexpected, default);
        Assert.AreEqual(1, first.Statistics.CachedKeys);
        Assert.AreEqual(0, second.Statistics.CachedKeys);
    }

    [TestMethod]
    [DataRow(PartitionIndexMode.Disabled)] [DataRow(PartitionIndexMode.Lazy)] [DataRow(PartitionIndexMode.Eager)]
    public async Task IndexCommitsReopensAndRejectsFailedBatchesWithoutDrift(PartitionIndexMode mode)
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-production-index-" + Guid.NewGuid().ToString("N"));
        var factory = new BoundedPartitionKeyIndexFactory(new() { Mode = mode, MaximumPartitions = 1, MaximumKeys = 2 });
        var scope = DataAccessScope.Partitioned("index-test", "a");
        try
        {
            await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, maximumEnumerationKeys: 5,
                maximumPartitionKeys: 3, keyIndexFactory: factory))
            {
                IEncodedEnvelopeStore a = new ScopedEncodedStore(store, scope);
                IEncodedEnvelopeStore b = new ScopedEncodedStore(store, DataAccessScope.Partitioned("index-test", "b"));
                await a.ApplyBatchAsync([new("1", [1]), new("2", [2])]);
                await a.EnumerateAsync(1); // Populate; growth below must invalidate/evict safely.
                await a.PutAsync("3", [3]);
                await Assert.ThrowsExactlyAsync<EncodedStoreRequestRejectedException>(async () => await a.PutAsync("4", [4]));
                await a.ApplyBatchAsync([new("1", null), new("2", [9])]);
                await b.PutAsync("2", [22]); await b.EnumerateAsync(1);
                CollectionAssert.AreEqual(new[] { "2", "3" }, (await a.EnumerateAsync(4)).Records!.Select(r => r.Key).ToArray());
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await a.DeleteAsync("2", cancelled.Token));
                CollectionAssert.AreEqual(new byte[] { 9 }, await a.GetAsync("2"));
            }
            await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, keyIndexFactory: factory);
            IEncodedEnvelopeStore read = new ScopedEncodedStore(reopened, scope);
            CollectionAssert.AreEqual(new[] { "2", "3" }, (await read.EnumerateAsync(4)).Records!.Select(r => r.Key).ToArray());
            CollectionAssert.AreEqual(new byte[] { 9 }, await read.GetAsync("2"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task IndexUpdateFailureAfterCommitInvalidatesAndRecoversFromCommittedKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-index-fault-" + Guid.NewGuid().ToString("N"));
        var factory = new FailingFactory();
        try
        {
            await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, keyIndexFactory: factory);
            IEncodedEnvelopeStore scope = new ScopedEncodedStore(store, DataAccessScope.Partitioned("test", "a"));
            await scope.EnumerateAsync(5);
            await Assert.ThrowsExactlyAsync<IOException>(async () => await scope.PutAsync("committed", [7]));
            CollectionAssert.AreEqual(new[] { "committed" }, (await scope.EnumerateAsync(5)).Records!.Select(r => r.Key).ToArray());
            CollectionAssert.AreEqual(new byte[] { 7 }, await scope.GetAsync("committed"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private sealed class FailingFactory : IPartitionKeyIndexFactory
    {
        public IPartitionKeyIndex Create() => new FailingIndex();
    }
    private sealed class FailingIndex : IPartitionKeyIndex
    {
        private readonly IPartitionKeyIndex _inner = new BoundedPartitionKeyIndexFactory().Create();
        private bool _fail = true;
        public PartitionIndexStatistics Statistics => _inner.Statistics;
        public void Initialize(Func<IReadOnlyList<string>> source, CancellationToken token) => _inner.Initialize(source, token);
        public PartitionKeyPage? Select(string partition, string? after, int take, Func<IReadOnlyList<string>> source, CancellationToken token)
            => _inner.Select(partition, after, take, source, token);
        public void Invalidate() => _inner.Invalidate();
        public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations)
        {
            if (_fail) { _fail = false; throw new IOException("Injected derived-index failure after commit."); }
            _inner.ApplyCommitted(mutations);
        }
    }

    [TestMethod]
    public async Task ProviderRejectsOutOfScopeKeysFromAnInjectedIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-index-boundary-" + Guid.NewGuid().ToString("N"));
        var other = DataAccessScope.Partitioned("test", "other");
        try
        {
            await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, keyIndexFactory: new IncorrectIndex(other.Partition + "secret"));
            IEncodedEnvelopeStore owner = new ScopedEncodedStore(store, other);
            await owner.PutAsync("secret", [42]);
            IEncodedEnvelopeStore caller = new ScopedEncodedStore(store, DataAccessScope.Partitioned("test", "caller"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await caller.EnumerateAsync(5));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private sealed class IncorrectIndex(string key) : IPartitionKeyIndexFactory, IPartitionKeyIndex
    {
        public IPartitionKeyIndex Create() => this;
        public PartitionIndexStatistics Statistics => new(0, 0, 0, 0, 0, 0, 0);
        public void Initialize(Func<IReadOnlyList<string>> source, CancellationToken token) { }
        public PartitionKeyPage? Select(string partition, string? after, int take, Func<IReadOnlyList<string>> source, CancellationToken token) => new(1, [key]);
        public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations) { }
        public void Invalidate() { }
    }

    [TestMethod]
    public async Task PolicyReview_UnrestrictedViewHidesTenantKeysReturnedByAnInjectedIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-root-index-boundary-" + Guid.NewGuid().ToString("N"));
        var owner = DataAccessScope.Partitioned("test", "owner");
        try
        {
            await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path,
                keyIndexFactory: new IncorrectIndex(owner.Partition + "secret"));
            await new ScopedEncodedStore(store, owner).PutAsync("secret", [42]);
            IEncodedEnvelopeStore view = new ScopedEncodedStore(store, DataAccessScope.Unrestricted);
            Assert.HasCount(0, (await view.EnumerateAsync(5)).Records!);
            // Trusted physical enumeration remains available for offline migration.
            Assert.HasCount(1, (await store.EnumerateAsync(5)).Records!);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task PolicyReview_SelectionCancellationPreservesOnlyProvenConsistentIndexState(bool customFailure)
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-index-cancellation-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var alice = DataAccessScope.Partitioned("review", "a");
        var bob = DataAccessScope.Partitioned("review", "b");
        var factory = new CancellingSelectionIndex(bob.Partition, cancellation, customFailure);
        try
        {
            await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, keyIndexFactory: factory);
            IEncodedEnvelopeStore a = new ScopedEncodedStore(store, alice);
            IEncodedEnvelopeStore b = new ScopedEncodedStore(store, bob);
            await a.PutAsync("one", [1]);
            await a.EnumerateAsync(10);
            var before = store.PartitionIndexStatistics;
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await b.EnumerateAsync(10, cancellationToken: cancellation.Token));
            Assert.AreEqual(customFailure ? 0 : before.CachedPartitions, store.PartitionIndexStatistics.CachedPartitions);
            CollectionAssert.AreEqual(new[] { "one" }, (await a.EnumerateAsync(10)).Records!.Select(record => record.Key).ToArray());
            Assert.AreEqual(before.Hits + (customFailure ? 0 : 1), store.PartitionIndexStatistics.Hits);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class CancellingSelectionIndex(string target, CancellationTokenSource cancellation, bool customFailure)
        : IPartitionKeyIndexFactory, IPartitionKeyIndex
    {
        private readonly IPartitionKeyIndex _inner = new BoundedPartitionKeyIndexFactory().Create();
        public IPartitionKeyIndex Create() => this;
        public PartitionIndexStatistics Statistics => _inner.Statistics;
        public void Initialize(Func<IReadOnlyList<string>> source, CancellationToken token) => _inner.Initialize(source, token);
        public void Invalidate() => _inner.Invalidate();
        public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations) => _inner.ApplyCommitted(mutations);
        public PartitionKeyPage? Select(string partition, string? after, int take, Func<IReadOnlyList<string>> source, CancellationToken token)
        {
            if (partition != target) return _inner.Select(partition, after, take, source, token);
            if (customFailure)
            {
                _inner.Select(partition, after, take, source, token);
                throw new OperationCanceledException("Injected index failure after modifying cache state.");
            }
            return _inner.Select(partition, after, take, () => new CancelDuringScan(source(), cancellation), token);
        }
    }

    private sealed class CancelDuringScan(IReadOnlyList<string> keys, CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => keys.Count;
        public string this[int index] => keys[index];
        public IEnumerator<string> GetEnumerator()
        {
            foreach (var key in keys)
            {
                yield return key;
                cancellation.Cancel();
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
