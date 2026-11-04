using System.Diagnostics;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.AtlasCore.Tests;

// Regression coverage for logical identity and compaction with a consumer-held scan.
public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    public async Task ReviewRegression_Atlas_ExactExistenceDoesNotAcceptAnotherLogicalGroup()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repo.InitializeAsync();
        var first = await repo.SaveAsync(new AtlasProviderRecord { Id = "order", Score = 1 });
        var second = await repo.SaveAsync(new AtlasProviderRecord { Id = "order-0", Score = 2 });
        Assert.IsTrue(await repo.DeleteAsync(first));
        Assert.IsFalse(await repo.ExistsAsync(first));
        Assert.IsTrue(await repo.ExistsAsync(second));
        var lookup = (IPhysicalEnvelopeLookup)provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        Assert.IsFalse(await lookup.ExistsPhysicalAsync(first.Envelope!.EnvelopeCacheKey));
        Assert.IsTrue(await lookup.ExistsPhysicalAsync(second.Envelope!.EnvelopeCacheKey));
    }

    [TestMethod]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    public async Task ReviewRegression_Atlas_CachedEditsRemainPrivate(StorageObjectCacheMode mode)
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with { ObjectCacheMode = mode });
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repo.InitializeAsync();
        var record = await repo.SaveAsync(new AtlasProviderRecord { Id = "cached", Score = 1 });
        record.Score = 999;
        var source = (ICachedEnvelopeSource<IAtlasProviderRecord>)provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        Assert.IsTrue(source.IsolatesCachedReads);
        Assert.IsTrue(source.EnumerateCachedObjects().All(entry => entry.Value.Score == 1 && !ReferenceEquals(entry.Value, record)));
        Assert.IsEmpty(await ToListAsync(repo.SearchManyAsync(r => r.Score == 999)));
        var original = (await ToListAsync(repo.SearchManyAsync(r => r.Score == 1))).Single();
        Assert.AreNotSame(record, original);
        await repo.SaveAsync(record);
        Assert.IsNotNull(await repo.SearchFirstAsync(r => r.Score == 999));
        Assert.IsNull(await repo.SearchFirstAsync(r => r.Score == 1));
        Assert.IsTrue(await repo.DeleteAsync(record));
        Assert.IsEmpty(source.EnumerateCachedObjects());
        Assert.IsEmpty(await ToListAsync(repo.SearchManyAsync(r => r.Score == 999)));
    }

    private static void Report(string name, IEnumerable<string> lines)
    {
        var observed = string.Join("\n", lines);
        Console.WriteLine(observed);
        if (name == "storage-key-digits") StringAssert.Contains(observed, "QueryFirstByStorageKey(order-6): six");
        else if (name == "compaction-scan-deadlock") StringAssert.Contains(observed, "save inside scan completed");
        else Assert.Fail("Unasserted regression: " + name);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [TestMethod]
    public async Task ReviewRegression_Atlas_StorageKeyEndingInDigits()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repo.InitializeAsync();
        var five = await repo.SaveAsync(new AtlasProviderRecord { Id = "order-5", Name = "five" });
        var six = await repo.SaveAsync(new AtlasProviderRecord { Id = "order-6", Name = "six" });
        Report("storage-key-digits", new[]
        {
            "order-5 physical key: " + five.Envelope!.EnvelopeCacheKey,
            "order-6 physical key: " + six.Envelope!.EnvelopeCacheKey,
            "QueryFirstByStorageKey(order-6): " + ((await repo.QueryFirstByStorageKeyAsync("order-6"))?.Name ?? "<null>"),
        });
    }

    [TestMethod]
    public async Task ReviewRegression_Atlas_CompactionVersusOpenScanWithWrite()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
        await repo.InitializeAsync();
        for (var round = 0; round < 3; round++)
        {
            var batch = new List<IAtlasProviderRecord>();
            for (var i = 0; i < 100; i++) batch.Add(new AtlasProviderRecord { Id = "c" + round + "x" + i, Name = "n", Score = i });
            await repo.SaveManyAsync(batch);
        }
        await maintenance.CheckpointAsync();
        var lines = new List<string>();
        Task? compaction = null;
        await foreach (var record in repo.QueryManyAsync(_ => true))
        {
            compaction = maintenance.CompactAsync().AsTask();
            await Task.Delay(1500);
            lines.Add("compaction completed while scan open: " + compaction.IsCompleted);
            var save = repo.SaveAsync(record).AsTask();
            var winner = await Task.WhenAny(save, Task.Delay(5000));
            lines.Add(winner == save ? "save inside scan completed" : "save inside scan BLOCKED for >5000 ms");
            break; // releasing the scan lets compaction (and then the save) proceed
        }
        var sw = Stopwatch.StartNew();
        try { await compaction!; lines.Add($"compaction finished {sw.ElapsedMilliseconds} ms after the scan was released"); }
        catch (Exception ex) { lines.Add("compaction ended with " + ex.GetType().Name); }
        Report("compaction-scan-deadlock", lines);
    }


}
