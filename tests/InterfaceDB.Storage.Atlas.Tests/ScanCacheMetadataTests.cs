using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.AtlasCore.Tests;

public sealed partial class AtlasCoreProviderTests
{
    private static IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<IAtlasProviderRecord>> ScanCache(
        ServiceProvider provider, ProviderConfig config) => provider.GetRequiredKeyedService<
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<IAtlasProviderRecord>>>(config.EnvelopeDICacheKey);

    private static async Task<List<IObjectEnvelope<IAtlasProviderRecord>>> ScanRead(
        IEnvelopeRepository<IAtlasProviderRecord> repository, string key, string kind)
    {
        if (kind == "point") return [(await repository.GetPhysicalAsync(key))!];
        if (kind == "batch") return [.. await ((IBatchedEnvelopeRepository<IAtlasProviderRecord>)repository).GetPhysicalManyAsync([key])];
        var result = new List<IObjectEnvelope<IAtlasProviderRecord>>();
        await foreach (var envelope in repository.EnumerateAsync()) result.Add(envelope);
        return result;
    }

    [TestMethod]
    public async Task ScanCacheMetadata_ColdSkeletonHydratesOnceAndWarmReadsAvoidEnvelopeFactory()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        string key;
        await using (var writer = BuildProvider(config, ProviderOptions()))
        {
            var saved = await writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>().WriteAsync(
                new AtlasProviderRecord { Id = "scan-cache", Name = "original", Score = 7 });
            key = saved.Envelope!.EnvelopeCacheKey;
        }
        var services = new ServiceCollection();
        int created = 0;
        services.AddKeyedTransient<IObjectEnvelope<IAtlasProviderRecord>>(config.EnvelopeDICacheKey, (_, _) =>
        {
            Interlocked.Increment(ref created);
            return new FileObjectEnvelope<IAtlasProviderRecord> { EnvelopeCacheKey = "", VersionTag = "1.0.0" };
        });
        services.AddAtlasCoreRepository<IAtlasProviderRecord>(config, ProviderOptions(), config.StoragePolicy, new ProviderIdentityFactory());
        await using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        await ((IPersistentSearchIndexCatalogRepository)repository).InitializeForSearchIndexCatalogAsync();
        var cache = ScanCache(provider, config);
        var skeleton = cache[key];
        Assert.IsNull(skeleton.ImplementationTypeName);
        created = 0;
        var first = (await ScanRead(repository, key, "scan")).Single();
        Assert.IsTrue(created > 0, "A skeleton must receive complete metadata.");
        var hydratedMetadata = cache[key];
        Assert.AreNotSame(skeleton, hydratedMetadata);
        created = 0;
        foreach (var kind in new[] { "scan", "scan", "point", "batch" })
        {
            var read = (await ScanRead(repository, key, kind)).Single();
            Assert.AreSame(hydratedMetadata, cache[key]);
            Assert.AreNotSame(first, read);
            Assert.AreNotSame(first.ObjectInstance, read.ObjectInstance);
            Assert.AreSame(read, read.ObjectInstance!.Envelope);
            Assert.AreEqual(7, read.ObjectInstance.Score);
        }
        Assert.AreEqual(0, created, "The metadata fast path must skip envelope construction, not just publication.");
        Assert.IsNull(cache[key].ObjectInstance);
        Assert.IsNull(cache[key].ObjectInstanceWeakReference);
        Assert.IsNull(await repository.GetPhysicalAsync("missing-key"));
        Assert.AreEqual(0, (await ((IBatchedEnvelopeRepository<IAtlasProviderRecord>)repository).GetPhysicalManyAsync(["missing-key"])).Count);
    }

    [TestMethod]
    public async Task ScanCacheMetadata_SameRevisionWithDifferentMetadataMustRefreshAndCopyBytes()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        await using var provider = BuildProvider(config, ProviderOptions());
        var metadata = provider.GetRequiredService<IEnvelopeMetadataRepository<IAtlasProviderRecord>>();
        var saved = await metadata.SaveWithExtensionMetadataAsync(new AtlasProviderRecord { Id = "metadata", Score = 3 }, null, [4, 5]);
        var key = saved.Envelope!.EnvelopeCacheKey;
        var repository = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var cache = ScanCache(provider, config);
        var staleMetadata = cache[key];
        // Model a stale metadata cache with the same revision; revision equality is insufficient.
        staleMetadata.ExtensionMetadata = [1, 2];
        var read = (await ScanRead(repository, key, "scan")).Single();
        Assert.AreNotSame(staleMetadata, cache[key]);
        Assert.AreEqual(staleMetadata.RevisionTimestampUtcMicroseconds, cache[key].RevisionTimestampUtcMicroseconds);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, cache[key].ExtensionMetadata);
        Assert.AreNotSame(read.ExtensionMetadata, cache[key].ExtensionMetadata);
        read.ExtensionMetadata![0] = 99;
        var currentMetadata = cache[key];
        var repeated = (await ScanRead(repository, key, "scan")).Single();
        Assert.AreSame(currentMetadata, cache[key]);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, repeated.ExtensionMetadata);
        Assert.AreSame(repeated, repeated.ObjectInstance!.Envelope);
    }

    [TestMethod]
    [DataRow("scan", "save")]
    [DataRow("scan", "metadata")]
    [DataRow("scan", "delete")]
    [DataRow("point", "save")]
    [DataRow("point", "metadata")]
    [DataRow("point", "delete")]
    [DataRow("batch", "save")]
    [DataRow("batch", "metadata")]
    [DataRow("batch", "delete")]
    public async Task ScanCacheMetadata_LateReadCannotReplaceCommittedCache(string kind, string mutation)
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path);
        using var serializer = new ScanBlockingSerializer();
        await using var provider = BuildProvider(config, ProviderOptions(), serializer: serializer);
        var repository = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var metadata = provider.GetRequiredService<IEnvelopeMetadataRepository<IAtlasProviderRecord>>();
        await metadata.InitializeAsync();
        var saved = await metadata.SaveWithExtensionMetadataAsync(
            new AtlasProviderRecord { Id = "late-read", Name = "before", Score = 1 }, null, [1]);
        var key = saved.Envelope!.EnvelopeCacheKey;
        long revision = saved.Envelope.RevisionTimestampUtcMicroseconds;
        var cache = ScanCache(provider, config);
        serializer.Block = true;
        var pending = Task.Run(() => ScanRead(repository, key, kind));
        IObjectEnvelope<IAtlasProviderRecord>? committedCache = null;
        try
        {
            await serializer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (mutation == "delete") Assert.IsTrue(await metadata.DeleteAsync(saved));
            else if (mutation == "metadata")
            {
                var acknowledged = await metadata.SaveWithExtensionMetadataAsync(
                    new AtlasProviderRecord { Id = "late-read", Score = 999 }, key, [2],
                    new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
                Assert.AreEqual(revision, acknowledged.Envelope!.RevisionTimestampUtcMicroseconds);
                Assert.AreEqual(1, acknowledged.Score);
                committedCache = cache[key];
            }
            else
            {
                saved.Score = 9;
                saved = await metadata.SaveAsync(saved);
                Assert.IsTrue(saved.Envelope!.RevisionTimestampUtcMicroseconds > revision);
                committedCache = cache[key];
            }
        }
        finally { serializer.Release.Set(); }
        var late = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Single();
        if (mutation == "delete")
        {
            Assert.IsFalse(cache.ContainsKey(key), "A delayed read must not resurrect cache membership.");
            Assert.IsNull(await repository.GetPhysicalAsync(key));
            Assert.AreEqual(0, (await ScanRead(repository, key, "scan")).Count);
        }
        else
        {
            Assert.AreSame(committedCache, cache[key], "A delayed read must not replace the new cache entry.");
            var read = (await ScanRead(repository, key, "scan")).Single();
            Assert.AreSame(committedCache, cache[key]);
            Assert.AreEqual(mutation == "save" ? 9 : 1, read.ObjectInstance!.Score);
            CollectionAssert.AreEqual(new byte[] { mutation == "metadata" ? (byte)2 : (byte)1 }, read.ExtensionMetadata);
            var indexed = await metadata.SearchFirstAsync(x => x.Score == (mutation == "save" ? 9 : 1));
            Assert.IsNotNull(indexed);
        }
        if (mutation != "metadata")
            await Assert.ThrowsExactlyAsync<WriteConflictException<IAtlasProviderRecord>>(() => metadata.SaveAsync(late.ObjectInstance!).AsTask());
        else
            await Assert.ThrowsExactlyAsync<WriteConflictException<IAtlasProviderRecord>>(() => metadata.SaveWithExtensionMetadataAsync(
                new AtlasProviderRecord { Id = "late-read" }, key, [3],
                new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1])).AsTask());
    }

    [TestMethod]
    [DataRow(StorageObjectCacheMode.WeakObject, false, false)]
    [DataRow(StorageObjectCacheMode.StrongObject, false, false)]
    [DataRow(StorageObjectCacheMode.MetadataOnly, true, false)]
    [DataRow(StorageObjectCacheMode.MetadataOnly, false, true)]
    public async Task ScanCacheMetadata_RetentionAndOverridesKeepCommittedCopies(StorageObjectCacheMode mode, bool perObject, bool attribute)
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with { ObjectCacheMode = mode, HydrateOnInitialize = false });
        string key;
        await using (var writer = BuildProvider(config, ProviderOptions()))
        {
            var repository = writer.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
            IAtlasProviderRecord record = attribute ? new ScanAttributedRecord { Id = "retention", Score = 3 } : new AtlasProviderRecord { Id = "retention", Score = 3 };
            var saved = await repository.WriteAsync(record);
            if (perObject) { saved.Envelope!.CacheObjectInstance = true; saved = await repository.WriteAsync(saved); }
            key = saved.Envelope!.EnvelopeCacheKey;
        }
        await using var reader = BuildProvider(config, ProviderOptions());
        var envelopes = reader.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        await ((IPersistentSearchIndexCatalogRepository)envelopes).InitializeForSearchIndexCatalogAsync();
        var first = (await ScanRead(envelopes, key, "scan")).Single().ObjectInstance!;
        var second = (await ScanRead(envelopes, key, "scan")).Single().ObjectInstance!;
        Assert.AreNotSame(first, second);
        first.Score = 99;
        Assert.AreEqual(3, second.Score);
        Assert.AreEqual(3, (await ScanRead(envelopes, key, "point")).Single().ObjectInstance!.Score);
        var cached = ScanCache(reader, config)[key];
        if (mode == StorageObjectCacheMode.WeakObject) { Assert.IsNull(cached.ObjectInstance); Assert.IsNotNull(cached.ObjectInstanceWeakReference); }
        else { Assert.IsNotNull(cached.ObjectInstance); Assert.AreNotSame(first, cached.ObjectInstance); Assert.AreEqual(3, cached.ObjectInstance.Score); }
        GC.KeepAlive(first);
    }

    private sealed class ScanBlockingSerializer : IRevisionObjectSerializer<IAtlasProviderRecord>, IDisposable
    {
        readonly BinaryObjectSerializer<IAtlasProviderRecord> inner = new();
        public volatile bool Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public byte[] Serialize(IObjectEnvelope<IAtlasProviderRecord> envelope) => inner.Serialize(envelope);
        public IObjectEnvelope<IAtlasProviderRecord> Deserialize(byte[] bytes)
        {
            if (Block) { Block = false; Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return inner.Deserialize(bytes);
        }
        public long ReadRevision(ReadOnlyMemory<byte> payload) => inner.ReadRevision(payload);
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => inner.ReadRevisionAsync(source, cancellationToken);
        public ValueTask SerializeAsync(IObjectEnvelope<IAtlasProviderRecord> envelope, Stream destination, CancellationToken cancellationToken = default) => inner.SerializeAsync(envelope, destination, cancellationToken);
        public ValueTask<IObjectEnvelope<IAtlasProviderRecord>> DeserializeAsync(Stream source, CancellationToken cancellationToken = default) => inner.DeserializeAsync(source, cancellationToken);
        public void Dispose() => Release.Dispose();
    }
}

[StorageCached]
[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class ScanAttributedRecord : IAtlasProviderRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Score { get; set; }
    [IgnoreMember] public IObjectEnvelope<IAtlasProviderRecord>? Envelope { get; private set; }
}
