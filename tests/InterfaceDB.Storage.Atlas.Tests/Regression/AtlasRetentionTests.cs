// Regression copied from experiments/Atlas.RetentionTests/AtlasRetentionTests.cs; only provider configuration is adapted for the promoted hybrid.
using AtlasQualification;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class AtlasRetentionTests
{
    [TestMethod]
    [DataRow(0)]

    public async Task DifferentBaselinesSurviveRelocationAndFailureWithoutBecomingCurrent(int variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var store = await AtlasCoreStore.OpenAsync(root, Setup.Options(variant)))
            {
                store.EnableRetainedVersions(new());
                await store.SaveAsync("one", new byte[8192]);
                using var first = await store.AcquireRetainedAsync("one");
                using var shared = await store.AcquireRetainedAsync("one");
                Assert.AreEqual(first.RecordId, shared.RecordId);
                await store.SaveAsync("one", Enumerable.Repeat((byte)1, 16384).ToArray());
                using var second = await store.AcquireRetainedAsync("one");
                for (var i = 2; i < 6; i++) await store.SaveAsync("one", Enumerable.Repeat((byte)i, i * 32768).ToArray());
                // Fail the history destination after the candidate current generation has been built.
                File.WriteAllText(Path.Combine(root, ".idb-history"), "blocked destination");
                await Assert.ThrowsAsync<IOException>(() => store.CompactAsync().AsTask());
                Assert.AreEqual(8192, (await first.ReadAsync()).Length);
                Assert.AreEqual((byte)5, (await store.GetAsync("one"))![0]);
                File.Delete(Path.Combine(root, ".idb-history"));
                await store.CompactAsync();
                Assert.AreEqual((byte)1, (await second.ReadAsync())[0]);
                Assert.AreEqual((byte)0, (await shared.ReadAsync())[0]);
                Assert.AreEqual(1, (await store.ReadAllAsync()).Count);
                Assert.IsTrue(store.RetentionStatistics.ArchivedBytes > 0);
                var backup = root + "-backup";
                try
                {
                    await store.BackupAsync(backup);
                    Assert.AreEqual(0, Directory.GetFiles(backup, "*.version", SearchOption.AllDirectories).Length);
                    Assert.AreEqual((byte)1, (await second.ReadAsync())[0]);
                }
                finally { if (Directory.Exists(backup)) Directory.Delete(backup, true); }
                first.Dispose(); second.Dispose();
                Assert.AreEqual(1, store.RetentionStatistics.RetainedRecords);
                shared.Dispose(); await store.CompactAsync();
                Assert.AreEqual(0L, store.RetentionStatistics.ArchivedBytes);
                Assert.AreEqual(0, Directory.GetFiles(root, "*.version", SearchOption.AllDirectories).Length);
            }
            await using var reopened = await AtlasCoreStore.OpenAsync(root, Setup.Options(variant));
            reopened.EnableRetainedVersions(new());
            Assert.AreEqual((byte)5, (await reopened.GetAsync("one"))![0]);
            Assert.AreEqual(1, (await reopened.ReadAllAsync()).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(0)]

    public async Task AcquisitionCompetesWithWritesAndCompactionAndRawDeletesInvalidateIt(int variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-retention-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = await AtlasCoreStore.OpenAsync(root, Setup.Options(variant));
            store.EnableRetainedVersions(new());
            await store.SaveAsync("one", new byte[] { 1 });
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Enumerable.Range(0, 16).Select(async _ =>
            {
                await start.Task;
                using var lease = await store.AcquireRetainedAsync("one");
                var original = await lease.ReadAsync();
                await store.CompactAsync();
                CollectionAssert.AreEqual(original, await lease.ReadAsync());
            }).ToArray();
            var writer = Task.Run(async () => { await start.Task; for (byte i = 2; i < 18; i++) await store.SaveAsync("one", new[] { i }); });
            start.SetResult(); await Task.WhenAll(readers.Append(writer));
            using var final = await store.AcquireRetainedAsync("one");
            await using (var transaction = await store.BeginTransactionAsync())
            { await transaction.DeleteAsync("one"); await transaction.CommitAsync(); }
            await store.SaveAsync("one", new byte[] { 99 });
            using var use = final.AcquireUse(); Assert.IsFalse(use.IsSameIncarnation);
            Assert.AreEqual((byte)17, (await use.ReadAsync())[0]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
