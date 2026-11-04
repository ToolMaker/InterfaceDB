using System.Collections.Concurrent;
using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class ReadHandleFastPathTests
{
    [TestMethod]
    public async Task HintsDoNotResurrectEvictedHandlesAndDisposalDrainsActiveLeases()
    {
        var root = Directory.CreateTempSubdirectory("atlas-handle-test-").FullName;
        try
        {
            var one = Path.Combine(root, "one"); var two = Path.Combine(root, "two");
            File.WriteAllBytes(one, [1]); File.WriteAllBytes(two, [2]);
            await using var cache = new SegmentReadHandleCache(1);
            var original = cache.Acquire(one, FileOptions.RandomAccess);
            var staleHint = original.Entry;
            original.Dispose();
            var replacement = cache.Acquire(two, FileOptions.RandomAccess);
            Assert.IsFalse(cache.TryAcquire(staleHint, out _));
            Assert.IsTrue(staleHint.Handle.IsClosed);
            Assert.IsTrue(cache.TryAcquire(replacement.Entry, out var fast));
            var disposal = cache.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(cache.TryAcquire(replacement.Entry, out _));
            Assert.IsFalse(replacement.Handle.IsClosed);
            replacement.Dispose();
            var lastHandle = fast.Handle;
            fast.Dispose();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(lastHandle.IsClosed);
            Assert.AreEqual(0, cache.Statistics.CurrentOpenHandles);
            Assert.AreEqual(1, cache.Statistics.PeakOpenHandles);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ConcurrentHintsAndEvictionsKeepHardDescriptorBoundAndReadCorrectFiles()
    {
        var root = Directory.CreateTempSubdirectory("atlas-handle-stress-").FullName;
        try
        {
            var paths = Enumerable.Range(0, 5).Select(i => Path.Combine(root, $"{i}")).ToArray();
            for (var i = 0; i < paths.Length; i++) File.WriteAllBytes(paths[i], [(byte)i]);
            await using var cache = new SegmentReadHandleCache(2);
            var hints = new ConcurrentDictionary<int, SegmentReadHandleCache.CacheEntry>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
            {
                Span<byte> value = stackalloc byte[1];
                for (var n = 0; n < 500; n++)
                {
                    var id = (n + worker) % paths.Length;
                    SegmentReadHandleLease lease;
                    if (!hints.TryGetValue(id, out var hint) || !cache.TryAcquire(hint, out lease))
                        lease = cache.Acquire(paths[id], FileOptions.RandomAccess, deadline.Token);
                    using (lease)
                    {
                        hints[id] = lease.Entry;
                        Assert.AreEqual(1, RandomAccess.Read(lease.Handle, value, 0));
                        Assert.AreEqual((byte)id, value[0]);
                    }
                }
            }))).WaitAsync(TimeSpan.FromSeconds(40));
            Assert.IsTrue(cache.Statistics.PeakOpenHandles <= 2);
            Assert.IsTrue(cache.Statistics.Evictions > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FullCacheWaitCanBeCancelledWithoutBreakingLiveHint()
    {
        var root = Directory.CreateTempSubdirectory("atlas-handle-cancel-").FullName;
        try
        {
            var one = Path.Combine(root, "one"); var two = Path.Combine(root, "two");
            File.WriteAllBytes(one, [1]); File.WriteAllBytes(two, [2]);
            await using var cache = new SegmentReadHandleCache(1);
            using var live = cache.Acquire(one, FileOptions.RandomAccess);
            using var token = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Task.Run(() =>
            { using var unused = cache.Acquire(two, FileOptions.RandomAccess, token.Token); }));
            Assert.IsTrue(cache.TryAcquire(live.Entry, out var hint));
            hint.Dispose();
        }
        finally { Directory.Delete(root, true); }
    }
}
