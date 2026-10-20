using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

// Regression coverage adapted from the independent storage review reproductions.
public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    public async Task ReviewRegression_ExactExistenceDoesNotDeserializeOrAcceptLogicalAliases()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        IProviderRecord first, second;
        await using (var writer = BuildProvider(config, ProviderOptions()))
        {
            var repo = writer.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            first = await repo.SaveAsync(new ProviderRecord { Id = "order", Score = 1 });
            second = await repo.SaveAsync(new ProviderRecord { Id = "order-0", Score = 2 });
            Assert.IsTrue(await repo.DeleteAsync(first));
        }
        var counter = new CountingObjectSerializer();
        await using var reader = BuildProvider(config, ProviderOptions(), serializer: counter);
        var repository = reader.GetRequiredService<IRepository<IProviderRecord>>();
        // Existence itself initializes the provider's metadata; no query/index hydration is needed.
        Assert.IsFalse(await repository.ExistsAsync(first));
        Assert.IsTrue(await repository.ExistsAsync(second));
        Assert.AreEqual(0, counter.DeserializeCount);
        Assert.AreEqual(0, counter.ReadRevisionCount);
        var envelopeRepository = reader.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
        var passthrough = new Idb.Libraries.Implementation.Indexes.PassthroughIndexManager<IProviderRecord>(envelopeRepository);
        Assert.IsFalse(await passthrough.ExistsAsync(first));
        Assert.IsTrue(await passthrough.ExistsAsync(second));
        Assert.AreEqual(0, counter.DeserializeCount);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => repository.ExistsAsync(second, cancelled.Token).AsTask());
    }

    private static void Report(string name, IEnumerable<string> lines)
    {
        var observed = string.Join("\n", lines);
        Console.WriteLine(observed);
        switch (name)
        {
            case "search-vs-scan":
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(observed, @"scan=(\d+) search=([^\r\n]+)"))
                    Assert.AreEqual(match.Groups[1].Value, match.Groups[2].Value);
                break;
            case "storage-key-digits":
                StringAssert.Contains(observed, "QueryFirstByStorageKey(order-6): six"); break;
            case "single-key-digits":
                StringAssert.Contains(observed, "stored as: invoice-2026");
                StringAssert.Contains(observed, "stored as: invoice-2027"); break;
            case "weak-cache-impl-query":
                StringAssert.Contains(observed, "implementation-query=10"); break;
            case "compaction-scan-deadlock":
                StringAssert.Contains(observed, "save inside scan completed"); break;
            case "recoverable-failure-faults-store":
            case "cancellation-faults-store":
                Assert.IsFalse(observed.Contains("FAILED"), observed); break;
            case "index-subscription-dropped":
                StringAssert.Contains(observed, "indexed search finds -> 1");
                StringAssert.Contains(observed, "getter threw"); break;
            case "strong-cache-aliasing":
                break; // Dedicated cache consistency assertions are added at the call site.
            default: Assert.Fail("Unasserted regression: " + name); break;
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [TestMethod]
    public async Task ReviewRegression_SearchVersusScan_TypeConversions()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repo.InitializeAsync();
        await repo.SaveManyAsync(new IProviderRecord[]
        {
            new ProviderRecord { Id = "a", Name = "alpha", Score = 7 },
            new ProviderRecord { Id = "b", Name = "beta", Score = 3 },
        });
        long seven = 7;
        var lines = new List<string>();
        async Task Compare(string label, System.Linq.Expressions.Expression<Func<IProviderRecord, bool>> e)
        {
            var scan = (await ToListAsync(repo.QueryManyAsync(e.Compile()))).Count;
            string search;
            try { search = (await ToListAsync(repo.SearchManyAsync(e))).Count.ToString(); }
            catch (Exception ex) { search = "THROWS " + ex.GetType().Name; }
            lines.Add($"{label,-34} scan={scan} search={search}");
        }
        await Compare("int == long variable", r => r.Score == seven);
        await Compare("int > 1.5 (double literal)", r => r.Score > 1.5);
        await Compare("int < 7.5 (double literal)", r => r.Score < 7.5);
        await Compare("int == literal (control)", r => r.Score == 7);
        Report("search-vs-scan", lines);
    }

    [TestMethod]
    public async Task ReviewRegression_StorageKeyEndingInDigits()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repo.InitializeAsync();
        var saved = await repo.SaveAsync(new ProviderRecord { Id = "order-5", Name = "five" });
        var saved6 = await repo.SaveAsync(new ProviderRecord { Id = "order-6", Name = "six" });
        var lines = new List<string>
        {
            "physical key assigned        : " + saved.Envelope!.EnvelopeCacheKey,
            "QueryFirstByStorageKey(order-5): " + ((await repo.QueryFirstByStorageKeyAsync("order-5"))?.Name ?? "<null>"),
            "QueryFirstByStorageKey(order)  : " + ((await repo.QueryFirstByStorageKeyAsync("order"))?.Name ?? "<null>"),
            "order-6 physical key           : " + saved6.Envelope!.EnvelopeCacheKey,
            "QueryFirstByStorageKey(order-6): " + ((await repo.QueryFirstByStorageKeyAsync("order-6"))?.Name ?? "<null>"),
            "QueryManyByStorageKey(order-6) : " + string.Join(",", (await ToListAsync(repo.QueryManyByStorageKeyAsync("order-6"))).Select(x => x.Name)),
        };
        Report("storage-key-digits", lines);
    }

    private sealed class SingleKeyFromIdFactory : IEnvelopeStorageIdentityFactory<IProviderRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public void InitEnvelope(IObjectEnvelope<IProviderRecord> envelope)
        {
            envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id;
            envelope.Location ??= envelope.EnvelopeCacheKey;
        }
    }

    [TestMethod]
    public async Task ReviewRegression_SingleCardinalityKeysEndingInDigits()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions(), new SingleKeyFromIdFactory());
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repo.InitializeAsync();
        var lines = new List<string>();
        var first = await repo.SaveAsync(new ProviderRecord { Id = "invoice-2026", Name = "first" });
        lines.Add("first requested key invoice-2026 -> stored as: " + first.Envelope!.EnvelopeCacheKey);
        try
        {
            var second = await repo.SaveAsync(new ProviderRecord { Id = "invoice-2027", Name = "second" });
            lines.Add("second requested key invoice-2027 -> stored as: " + second.Envelope!.EnvelopeCacheKey);
        }
        catch (Exception ex) { lines.Add("second save THROWS " + ex.GetType().Name + ": " + ex.Message); }
        Report("single-key-digits", lines);
    }

    [TestMethod]
    public async Task ReviewRegression_WeakCacheImplementationQuery()
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.WeakObject,
            RaiseNotifications = true,
            HydrateOnInitialize = false
        };
        var config = new ProviderConfig(root.Path, policy);
        await using (var writer = BuildProvider(config, ProviderOptions()))
        {
            var repo = writer.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            for (var i = 0; i < 10; i++)
                await repo.SaveAsync(new ProviderRecord { Id = "weak" + (char)('a' + i), Name = "n" + i, Score = i });
        }
        await using var reader = BuildProvider(config, ProviderOptions());
        var r = reader.GetRequiredService<IRepository<IProviderRecord>>();
        await r.InitializeAsync();
        var held = await r.QueryFirstByStorageKeyAsync("weaka"); // now weakly cached and kept alive
        var implCount = (await ToListAsync(r.QueryManyAsync<ProviderRecord>(_ => true))).Count;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var contractCount = (await ToListAsync(r.QueryManyAsync(_ => true))).Count;
        Report("weak-cache-impl-query", new[]
        {
            $"records=10 contract-query={contractCount} implementation-query={implCount}",
        });
        GC.KeepAlive(held);
    }

    [TestMethod]
    public async Task ReviewRegression_CompactionVersusOpenScanWithWrite()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        var maintenance = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();
        await repo.InitializeAsync();
        for (var round = 0; round < 3; round++)
        {
            var batch = new List<IProviderRecord>();
            for (var i = 0; i < 100; i++) batch.Add(new ProviderRecord { Id = "c" + round + "x" + i, Name = "n", Score = i });
            await repo.SaveManyAsync(batch);
        }
        await maintenance.CheckpointAsync();
        var lines = new List<string>();
        using var cts = new CancellationTokenSource();
        Task? compaction = null;
        await foreach (var record in repo.QueryManyAsync(_ => true))
        {
            compaction = maintenance.CompactAsync(cts.Token).AsTask();
            await Task.Delay(1500);
            lines.Add("compaction completed while scan open: " + compaction.IsCompleted);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var save = repo.SaveAsync(record).AsTask();
            var winner = await Task.WhenAny(save, Task.Delay(5000));
            lines.Add(winner == save
                ? $"save inside scan completed in {sw.ElapsedMilliseconds} ms"
                : "save inside scan BLOCKED for >5000 ms (compaction holds writers while waiting for this scan to end)");
            cts.Cancel();
            try { await save; lines.Add($"save finished after cancelling compaction at {sw.ElapsedMilliseconds} ms"); }
            catch (Exception ex) { lines.Add("save failed: " + ex.GetType().Name); }
            break;
        }
        try { await compaction!; lines.Add("compaction finished"); }
        catch (Exception ex) { lines.Add("compaction ended with " + ex.GetType().Name); }
        Report("compaction-scan-deadlock", lines);
    }

    private static SegmentCoreRepositoryOptions TinyQueueOptions(Idb.Storage.SegmentCore.ObjectLog.ObjectLogQueueFullBehavior behavior) => ProviderOptions() with
    {
        StoreOptions = ProviderOptions().StoreOptions with
        {
            LogOptions = ProviderOptions().StoreOptions.LogOptions with
            {
                StripeCount = 1,
                QueueCapacityPerStripe = 1,
                QueueFullBehavior = behavior
            }
        }
    };

    [TestMethod]
    public async Task ReviewRegression_RecoverableBatchFailureFaultsStore()
    {
        var lines = new List<string>();
        foreach (var mode in new[] { "reject", "cancel" })
        {
            using var root = new ProviderTemporaryDirectory();
            var config = new ProviderConfig(root.Path);
            var behavior = mode == "reject"
                ? Idb.Storage.SegmentCore.ObjectLog.ObjectLogQueueFullBehavior.Reject
                : Idb.Storage.SegmentCore.ObjectLog.ObjectLogQueueFullBehavior.Wait;
            await using var provider = BuildProvider(config, TinyQueueOptions(behavior));
            var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            var batch = Enumerable.Range(0, 2000).Select(i => (IProviderRecord)new ProviderRecord { Id = mode + i, Name = "n" }).ToList();
            using var cts = new CancellationTokenSource();
            if (mode == "cancel") cts.CancelAfter(TimeSpan.FromMilliseconds(2));
            try { await repo.SaveManyAsync(batch, cts.Token); lines.Add($"{mode}: batch committed"); }
            catch (Exception ex) { lines.Add($"{mode}: batch failed with {ex.GetType().Name}"); }
            try { await repo.SaveAsync(new ProviderRecord { Id = mode + "-after", Name = "x" }); lines.Add($"{mode}: next unrelated save OK"); }
            catch (Exception ex) { lines.Add($"{mode}: next unrelated save FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }
        Report("recoverable-failure-faults-store", lines);
    }

    [TestMethod]
    public async Task ReviewRegression_CancellationDuringBatchFaultsStore()
    {
        var lines = new List<string>();
        foreach (var delay in new[] { 20, 50, 100, 200, 400 })
        {
            using var root = new ProviderTemporaryDirectory();
            var config = new ProviderConfig(root.Path);
            await using var provider = BuildProvider(config, TinyQueueOptions(Idb.Storage.SegmentCore.ObjectLog.ObjectLogQueueFullBehavior.Wait));
            var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
            await repo.InitializeAsync();
            var batch = Enumerable.Range(0, 20000).Select(i => (IProviderRecord)new ProviderRecord { Id = "k" + i, Name = "n" }).ToList();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(delay));
            string first;
            try { await repo.SaveManyAsync(batch, cts.Token); first = "committed"; }
            catch (Exception ex) { first = ex.GetType().Name; }
            string next;
            try { await repo.SaveAsync(new ProviderRecord { Id = "after", Name = "x" }); next = "next save OK"; }
            catch (Exception ex) { next = "next save FAILED: " + ex.Message; }
            lines.Add($"cancel after {delay} ms: batch={first}; {next}");
        }
        Report("cancellation-faults-store", lines);
    }



    private sealed class RatioConfig(string root) : IDbContractConfigDto<IRatioRecord>
    {
        public string EnvelopeDICacheKey => "ratio-probe";
        public string ContractName => "RatioRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.SegmentCore;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy { get; } = Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
        {
            ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            RaiseNotifications = true,
            HydrateOnInitialize = false
        };
    }

    [TestMethod]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    [DataRow(StorageObjectCacheMode.MetadataOnly)]
    public async Task ReviewRegression_StrongCacheAliasing(StorageObjectCacheMode mode)
    {
        using var root = new ProviderTemporaryDirectory();
        var policy = StoragePolicy.Default with { ObjectCacheMode = mode, RaiseNotifications = true, HydrateOnInitialize = false };
        var config = new ProviderConfig(root.Path, policy);
        await using var provider = BuildProvider(config, ProviderOptions());
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        await repo.InitializeAsync();
        var saved = await repo.SaveAsync(new ProviderRecord { Id = "alias", Name = "committed", Score = 1 });
        if (mode == StorageObjectCacheMode.MetadataOnly)
        {
            saved.Envelope!.CacheObjectInstance = true;
            saved = await repo.SaveAsync(saved);
        }
        var source = (ICachedEnvelopeSource<IProviderRecord>)provider.GetRequiredService<IEnvelopeRepository<IProviderRecord>>();
        Assert.IsTrue(source.EnumerateCachedObjects().Any(item => ReferenceEquals(item.Value, saved)));
        saved.Name = "UNSAVED EDIT"; saved.Score = 999;
        var reread = await repo.QueryFirstByStorageKeyAsync("alias");
        var currentMatches = await ToListAsync(repo.SearchManyAsync(r => r.Score == 999));
        Assert.HasCount(1, currentMatches);
        Assert.AreSame(reread, currentMatches[0]);
        Assert.IsEmpty(await ToListAsync(repo.SearchManyAsync(r => r.Score == 1)));
        var byIndex = (await ToListAsync(repo.SearchManyAsync(r => r.Score == 1))).Select(r => r.Name + "/" + r.Score);
        Report("strong-cache-aliasing", new[]
        {
            "after mutating the returned object WITHOUT saving:",
            "  QueryFirstByStorageKey -> Name=" + reread!.Name + ", Score=" + reread.Score + ", same instance=" + ReferenceEquals(saved, reread),
            "  SearchMany(Score==1)   -> " + string.Join(",", byIndex) + "  (index still says 1, object says 999)",
        });
    }



    [TestMethod]
    public async Task ReviewRegression_IndexSubscriptionSilentlyDropped()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new RatioConfig(root.Path);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IRatioRecord>(config, ProviderOptions(), config.StoragePolicy);
        await using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IRepository<IRatioRecord>>();
        await repo.InitializeAsync();
        var lines = new List<string>();
        await repo.SaveAsync(new RatioRecord { Name = "before", Divisor = 4 });   // Ratio 25
        lines.Add("before failure: search Ratio==25 -> " + (await ToListAsync(repo.SearchManyAsync(r => r.Ratio == 25))).Count);
        try { await repo.SaveAsync(new RatioRecord { Name = "bad", Divisor = 0 }); lines.Add("save with throwing indexed getter: returned normally (no error surfaced)"); }
        catch (Exception ex) { lines.Add("save with throwing indexed getter threw " + ex.GetType().Name); }
        await repo.SaveAsync(new RatioRecord { Name = "after", Divisor = 5 });    // Ratio 20
        var scan = (await ToListAsync(repo.QueryManyAsync(r => r.Divisor != 0 && r.Ratio == 20))).Count;
        var search = (await ToListAsync(repo.SearchManyAsync(r => r.Ratio == 20))).Count;
        lines.Add($"after failure: scan finds Ratio==20 -> {scan}; indexed search finds -> {search}");
        Report("index-subscription-dropped", lines);
    }
}

[Idb.Libraries.Abstractions.Attributes.StorageScope(VersionTag = "1.0.0")]
public interface IRatioRecord : IStoredContract<IRatioRecord>
{
    string Name { get; set; }
    int Divisor { get; set; }
    [Idb.Libraries.Abstractions.Attributes.SearchIndex]
    int Ratio { get; }
}

[MessagePack.MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class RatioRecord : IRatioRecord
{
    public string Name { get; set; } = "";
    public int Divisor { get; set; }
    [MessagePack.IgnoreMember] public int Ratio => 100 / Divisor; // derived/computed indexed member
    [MessagePack.IgnoreMember] public IObjectEnvelope<IRatioRecord>? Envelope { get; private set; }
}
