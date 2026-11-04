using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Tests;

[TestClass]
public sealed class HybridStressTests
{
    [TestMethod]
    [DataRow(AtlasCoreTransactionProtocol.MutationWriteAheadLog)]
    [DataRow(AtlasCoreTransactionProtocol.ParticipantCommitJournal)]
    public async Task RandomizedMutationsMatchReferenceAcrossSplitsTransactionsCompactionAndReopen(AtlasCoreTransactionProtocol protocol)
    {
        using var root = new HybridPartitionTests.Temp(); var model = new Dictionary<string, byte[]>(); var random = new Random(321);
        var options = HybridPartitionTests.Options(true) with { TransactionProtocol = protocol,
            Partitioning = HybridPartitionTests.Options(true).Partitioning with { SplitAboveLiveRecords = 8 } };
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        {
            for (var i = 0; i < 240; i++)
            {
                var key = $"{new[] { "ABC", "XYZ", "MNO" }[random.Next(3)]}{random.Next(32):D2}";
                if (random.Next(5) == 0) { await store.DeleteAsync(key); model.Remove(key); }
                else
                {
                    var bytes = new byte[random.Next(1, 4096)]; random.NextBytes(bytes);
                    if (i % 9 == 0)
                    {
                        await using var transaction = await store.BeginTransactionAsync();
                        await transaction.SaveAsync(key, bytes);
                        if (i % 18 == 0) { await transaction.CommitAsync(); model[key] = bytes; }
                    }
                    else { await store.SaveAsync(key, bytes); model[key] = bytes; }
                }
                if (i % 40 == 0) { await store.FlushPartitionMaintenanceAsync(); await Compare(store, model); }
            }
            await store.FlushPartitionMaintenanceAsync(); await Compare(store, model);
            await store.CompactAsync(); await Compare(store, model); await store.CheckpointAsync();
        }
        // Capacity/concurrency are deployment budgets, not a new physical routing format.
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options with
        { MaximumConcurrentPartitionWrites = 2, Partitioning = options.Partitioning with { MaximumPartitions = 2048 } });
        await Compare(reopened, model); Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
    }

    private static async Task Compare(AtlasCoreStore store, Dictionary<string, byte[]> expected)
    {
        var rows = await store.ReadAllAsync(); Assert.AreEqual(expected.Count, rows.Count);
        foreach (var row in rows) CollectionAssert.AreEqual(expected[row.Key], row.Payload, row.Key);
    }

    [TestMethod]
    public async Task BlockedSingleWriterDoesNotHoldOtherBucketsBehindAStoreWideTransactionGate()
    {
        using var root = new HybridPartitionTests.Temp(); using var fs = new BlockingFlush();
        var options = HybridPartitionTests.Options() with { DurableFileSystem = fs, MaximumConcurrentPartitionWrites = 2 };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        // Register/activate both prefixes first: first-prefix metadata publication is deliberately serialized.
        await store.SaveAsync("AAA1", new byte[] { 1 }); await store.SaveAsync("BBB1", new byte[] { 1 });
        fs.Armed = 1;
        var first = store.SaveAsync("AAA1", new byte[] { 2 }).AsTask();
        try
        {
            await fs.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual((byte)1, (await store.GetAsync("AAA1"))![0], "Uncommitted replacement must not be visible.");
            await store.SaveAsync("BBB1", new byte[] { 3 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual((byte)3, (await store.GetAsync("BBB1"))![0]);
            Assert.IsFalse(first.IsCompleted);
            Assert.AreEqual(2, store.Statistics.PeakConcurrentPartitionWrites);
        }
        finally { fs.Release.Set(); await first; }
        Assert.AreEqual((byte)2, (await store.GetAsync("AAA1"))![0]);
        Assert.AreEqual(0L, store.Statistics.CommittedTransactions, "Single-record commits must not create coordinated transactions.");
    }

    private sealed class BlockingFlush : IDurableFileSystem, IDisposable
    {
        private readonly IDurableFileSystem _inner = PlatformDurableFileSystem.Instance;
        public int Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public string DirectoryFlushStrategy => _inner.DirectoryFlushStrategy;
        public void FlushFile(SafeFileHandle handle)
        {
            if (Interlocked.Exchange(ref Armed, 0) == 1)
            { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Blocked test flush timed out."); }
            _inner.FlushFile(handle);
        }
        public void FlushDirectory(string path) => _inner.FlushDirectory(path);
        public void ReplaceFile(string temporary, string final) => _inner.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final) => _inner.PublishFile(temporary, final);
        public void Dispose() => Release.Dispose();
    }
}
