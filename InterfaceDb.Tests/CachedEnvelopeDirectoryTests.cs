using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Cache;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class CachedEnvelopeDirectoryTests
{
    private sealed class Row : IStoredContract<Row>
    {
        public IObjectEnvelope<Row>? Envelope { get; set; }
    }

    [TestMethod]
    public void ReplacementDeletionAndLateHydrationDoNotResurrectCachedEntries()
    {
        var cache = new ObjectEnvelopeDictionary<Row>();
        var old = new FileObjectEnvelope<Row> { VersionTag = "1.0.0", EnvelopeCacheKey = "one", ObjectInstance = new Row() };
        cache.AddOrUpdate("one", "group", old);
        Assert.HasCount(1, cache.EnumerateCachedObjects().ToArray());
        var replacement = new FileObjectEnvelope<Row> { VersionTag = "1.0.0", EnvelopeCacheKey = "one" };
        cache.AddOrUpdate("one", "group", replacement);
        cache.RefreshCachedObject(old);
        Assert.IsEmpty(cache.EnumerateCachedObjects());
        replacement.ObjectInstance = new Row();
        cache.RefreshCachedObject(replacement);
        Assert.AreSame(replacement.ObjectInstance, cache.EnumerateCachedObjects().Single().Value);
        cache.Remove("one");
        cache.RefreshCachedObject(replacement);
        Assert.IsEmpty(cache.EnumerateCachedObjects());
        Assert.AreEqual(0, cache.GetGroupCount("group"));
    }

    [TestMethod]
    public void WeakEntriesCanBeCollectedAndRehydratedWhileMetadataStaysAvailable()
    {
        var cache = new ObjectEnvelopeDictionary<Row>();
        for (var i = 0; i < 10_000; i++)
            cache.AddOrUpdate(i.ToString(), "metadata", new FileObjectEnvelope<Row> { VersionTag = "1.0.0", EnvelopeCacheKey = i.ToString() });
        var envelope = AddWeak(cache);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsEmpty(cache.EnumerateCachedObjects());
        Assert.AreEqual(10_001, cache.Count);
        var live = new Row();
        envelope.ObjectInstanceWeakReference = new(live);
        cache.RefreshCachedObject(envelope);
        Assert.AreSame(live, cache.EnumerateCachedObjects().Single().Value);
        GC.KeepAlive(live);
        cache.Clear();
        Assert.IsEmpty(cache.EnumerateCachedObjects());
        Assert.AreEqual(0, cache.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IObjectEnvelope<Row> AddWeak(ObjectEnvelopeDictionary<Row> cache)
    {
        var envelope = new FileObjectEnvelope<Row> { VersionTag = "1.0.0", EnvelopeCacheKey = "weak", ObjectInstanceWeakReference = new(new Row()) };
        cache.AddOrUpdate("weak", "objects", envelope);
        return envelope;
    }
}
