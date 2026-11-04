using System.Collections.Immutable;
using System.Text;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class FixedLayoutTests
{
    internal static AtlasCoreOptions Options(int count = 4) => HybridPartitionTests.Options() with
    {
        MaximumConcurrentPartitionWrites = 2,
        Partitioning = HybridPartitionTests.Options().Partitioning with
        {
            FixedPrefixes = Enumerable.Range(0, count).Select(i => i.ToString("X3", System.Globalization.CultureInfo.InvariantCulture)).ToImmutableArray(),
            MaximumPartitions = count, SplitAboveLiveRecords = 1, SplitAboveLiveBytes = 1
        }
    };

    [TestMethod]
    [DataRow(1)] [DataRow(4)] [DataRow(16)]
    public async Task InitializationCreatesExactlyTheRequestedEmptyOwnersAndWritesNeverGrowThem(int count)
    {
        using var root = new HybridPartitionTests.Temp(); var options = Options(count);
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(count, store.Partitions.Count); Assert.AreEqual(count, store.Statistics.OpenSegmentGroups);
        Assert.AreEqual(0L, store.Statistics.Saves); Assert.AreEqual(0, await store.CountAsync());
        Assert.AreEqual(count, Directory.GetDirectories(root.Path, "bucket-*", SearchOption.AllDirectories).Length);
        var generation = store.Statistics.PlacementGeneration;
        var mapCount = Directory.GetFiles(Path.Combine(root.Path, "hybrid-placement"), "map-*.idbmap").Length;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
        {
            var key = $"{i % count:X3}/item-{i:D4}";
            await store.SaveAsync(key, Encoding.UTF8.GetBytes(key));
            Assert.AreEqual(key, Encoding.UTF8.GetString((await store.GetAsync(key))!));
        }));
        await store.FlushPartitionMaintenanceAsync();
        Assert.AreEqual(64, await store.CountAsync()); Assert.AreEqual(count, store.Partitions.Count);
        Assert.IsTrue(store.Partitions.All(p => p.ParentId == -1 && p.SplitDepth == 0));
        Assert.AreEqual(generation, store.Statistics.PlacementGeneration);
        Assert.AreEqual(mapCount, Directory.GetFiles(Path.Combine(root.Path, "hybrid-placement"), "map-*.idbmap").Length);
        Assert.AreEqual(0L, store.PartitionMaintenanceStatistics.Attempts);
        Assert.AreEqual(0L, store.PartitionMaintenanceStatistics.PublishedSplits);
        Assert.AreEqual(2, store.Statistics.MaximumConcurrentPartitionWrites, "I/O concurrency is not the owner count.");
    }

    [TestMethod]
    public async Task ManualSplitAndUnknownPrefixCannotChangeTheLayout()
    {
        using var root = new HybridPartitionTests.Temp(); await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options());
        await store.SaveAsync("000/F", new byte[] { 1 }); await store.SaveAsync("000/G", new byte[] { 2 });
        var generation = store.Statistics.PlacementGeneration;
        var split = await store.SplitBucketAsync("000/F");
        Assert.IsFalse(split.Published); Assert.AreEqual("fixed-layout", split.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("ZZZ/new", new byte[] { 9 }).AsTask());
        Assert.IsNull(await store.GetAsync("ZZZ/new")); Assert.IsFalse(await store.DeleteAsync("ZZZ/new"));
        Assert.AreEqual(generation, store.Statistics.PlacementGeneration); Assert.AreEqual(4, store.Partitions.Count);
        Assert.AreEqual(0L, store.PartitionMaintenanceStatistics.Attempts, "Fixed layout must not start maintenance work.");
        await store.SaveAsync("001/valid", new byte[] { 3 }); Assert.AreEqual(3, await store.CountAsync());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task FixedOwnershipSurvivesReopenCompactionAndBackup(bool concurrent)
    {
        using var root = new HybridPartitionTests.Temp(); var path = Path.Combine(root.Path, "store"); var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(path, options))
        {
            await store.SaveAsync("000/one", new byte[] { 1 }); await store.SaveAsync("000/one", new byte[] { 2 });
            await using (var transaction = await store.BeginTransactionAsync())
            { await transaction.SaveAsync("001/two", new byte[] { 3 }); await transaction.SaveAsync("002/three", new byte[] { 4 }); await transaction.CommitAsync(); }
            await store.DeleteAsync("002/three");
            if (concurrent) Assert.IsTrue((await store.CompactConcurrentAsync(new() { MinimumFreeSpaceReserveBytes = 0 })).Published);
            else await store.CompactAsync();
            await store.BackupAsync(Path.Combine(root.Path, "backup"));
        }
        await AtlasCoreStore.RestoreAsync(Path.Combine(root.Path, "backup"), Path.Combine(root.Path, "restore"), options);
        foreach (var candidate in new[] { path, Path.Combine(root.Path, "restore") })
        {
            await using var reopened = await AtlasCoreStore.OpenAsync(candidate, options with
            { MaximumConcurrentPartitionWrites = 1, Partitioning = options.Partitioning with { FixedPrefixes = ["003", "001", "002", "000"] } });
            Assert.AreEqual(4, reopened.Partitions.Count); Assert.AreEqual(4, reopened.Statistics.OpenSegmentGroups);
            Assert.AreEqual(2, await reopened.CountAsync()); Assert.AreEqual((byte)2, (await reopened.GetAsync("000/one"))![0]);
            Assert.IsNull(await reopened.GetAsync("002/three")); Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
        }
    }

    [TestMethod]
    public async Task ReopenCannotSilentlySwitchModesOrPrefixes()
    {
        using var root = new HybridPartitionTests.Temp(); var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options)) { await store.SaveAsync("000/one", new byte[] { 1 }); }
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path, HybridPartitionTests.Options()).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path,
            options with { Partitioning = options.Partitioning with { FixedPrefixes = ["000", "001", "002", "ZZZ"] } }).AsTask());
        using var dynamicRoot = new HybridPartitionTests.Temp();
        await using (var dynamicStore = await AtlasCoreStore.OpenAsync(dynamicRoot.Path, HybridPartitionTests.Options())) { }
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(dynamicRoot.Path, options).AsTask());
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual((byte)1, (await reopened.GetAsync("000/one"))![0]);
    }

    [TestMethod]
    [DataRow("duplicate")] [DataRow("short")] [DataRow("long")] [DataRow("automatic")]
    [DataRow("capacity")] [DataRow("default-array")] [DataRow("invalid-unicode")]
    public async Task InvalidConfigurationIsRejectedBeforeCreatingAStore(string invalid)
    {
        using var root = new HybridPartitionTests.Temp(); var p = Options().Partitioning;
        p = invalid switch
        {
            "duplicate" => p with { FixedPrefixes = ["000", "000"] },
            "short" => p with { FixedPrefixes = ["00"] },
            "long" => p with { FixedPrefixes = ["0000"] },
            "automatic" => p with { EnableAutomaticSplitting = true },
            "capacity" => p with { MaximumPartitions = 1 },
            "default-array" => p with { FixedPrefixes = default },
            _ => p with { FixedPrefixes = ["\uD800"] }
        };
        var path = Path.Combine(root.Path, "unused");
        await Assert.ThrowsAsync<ArgumentException>(() => AtlasCoreStore.OpenAsync(path, Options() with { Partitioning = p }).AsTask());
        Assert.IsFalse(Directory.Exists(path));
    }

    [TestMethod]
    public async Task FixedPrefixesAreByteBasedAndPersistInCanonicalOrder()
    {
        using var root = new HybridPartitionTests.Temp(); var options = Options() with
        { Partitioning = Options().Partitioning with { FixedPrefixes = ["éB", "éA"] } };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await store.SaveAsync("éA/item", new byte[] { 1 }); await store.SaveAsync("éB/item", new byte[] { 2 });
        Assert.AreEqual("C3A941", store.Partitions[0].PrefixHex); Assert.AreEqual("C3A942", store.Partitions[1].PrefixHex);
        Assert.AreEqual(2, await store.CountAsync());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task FailedInitialActivationReleasesResourcesAndCanBeReopened(bool publicationUncertain)
    {
        using var root = new HybridPartitionTests.Temp(); var fs = new FailInitialization(publicationUncertain);
        var options = Options() with { DurableFileSystem = fs };
        await Assert.ThrowsAsync<IOException>(() => AtlasCoreStore.OpenAsync(root.Path, options).AsTask());
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual(4, reopened.Statistics.OpenSegmentGroups); Assert.AreEqual(0, await reopened.CountAsync());
        await reopened.SaveAsync("001/new", new byte[] { 1 }); Assert.AreEqual(1, await reopened.CountAsync());
    }

    [TestMethod]
    public async Task LosingAReadyFixedOwnerIsNotSilentlyReinitializedAsEmpty()
    {
        using var root = new HybridPartitionTests.Temp(); var options = Options();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options)) { await store.SaveAsync("000/one", new byte[] { 1 }); }
        var owner = Directory.GetDirectories(root.Path, "bucket-00000000", SearchOption.AllDirectories).Single();
        foreach (var file in Directory.GetFiles(Path.Combine(owner, "segments"), "segment-*.idbag")) File.Delete(file);
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    private sealed class FailInitialization(bool publicationUncertain) : IDurableFileSystem
    {
        private readonly IDurableFileSystem _inner = PlatformDurableFileSystem.Instance;
        private int _armed = 1;
        public string DirectoryFlushStrategy => _inner.DirectoryFlushStrategy;
        public void FlushFile(SafeFileHandle handle) => _inner.FlushFile(handle);
        public void FlushDirectory(string path) => _inner.FlushDirectory(path);
        public void ReplaceFile(string temporary, string final) => _inner.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final)
        {
            var target = publicationUncertain ? final.EndsWith("map-00000000000000000002.idbmap", StringComparison.Ordinal)
                : final.EndsWith(".idbag", StringComparison.Ordinal);
            if (target && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                if (publicationUncertain) _inner.PublishFile(temporary, final);
                throw new IOException("Injected fixed initialization failure.");
            }
            _inner.PublishFile(temporary, final);
        }
    }
}
