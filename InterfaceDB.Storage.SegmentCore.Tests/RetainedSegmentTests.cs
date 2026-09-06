using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class RetainedSegmentTests
{
    [TestMethod]
    public async Task MultipleOriginalsRelocateWithoutBecomingLatestAndReleaseReclaimsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new SegmentCoreOptions { TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
                RetainedVersions = new(), CheckpointGenerationsToKeep = 2 };
            await using (var store = await SegmentCoreStore.OpenAsync(root, options))
            {
                await store.SaveAsync("one", new byte[8192]);
                using var first = await store.AcquireRetainedAsync("one");
                using var shared = await store.AcquireRetainedAsync("one");
                Assert.AreEqual(first.RecordId, shared.RecordId);
                Assert.AreEqual(1, store.RetentionStatistics.RetainedRecords);
                await store.SaveAsync("one", Enumerable.Repeat((byte)1, 16384).ToArray());
                using var second = await store.AcquireRetainedAsync("one");
                for (var i = 2; i < 6; i++) await store.SaveAsync("one", Enumerable.Repeat((byte)i, i * 32768).ToArray());
                await store.CompactAsync();
                Assert.AreEqual(8192, (await first.ReadAsync()).Length);
                Assert.AreEqual((byte)1, (await second.ReadAsync())[0]);
                Assert.AreEqual((byte)5, (await store.GetAsync("one"))![0]);
                Assert.AreEqual(1, (await store.ReadAllAsync()).Count);
                Assert.AreEqual(2, store.RetentionStatistics.RetainedRecords);
                Assert.IsTrue(store.RetentionStatistics.ArchivedBytes > 0);
                first.Dispose(); Assert.AreEqual(8192, (await shared.ReadAsync()).Length);
                second.Dispose(); shared.Dispose(); await store.CompactAsync();
                Assert.AreEqual(0, store.RetentionStatistics.RetainedRecords);
                Assert.AreEqual(0L, store.RetentionStatistics.ArchivedBytes);
            }
            await using var reopened = await SegmentCoreStore.OpenAsync(root, options);
            Assert.AreEqual((byte)5, (await reopened.GetAsync("one"))![0]);
            Assert.AreEqual(1, (await reopened.ReadAllAsync()).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RawDeleteRecreateInvalidatesIncarnationAndDisabledAcquisitionFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var disabled = await SegmentCoreStore.OpenAsync(root))
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => disabled.AcquireRetainedAsync("one").AsTask());
            await using var store = await SegmentCoreStore.OpenAsync(root, new() { RetainedVersions = new() });
            await store.SaveAsync("one", new byte[] { 1 });
            using var original = await store.AcquireRetainedAsync("one");
            await store.DeleteAsync("one"); await store.SaveAsync("one", new byte[] { 2 });
            using var use = original.AcquireUse();
            Assert.IsFalse(use.IsSameIncarnation);
            Assert.AreEqual((byte)1, (await use.ReadAsync())[0]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
