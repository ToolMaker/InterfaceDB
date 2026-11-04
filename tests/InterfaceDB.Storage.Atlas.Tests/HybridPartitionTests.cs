using System.Text;
using Idb.Storage.AtlasCore;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class HybridPartitionTests
{
    internal static AtlasCoreOptions Options(bool automatic = false) => new()
    {
        MaximumConcurrentPartitionWrites = 4,
        MaximumSegmentBytes = 16 * 1024,
        DurabilityStrategy = AtlasCoreDurabilityStrategy.ExplicitFlush,
        Partitioning = new()
        {
            EnableAutomaticSplitting = automatic, SplitAboveLiveRecords = 4,
            SplitAboveLiveBytes = long.MaxValue, MinimumChildRecords = 1,
            CheckEveryMutations = 1, RetryDelay = TimeSpan.Zero,
            MinimumFreeSpaceReserveBytes = 0
        }
    };

    private static string Prefix(AtlasPartitionInfo p) => Encoding.UTF8.GetString(Convert.FromHexString(p.PrefixHex));
    private static async Task Populate(AtlasCoreStore store, string prefix, int count, byte value = 1)
    {
        for (var i = 0; i < count; i++) await store.SaveAsync($"{prefix}{i:D3}", new[] { value });
    }

    [TestMethod]
    public async Task OnlyGrowingBranchSplitsAndLaterDescendantCanSplitAgain()
    {
        using var root = new Temp(); var options = Options() with
        { Partitioning = Options().Partitioning with { SplitAboveLiveRecords = 30 } };
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        {
            await Populate(store, "ABC", 5);
            await Populate(store, "ABEF", 16); await Populate(store, "ABEG", 16);
            Assert.AreEqual(2, store.Partitions.Count);
            var result = await store.SplitBucketAsync("ABEF000");
            Assert.IsTrue(result.Published, result.Reason); Assert.AreEqual(2, result.ChildrenCreated);
            Assert.AreEqual(4, store.Partitions.Count);
            Assert.AreEqual(0, store.Partitions.Single(p => Prefix(p) == "ABC").SplitDepth);
            Assert.AreEqual(4, store.Partitions.Single(p => Prefix(p) == "ABE").SplitDepth);
            await Populate(store, "ABEFX", 16);
            Assert.IsTrue((await store.SplitBucketAsync("ABEF000")).Published);
            Assert.AreEqual(5, store.Partitions.Single(p => Prefix(p) == "ABEF").SplitDepth);
            await store.SaveAsync("ABEH000", new byte[] { 9 }); // lazily register a previously unseen child
            await store.SaveAsync("ZZZ-new", new byte[] { 8 }); // independently register a new root
            Assert.AreEqual(55, (await store.ReadAllAsync()).Count);
            await store.CheckpointAsync();
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options with { MaximumConcurrentPartitionWrites = 2 });
        Assert.AreEqual(55, (await reopened.ReadAllAsync()).Count);
        Assert.AreEqual((byte)9, (await reopened.GetAsync("ABEH000"))![0]);
        Assert.AreEqual((byte)1, (await reopened.GetAsync("ABEF000"))![0]);
        Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
    }

    [TestMethod]
    public async Task CommonByteRunsAreSkippedWithoutLosingShortKeysOrUnicode()
    {
        using var root = new Temp(); await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options());
        foreach (var key in new[] { "ABE", "ABEcommon", "ABEcommonF0", "ABEcommonF1", "ABEcommonG0", "ABEcommonG1", "AB", "éé-a", "éé-b" })
            await store.SaveAsync(key, Encoding.UTF8.GetBytes(key));
        var result = await store.SplitBucketAsync("ABEcommonF0");
        Assert.IsTrue(result.Published, result.Reason);
        Assert.AreEqual(10, store.Partitions.Single(p => Prefix(p) == "ABE").SplitDepth);
        foreach (var row in await store.ReadAllAsync()) Assert.AreEqual(row.Key, Encoding.UTF8.GetString(row.Payload));
        Assert.AreEqual(9, (await store.ReadAllAsync()).Count);
        await store.SaveAsync("ABEcommonH", new byte[] { 7 });
        Assert.IsNotNull(await store.GetAsync("ABEcommon")); Assert.IsNotNull(await store.GetAsync("ABE"));
        await store.DeleteAsync("ABEcommonF0"); Assert.IsNull(await store.GetAsync("ABEcommonF0"));
        await store.CompactAsync(); Assert.IsNull(await store.GetAsync("ABEcommonF0"));
    }

    [TestMethod]
    public async Task AutomaticWorkerDrainsRepeatedRequestsWithoutChangingPayloads()
    {
        using var root = new Temp(); var options = Options(true);
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var prefix = $"{iteration:X3}";
            await Populate(store, prefix + "A", 3); await Populate(store, prefix + "B", 3);
            await store.FlushPartitionMaintenanceAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(4, store.Partitions.Single(p => Prefix(p) == prefix).SplitDepth);
        }
        Assert.AreEqual(8L, store.PartitionMaintenanceStatistics.PublishedSplits);
        Assert.AreEqual(48, (await store.ReadAllAsync()).Count);
        Assert.AreEqual(0L, store.PartitionMaintenanceStatistics.Failures);
    }

    [TestMethod]
    public async Task ConcurrentWritersReadersAndScansDuringSplitsKeepEveryAcknowledgedRecord()
    {
        using var root = new Temp(); var options = Options(true) with
        { Partitioning = Options(true).Partitioning with { SplitAboveLiveRecords = 20, MaximumPartitions = 1024 } };
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        {
            var stop = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var rows = await store.ReadAllAsync();
                    Assert.AreEqual(rows.Count, rows.Select(r => r.Key).Distinct().Count());
                    foreach (var row in rows) Assert.AreEqual(row.Key, Encoding.UTF8.GetString(row.Payload));
                    await Task.Yield();
                }
            });
            try
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(async writer =>
                {
                    for (var i = 0; i < 36; i++)
                    {
                        var key = $"ABC{writer:X1}-{i:D3}";
                        await store.SaveAsync(key, Encoding.UTF8.GetBytes(key));
                        Assert.AreEqual(key, Encoding.UTF8.GetString((await store.GetAsync(key))!));
                    }
                }));
                await store.FlushPartitionMaintenanceAsync();
            }
            finally { stop.Cancel(); await reader; stop.Dispose(); }
            Assert.AreEqual(288, (await store.ReadAllAsync()).Count);
            Assert.IsTrue(store.PartitionMaintenanceStatistics.PublishedSplits > 0);
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(288, (await reopened.ReadAllAsync()).Count);
    }

    [TestMethod]
    [DataRow("maximum-prefix-depth")]
    [DataRow("partition-limit")]
    [DataRow("metadata-budget")]
    [DataRow("copy-byte-budget")]
    [DataRow("insufficient-child-population")]
    public async Task BoundedDeferralsNeverPreventExistingBucketWrites(string reason)
    {
        using var root = new Temp(); var p = Options().Partitioning;
        p = reason switch
        {
            "maximum-prefix-depth" => p with { MaximumPrefixBytes = 3 },
            "partition-limit" => p with { MaximumPartitions = 2 },
            "metadata-budget" => p with { MaximumSplitRecords = 2 },
            "copy-byte-budget" => p with { MaximumSplitBytes = 1 },
            _ => p with { MinimumChildRecords = 4 }
        };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options() with { Partitioning = p });
        await Populate(store, "ABCF", 3); await Populate(store, "ABCG", 3);
        var result = await store.SplitBucketAsync("ABCF000");
        Assert.IsFalse(result.Published); Assert.AreEqual(reason, result.Reason);
        await store.SaveAsync("ABCF000", new byte[] { 7 });
        Assert.AreEqual((byte)7, (await store.GetAsync("ABCF000"))![0]);
        Assert.AreEqual(6, (await store.ReadAllAsync()).Count);
    }

    [TestMethod]
    public async Task GrowthUsesLiveBytesAndRewritesDoNotInflateLiveCount()
    {
        using var root = new Temp(); var options = Options() with
        { Partitioning = Options().Partitioning with { SplitAboveLiveRecords = 100, SplitAboveLiveBytes = 8192 } };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await store.SaveAsync("ABCF", new byte[] { 1 }); await store.SaveAsync("ABCG", new byte[] { 1 });
        for (var i = 0; i < 16; i++) await store.SaveAsync("ABCF", new byte[] { 1 });
        Assert.AreEqual("below-threshold", (await store.SplitBucketAsync("ABCF")).Reason);
        await store.SaveAsync("ABCF", new byte[16384]);
        Assert.IsTrue((await store.SplitBucketAsync("ABCF")).Published);
        Assert.AreEqual(16384, (await store.GetAsync("ABCF"))!.Length);
    }

    [TestMethod]
    public async Task NewPrefixLimitRejectsBeforeWriteButExistingAndMissingReadsWork()
    {
        using var root = new Temp(); var options = Options() with { Partitioning = Options().Partitioning with { MaximumPartitions = 1 } };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await store.SaveAsync("ABC1", new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("XYZ1", new byte[] { 1 }).AsTask());
        Assert.IsNull(await store.GetAsync("XYZ1")); await store.SaveAsync("ABC2", new byte[] { 2 });
        Assert.AreEqual(2, (await store.ReadAllAsync()).Count);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task TransactionsRetentionCompactionAndBackupSurviveSplit(bool concurrent)
    {
        using var root = new Temp(); var source = Path.Combine(root.Path, "source");
        var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(source, options))
        {
            store.EnableRetainedVersions(new());
            await Populate(store, "ABCF", 3); await Populate(store, "ABCG", 3);
            using var retained = await store.AcquireRetainedAsync("ABCF000");
            Assert.IsTrue((await store.SplitBucketAsync("ABCF000")).Published);
            await using (var transaction = await store.BeginTransactionAsync())
            {
                await transaction.SaveAsync("ABCF000", new byte[] { 5 });
                await transaction.SaveAsync("ABCG000", new byte[] { 6 });
                await transaction.DeleteAsync("ABCF001"); await transaction.CommitAsync();
            }
            await using (var rolledBack = await store.BeginTransactionAsync())
                await rolledBack.SaveAsync("ABCF000", new byte[] { 99 });
            await store.DeleteAsync("ABCG001");
            if (concurrent) Assert.IsTrue((await store.CompactConcurrentAsync(new() { MinimumFreeSpaceReserveBytes = 0 })).Published);
            else await store.CompactAsync();
            Assert.AreEqual((byte)1, (await retained.ReadAsync())[0]);
            Assert.AreEqual((byte)5, (await store.GetAsync("ABCF000"))![0]);
            Assert.IsNull(await store.GetAsync("ABCF001")); Assert.IsNull(await store.GetAsync("ABCG001"));
            await store.BackupAsync(Path.Combine(root.Path, "backup"));
        }
        await AtlasCoreStore.RestoreAsync(Path.Combine(root.Path, "backup"), Path.Combine(root.Path, "restore"), options);
        await using var reopened = await AtlasCoreStore.OpenAsync(source, options);
        await using var restored = await AtlasCoreStore.OpenAsync(Path.Combine(root.Path, "restore"), options);
        foreach (var store in new[] { reopened, restored })
        {
            Assert.AreEqual(4, (await store.ReadAllAsync()).Count);
            Assert.AreEqual((byte)6, (await store.GetAsync("ABCG000"))![0]);
            Assert.IsNull(await store.GetAsync("ABCF001")); Assert.IsTrue((await store.VerifyIntegrityAsync()).IsValid);
        }
    }

    [TestMethod]
    [DataRow("children-durable", false)] [DataRow("ownership-published", true)]
    public async Task PublicationFailureEitherKeepsParentOrRequiresRecovery(string stage, bool committed)
    {
        using var root = new Temp(); var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options with
        { SplitTestHook = step => { if (step == stage) throw new IOException("injected split failure"); } }))
        {
            await Populate(store, "ABCF", 3); await Populate(store, "ABCG", 3);
            await Assert.ThrowsAsync<IOException>(() => store.SplitBucketAsync("ABCF000").AsTask());
            if (committed) await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("ABCF000").AsTask());
            else Assert.AreEqual(6, (await store.ReadAllAsync()).Count);
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(committed ? 3 : 1, reopened.Partitions.Count);
        Assert.AreEqual(6, (await reopened.ReadAllAsync()).Count);
        if (!committed) Assert.IsTrue((await reopened.SplitBucketAsync("ABCF000")).Published);
    }

    [TestMethod]
    public async Task CorruptNewestMapNeverFallsBackToAnOlderMap()
    {
        using var root = new Temp(); var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        { await Populate(store, "ABCF", 3); await Populate(store, "ABCG", 3); await store.SplitBucketAsync("ABCF000"); }
        var newest = Directory.GetFiles(Path.Combine(root.Path, "hybrid-placement"), "map-*.idbmap").Order(StringComparer.Ordinal).Last();
        var bytes = await File.ReadAllBytesAsync(newest); bytes[^1] ^= 1; await File.WriteAllBytesAsync(newest, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    [TestMethod]
    public async Task EmptyStoreSupportsCheckpointCompactionAndBackup()
    {
        using var root = new Temp(); var options = Options();
        await using var store = await AtlasCoreStore.OpenAsync(Path.Combine(root.Path, "store"), options);
        Assert.AreEqual(0, (await store.CheckpointAsync()).SegmentGroupsCheckpointed);
        await store.CompactAsync();
        Assert.IsTrue((await store.CompactConcurrentAsync(new() { MinimumFreeSpaceReserveBytes = 0 })).Published);
        await store.BackupAsync(Path.Combine(root.Path, "backup"));
        Assert.AreEqual(0, (await store.ReadAllAsync()).Count);
    }

    internal sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-hybrid-test-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!System.IO.Path.GetFileName(resolved).StartsWith("atlas-hybrid-test-", StringComparison.Ordinal)
                || !string.Equals(System.IO.Path.GetDirectoryName(resolved), System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test cleanup target.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
