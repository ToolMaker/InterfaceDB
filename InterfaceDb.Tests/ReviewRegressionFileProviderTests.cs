using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

// Regressions for logical key identity and ordinal allocation.
[TestClass]
public sealed class ReviewRegressionFileProviderTests
{
    [TestMethod]
    [DataRow(StorageObjectCacheMode.StrongObject)]
    [DataRow(StorageObjectCacheMode.WeakObject)]
    public async Task ReopenedFileCacheKeepsUnsavedEditsPrivate(StorageObjectCacheMode mode)
    {
        var initial = Build(p => p.Id, mode: mode);
        try
        {
            await using (initial.Provider)
            {
                await initial.Repo.InitializeAsync();
                await initial.Repo.SaveAsync(new FileProbe { Id = "cached", Score = 1 });
            }
            var reopened = Build(p => p.Id, existingRoot: initial.Root, mode: mode);
            await using (reopened.Provider)
            {
                await reopened.Repo.InitializeAsync();
                // Read through the envelope API to verify the provider tracks hydration,
                // independently of the index manager's ordinary object-return path.
                var envelopes = reopened.Provider.GetRequiredService<IEnvelopeRepository<IFileProbe>>();
                IFileProbe? record = null;
                await foreach (var envelope in envelopes.GetAsync("cached"))
                    Idb.Libraries.Implementation.Serialization.StorageObjectCachePolicy.TryGetObjectInstance(envelope, out record);
                Assert.IsNotNull(record);
                record.Score = 99;
                var source = (ICachedEnvelopeSource<IFileProbe>)envelopes;
                Assert.IsTrue(source.IsolatesCachedReads);
                Assert.IsTrue(source.EnumerateCachedObjects().All(item => item.Value.Score == 1 && !ReferenceEquals(item.Value, record)));
                var matches = new List<IFileProbe>();
                await foreach (var row in reopened.Repo.SearchManyAsync(r => r.Score == 99)) matches.Add(row);
                Assert.IsEmpty(matches);
                Assert.IsNotNull(await reopened.Repo.SearchFirstAsync(r => r.Score == 1));
                await reopened.Repo.SaveAsync(record);
                Assert.IsNotNull(await reopened.Repo.SearchFirstAsync(r => r.Score == 99));
                Assert.IsNull(await reopened.Repo.SearchFirstAsync(r => r.Score == 1));
            }
        }
        finally { Directory.Delete(initial.Root, recursive: true); }
    }

    [StorageScope(VersionTag = "1.0.0")]
    [SearchIndex]
    public interface IFileProbe : IStoredContract<IFileProbe>
    {
        string Id { get; set; }
        string Name { get; set; }
        int Score { get; set; }
    }

    [MessagePackObject(true)]
    public sealed class FileProbe : IFileProbe
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Score { get; set; }
        [IgnoreMember] public IObjectEnvelope<IFileProbe>? Envelope { get; set; }
    }

    private sealed class Config(string root, StorageObjectCacheMode mode = StorageObjectCacheMode.MetadataOnly) : IDbContractConfigDto<IFileProbe>
    {
        public string EnvelopeDICacheKey => "file-probe";
        public string ContractName => "FileProbe";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
        {
            ObjectCacheMode = mode,
            WatchStorageChanges = false,
            HydrateOnInitialize = false,
            RaiseNotifications = true
        };
    }

    private sealed class KeyFromId(Func<IFileProbe, string> select) : IEnvelopeKeyFactory<IFileProbe, string>
    {
        public string CreateKey(IFileProbe instance) => select(instance);
    }

    private static (ServiceProvider Provider, IRepository<IFileProbe> Repo, string Root) Build(Func<IFileProbe, string> key, bool single = false, string? existingRoot = null, StorageObjectCacheMode mode = StorageObjectCacheMode.MetadataOnly)
    {
        var root = existingRoot ?? Path.Combine(Path.GetTempPath(), "idb-review-file-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new Config(root, mode);
        var services = new ServiceCollection();
        services.AddFileRepository(config,
            storageIdentityFactory: single ? new SingleIdentity(config) : new FlatFileStorageIdentityFactory<IFileProbe, string>(config, new KeyFromId(key)));
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<IRepository<IFileProbe>>(), root);
    }

    private static void Report(string name, IEnumerable<string> lines)
    {
        var observed = string.Join("\n", lines);
        Console.WriteLine(observed);
        if (name == "storage-key-digits") StringAssert.Contains(observed, "QueryFirstByStorageKey(order-6): six");
        else if (name == "shared-key-delete-insert") StringAssert.Contains(observed, "live objects: B,C");
        else Assert.Fail("Unasserted regression: " + name);
    }

    [TestMethod]
    public async Task ReviewRegression_File_StorageKeyEndingInDigits()
    {
        var (provider, repo, root) = Build(p => p.Id);
        await using (provider)
        {
            await repo.InitializeAsync();
            var five = await repo.SaveAsync(new FileProbe { Id = "order-5", Name = "five" });
            var six = await repo.SaveAsync(new FileProbe { Id = "order-6", Name = "six" });
            Report("storage-key-digits", new[]
            {
                "order-5 physical key: " + five.Envelope!.EnvelopeCacheKey + "  file: " + Path.GetFileName(five.Envelope.Location),
                "order-6 physical key: " + six.Envelope!.EnvelopeCacheKey + "  file: " + Path.GetFileName(six.Envelope.Location),
                "QueryFirstByStorageKey(order-6): " + ((await repo.QueryFirstByStorageKeyAsync("order-6"))?.Name ?? "<null>"),
            });
        }
        Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task ReviewRegression_File_SharedLogicalKeyDeleteThenInsert()
    {
        var (provider, repo, root) = Build(_ => "shared");
        var lines = new List<string>();
        await using (provider)
        {
            await repo.InitializeAsync();
            var a = await repo.SaveAsync(new FileProbe { Id = "a", Name = "A" });
            var b = await repo.SaveAsync(new FileProbe { Id = "b", Name = "B" });
            lines.Add($"A -> {a.Envelope!.EnvelopeCacheKey}, B -> {b.Envelope!.EnvelopeCacheKey}");
            lines.Add("delete A: " + await repo.DeleteAsync(a));
            try
            {
                var c = await repo.SaveAsync(new FileProbe { Id = "c", Name = "C" });
                lines.Add($"C -> {c.Envelope!.EnvelopeCacheKey}");
            }
            catch (Exception ex) { lines.Add("insert C FAILED: " + ex.GetType().Name + ": " + ex.Message); }
            var names = new List<string>();
            await foreach (var item in repo.QueryManyAsync(_ => true)) names.Add(item.Name);
            lines.Add("live objects: " + string.Join(",", names.OrderBy(n => n)));
        }
        Report("shared-key-delete-insert", lines);
        Directory.Delete(root, true);
    }



    [StorageScope(VersionTag = "1.0.0")]
    public interface IPlainProbe : IStoredContract<IPlainProbe> { string Id { get; set; } string Body { get; set; } }

    [MessagePackObject(true)]
    public sealed class PlainProbe : IPlainProbe
    {
        public string Id { get; set; } = "";
        public string Body { get; set; } = "";
        [IgnoreMember] public IObjectEnvelope<IPlainProbe>? Envelope { get; set; }
    }

    private sealed class PlainConfig(string root, bool hydrate) : IDbContractConfigDto<IPlainProbe>
    {
        public string EnvelopeDICacheKey => "plain-probe";
        public string ContractName => "PlainProbe";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.FileSystem;
        public StoragePolicy? StoragePolicy => Idb.Libraries.Abstractions.Options.StoragePolicy.Default with
        {
            WatchStorageChanges = false,
            HydrateOnInitialize = hydrate // library default is true; ObjectCacheMode default is MetadataOnly
        };
    }


    private sealed class SingleIdentity(Config config) : IEnvelopeStorageIdentityFactory<IFileProbe>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public void InitEnvelope(IObjectEnvelope<IFileProbe> envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)) envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id;
            envelope.Location ??= Path.Combine(config.DataRoot!, "1.0.0", envelope.EnvelopeCacheKey + ".mpk");
        }
    }

    [TestMethod]
    public async Task SingleFactoryPreservesDigitSuffixAcrossReopen()
    {
        var (provider, repo, root) = Build(p => p.Id, single: true);
        try
        {
            await using (provider)
            {
                await repo.InitializeAsync();
                await repo.SaveAsync(new FileProbe { Id = "invoice-2026", Name = "first" });
                await repo.SaveAsync(new FileProbe { Id = "invoice-2027", Name = "second" });
            }
            var reopened = Build(p => p.Id, single: true, existingRoot: root);
            await using (reopened.Provider)
            {
                await reopened.Repo.InitializeAsync();
                Assert.AreEqual("first", (await reopened.Repo.QueryFirstByStorageKeyAsync("invoice-2026"))!.Name);
                Assert.AreEqual("second", (await reopened.Repo.QueryFirstByStorageKeyAsync("invoice-2027"))!.Name);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LogicalKeyThatEqualsAnotherPhysicalKeyDoesNotChangeEntityExistence()
    {
        var (provider, repo, root) = Build(p => p.Id);
        try
        {
            await using (provider)
            {
                await repo.InitializeAsync();
                var first = await repo.SaveAsync(new FileProbe { Id = "order", Name = "first" });
                var second = await repo.SaveAsync(new FileProbe { Id = "order-0", Name = "second" });
                Assert.AreEqual("second", (await repo.QueryFirstByStorageKeyAsync("order-0"))!.Name);
                Assert.IsTrue(await repo.DeleteAsync(first));
                Assert.IsFalse(await repo.ExistsAsync(first));
                Assert.IsTrue(await repo.ExistsAsync(second));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
