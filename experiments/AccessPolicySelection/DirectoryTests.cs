using Idb.Transport;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class ExperimentalPartitionDirectoryTests
{
    [TestMethod]
    [DataRow("Array")]
    [DataRow("Tree")]
    public void RandomCommittedMutationsAndDeletedCursorsMatchSortedOracle(string mode)
    {
        IPartitionKeyDirectory directory = new PartitionKeyDirectory(Enum.Parse<SelectionVariant>(mode));
        var oracle = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = Enumerable.Range(0, 4).Select(n => DataAccessScope.Partitioned("directory-test", n.ToString()).Partition).ToArray();
        var random = new Random(413);
        directory.Rebuild([]);
        for (int iteration = 0; iteration < 1200; iteration++)
        {
            var mutations = Enumerable.Range(0, 1 + random.Next(6)).Select(_ => new EncodedMutation(
                prefixes[random.Next(prefixes.Length)] + random.Next(100).ToString("D3"), random.Next(3) == 0 ? null : [7])).ToArray();
            foreach (var mutation in mutations)
                if (mutation.Payload is null) oracle.Remove(mutation.Key); else oracle.Add(mutation.Key);
            directory.Apply(mutations);
            if (iteration % 37 == 0) directory.Rebuild(oracle.Concat(["ordinary", "\u0001idb-changes:reserved"]));
            foreach (var prefix in prefixes)
            {
                string? after = iteration % 4 == 0 ? null : prefix + random.Next(110).ToString("D3");
                var expected = oracle.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
                var result = directory.Select(prefix, after, 8);
                Assert.AreEqual(expected.Length, result.Count);
                CollectionAssert.AreEqual(expected.Where(key => after is null || StringComparer.Ordinal.Compare(key, after) > 0).Take(8).ToArray(), result.Keys);
            }
        }
    }

    [TestMethod]
    public async Task CommittedMixedBatchCancellationAndCapacityFailureRemainVisibleAfterReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-directory-test-" + Guid.NewGuid().ToString("N"));
        var a = DataAccessScope.Partitioned("experiment", "a");
        var b = DataAccessScope.Partitioned("experiment", "b");
        try
        {
            await using (var backend = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, maximumEnumerationKeys: 8, maximumPartitionKeys: 3))
            {
                IEncodedEnvelopeStore first = new ScopedEncodedStore(backend, a), second = new ScopedEncodedStore(backend, b);
                await first.ApplyBatchAsync([new("0", [0]), new("1", [1]), new("2", [2])]);
                await second.PutAsync("0", [9]);
                await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await first.PutAsync("3", [3]));
                // A prior error invalidates the candidate; a subsequent commit must survive rebuild.
                await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                    await first.ApplyBatchAsync([new("1", null), new("3", [3]), new("3", null)]));
                await first.ApplyBatchAsync([new("1", null), new("2", [4]), new("4", [5])]);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.DeleteAsync("0", cancelled.Token));
                CollectionAssert.AreEqual(new[] { "0", "2", "4" }, (await first.EnumerateAsync(8)).Records!.Select(r => r.Key).ToArray());
                CollectionAssert.AreEqual(new byte[] { 9 }, await second.GetAsync("0"));
                var page = await first.EnumerateAsync(1, "1");
                Assert.AreEqual("2", page.Records![0].Key); Assert.AreEqual("2", page.NextAfterKey);
                var last = await first.EnumerateAsync(1, page.NextAfterKey);
                Assert.AreEqual("4", last.Records![0].Key); Assert.IsNull(last.NextAfterKey);
            }
            await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path, maximumEnumerationKeys: 8, maximumPartitionKeys: 3);
            IEncodedEnvelopeStore scoped = new ScopedEncodedStore(reopened, a);
            CollectionAssert.AreEqual(new[] { "0", "2", "4" }, (await scoped.EnumerateAsync(8)).Records!.Select(r => r.Key).ToArray());
            CollectionAssert.AreEqual(new byte[] { 4 }, await scoped.GetAsync("2"));
            Assert.IsNull(await scoped.GetAsync("1"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [TestMethod]
    public async Task ConcurrentPartitionsDoNotMixIdenticalKeysOrPublishUncommittedMembership()
    {
        var path = Path.Combine(Path.GetTempPath(), "idb-directory-concurrent-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var backend = await SegmentCoreEncodedEnvelopeStore.OpenAsync(path);
            var scopes = Enumerable.Range(0, 8).Select(n => (IEncodedEnvelopeStore)new ScopedEncodedStore(backend,
                DataAccessScope.Partitioned("concurrent", n.ToString()))).ToArray();
            await Task.WhenAll(scopes.Select((scope, tenant) => Task.Run(async () =>
            {
                for (int n = 0; n < 25; n++)
                {
                    await scope.PutAsync(n.ToString("D3"), [(byte)tenant, (byte)n]);
                    var snapshot = await scope.EnumerateAsync(64);
                    Assert.HasCount(n + 1, snapshot.Records!);
                    foreach (var record in snapshot.Records!) Assert.AreEqual((byte)tenant, record.Payload[0]);
                }
            })));
            foreach (var scope in scopes) Assert.HasCount(25, (await scope.EnumerateAsync(64)).Records!);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [TestMethod]
    public void LazyDirectoryEvictsAndRebuildsWithoutLosingCommittedChanges()
    {
        var a = DataAccessScope.Partitioned("lazy", "a").Partition;
        var b = DataAccessScope.Partitioned("lazy", "b").Partition;
        var data = new HashSet<string>([a + "0", a + "1", b + "0"], StringComparer.Ordinal);
        int scans = 0;
        IPartitionKeyDirectory directory = new LazyPartitionKeyDirectory(() => { scans++; return data.ToArray(); }, maximumKeys: 2, maximumPartitions: 1);
        CollectionAssert.AreEqual(new[] { a + "0", a + "1" }, directory.Select(a, null, 3).Keys);
        directory.Select(a, null, 3); Assert.AreEqual(1, scans);
        directory.Select(b, null, 3); Assert.AreEqual(2, scans); // Evicts a.
        data.Remove(a + "0"); data.Add(a + "2");
        directory.Apply([new(a + "0", null), new(a + "2", [2])]);
        CollectionAssert.AreEqual(new[] { a + "1", a + "2" }, directory.Select(a, null, 3).Keys);
        Assert.AreEqual(3, scans);
        data.Add(a + "3"); directory.Apply([new(a + "3", [3])]); // Growth evicts oversized tenant.
        Assert.AreEqual(3, directory.Select(a, null, 4).Count);
        Assert.AreEqual(4, scans);
        directory.Select(a, null, 4); Assert.AreEqual(5, scans);
        directory.Rebuild(data);
        directory.Select(b, null, 4); Assert.AreEqual(6, scans);
    }
}
