using System.Buffers.Binary;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class IndependentCompactionTests
{
    [TestMethod]
    public async Task EstimateIsReadOnlyIncludesActiveObsoleteDataAndExcludesProtectedBaselineBytes()
    {
        using var root = new Temp();
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, Options(retention: true));
        await store.SaveAsync("one", new byte[4096]);
        using var original = await store.AcquireRetainedAsync("one");
        await store.SaveAsync("one", new byte[6144]);
        var paths = Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var generation = store.Statistics.CheckpointGeneration;
        var protectedEstimate = await store.GetCompactionEstimateAsync(CompactOptions());
        original.Dispose();
        var releasedEstimate = await store.GetCompactionEstimateAsync(CompactOptions());
        Assert.IsTrue(releasedEstimate.ReclaimableBytes >= protectedEstimate.ReclaimableBytes + 4096);
        Assert.AreEqual(generation, store.Statistics.CheckpointGeneration);
        CollectionAssert.AreEqual(paths, Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(System.IO.Path.Combine(root.Path, "storage-format.manifest")).AsSpan(20)));
    }

    [TestMethod]
    public async Task CorruptFrameIsRejectedBeforeIndependentFilePublication()
    {
        using var root = new Temp();
        await using (var writer = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        { await writer.SaveAsync("one", new byte[4096]); await writer.CheckpointAsync(); }
        var source = Directory.EnumerateFiles(root.Path, "*.idbseg", SearchOption.AllDirectories)
            .OrderByDescending(p => new FileInfo(p).Length).First();
        using (var handle = File.OpenHandle(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var position = RandomAccess.GetLength(handle) - 1;
            var value = new byte[1]; RandomAccess.Read(handle, value, position); value[0] ^= 0x80;
            RandomAccess.Write(handle, value, position); RandomAccess.FlushToDisk(handle);
        }
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, Options());
        await Assert.ThrowsAsync<ObjectLogCorruptionException>(() => store.CompactAsync(CompactOptions()).AsTask());
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(Directory.EnumerateFiles(root.Path, "*.idbseg", SearchOption.AllDirectories).Any(p => p.Contains("compacted")));
        Assert.IsFalse(Directory.EnumerateFiles(root.Path, "*.creating-*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task FailedInitialCheckpointPreventsFirstIndependentOutputAndLeavesNormalRecovery()
    {
        using var root = new Temp(); using var fs = new ControlledFileSystem();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options(fs)))
        {
            await store.SaveAsync("one", new byte[] { 5 }); fs.FailCheckpoints = true;
            await Assert.ThrowsAsync<IOException>(() => store.CompactAsync(CompactOptions()).AsTask());
            Assert.IsFalse(Directory.EnumerateFiles(root.Path, "*.idbseg", SearchOption.AllDirectories).Any(p => p.Contains("compacted")));
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)5, (await reopened.GetAsync("one"))![0]);
    }

    private static SegmentCoreOptions Options(IDurableFileSystem? fs = null, bool retention = false) => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        DurableFileSystem = fs ?? PlatformDurableFileSystem.Instance,
        RetainedVersions = retention ? new RetainedVersionOptions() : null,
        LogOptions = new() { StripeCount = 2, MaxSegmentBytes = 16 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush }
    };

    private static SegmentCoreCompactionOptions CompactOptions(IMaintenanceBudget? budget = null) => new()
    {
        Mode = SegmentCoreCompactionMode.IndependentOutput, MaximumCopyConcurrency = 2,
        MaintenanceBudget = budget, MinimumFreeSpaceReserveBytes = 0,
        Selection = new() { MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0,
            MaximumCopyBytes = 32 * 1024 * 1024, MaximumCandidates = 128 }
    };

    [TestMethod]
    public async Task IndependentOutputPreservesCurrentBytesAndReopensFromPagedCheckpoint()
    {
        using var root = new Temp();
        var options = Options() with { PrimaryIndexOptions = new() { Mode = SegmentCorePrimaryIndexMode.Paged } };
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            for (var i = 0; i < 20; i++) await store.SaveAsync("key-" + i, Enumerable.Repeat((byte)i, 4096).ToArray());
            for (var i = 0; i < 20; i++) await store.SaveAsync("key-" + i, Enumerable.Repeat((byte)(i + 1), 6144).ToArray());
            var result = await store.CompactAsync(CompactOptions());
            Assert.IsTrue(result.FilesSelected > 0); Assert.IsTrue(result.FramesCopied > 0);
            Assert.AreEqual(TimeSpan.Zero, result.LongestCopySlice);
            Assert.IsTrue(result.RetiredFileBytes > result.BytesCopied);
            Assert.IsTrue(Directory.EnumerateFiles(root.Path, "segment-*.idbseg", SearchOption.AllDirectories)
                .Any(p => p.Contains("compacted")));
            Assert.IsTrue((await store.VerifyIntegrityAsync()).IsValid);
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual(20, reopened.Count);
        for (var i = 0; i < 20; i++)
            CollectionAssert.AreEqual(Enumerable.Repeat((byte)(i + 1), 6144).ToArray(), (await reopened.GetAsync("key-" + i))!);
        await reopened.SaveAsync("key-0", new byte[] { 90 });
        await reopened.CheckpointAsync();
        Assert.AreEqual((byte)90, (await reopened.GetAsync("key-0"))![0]);
    }

    [TestMethod]
    public async Task ForegroundWriteDeleteAndRecreateProceedWhileOutputIsBeingCopied()
    {
        using var root = new Temp(); using var fs = new ControlledFileSystem();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options(fs)))
        {
            await store.SaveAsync("updated", new byte[] { 1 });
            await store.SaveAsync("deleted", new byte[] { 2 });
            await store.SaveAsync("recreated", new byte[] { 3 });
            fs.BlockOutput = true;
            var compact = store.CompactAsync(CompactOptions()).AsTask();
            try
            {
                await fs.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await store.SaveAsync("updated", new byte[] { 11 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsTrue(await store.DeleteAsync("deleted").AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                await store.DeleteAsync("recreated"); await store.SaveAsync("recreated", new byte[] { 33 });
                await store.SaveAsync("new-key", new byte[] { 44 });
            }
            finally { fs.Release.Set(); }
            await compact.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual((byte)11, (await store.GetAsync("updated"))![0]);
            Assert.IsNull(await store.GetAsync("deleted"));
            Assert.AreEqual((byte)33, (await store.GetAsync("recreated"))![0]);
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)11, (await reopened.GetAsync("updated"))![0]);
        Assert.IsNull(await reopened.GetAsync("deleted"));
        Assert.AreEqual((byte)33, (await reopened.GetAsync("recreated"))![0]);
        Assert.AreEqual((byte)44, (await reopened.GetAsync("new-key"))![0]);
    }

    [TestMethod]
    public async Task TransactionCommitCanProceedDuringIndependentCopy()
    {
        using var root = new Temp(); using var fs = new ControlledFileSystem();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options(fs)))
        {
            await store.SaveAsync("one", new byte[] { 1 }); await store.SaveAsync("two", new byte[] { 2 });
            fs.BlockOutput = true; var compact = store.CompactAsync(CompactOptions()).AsTask();
            try
            {
                await fs.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync("one", new byte[] { 10 });
                await transaction.SaveAsync("two", new byte[] { 20 });
                await transaction.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { fs.Release.Set(); }
            await compact.WaitAsync(TimeSpan.FromSeconds(15));
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)10, (await reopened.GetAsync("one"))![0]);
        Assert.AreEqual((byte)20, (await reopened.GetAsync("two"))![0]);
    }

    [TestMethod]
    public async Task RetainedOriginalsSurviveIndependentCopyAndAllIoChargesStayBounded()
    {
        using var root = new Temp();
        var budget = new TrackingBudget(512);
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, Options(retention: true));
        await store.SaveAsync("one", Enumerable.Repeat((byte)1, 8192).ToArray());
        using var original = await store.AcquireRetainedAsync("one");
        await store.SaveAsync("one", Enumerable.Repeat((byte)2, 12288).ToArray());
        using var second = await store.AcquireRetainedAsync("one");
        await store.SaveAsync("one", Enumerable.Repeat((byte)3, 16384).ToArray());
        var result = await store.CompactAsync(CompactOptions(budget));
        Assert.AreEqual((byte)1, (await original.ReadAsync())[0]);
        Assert.AreEqual((byte)2, (await second.ReadAsync())[0]);
        Assert.AreEqual((byte)3, (await store.GetAsync("one"))![0]);
        Assert.IsTrue(result.RetainedBytesCopied >= 8192 + 12288);
        Assert.IsTrue(budget.MaximumObserved <= 512);
        Assert.IsTrue(budget.Statistics.AdmittedIoBytes > 2 * result.BytesCopied);
        Assert.AreEqual(0, budget.Statistics.ActiveUnits);
        original.Dispose(); second.Dispose();
        Assert.AreEqual(0L, store.RetentionStatistics.ArchivedBytes);
        await store.CompactAsync(CompactOptions(budget));
        Assert.AreEqual((byte)3, (await store.GetAsync("one"))![0]);
    }

    [TestMethod]
    public async Task BackupAndRestoreIncludeIndependentFilesAndRecoveryFloor()
    {
        using var root = new Temp(); using var backup = new Temp(create: false); using var restored = new Temp(create: false);
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        {
            await store.SaveAsync("one", new byte[] { 7, 8, 9 });
            await store.CompactAsync(CompactOptions());
            await store.BackupAsync(backup.Path);
        }
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(backup.Path, "compaction-checkpoint-floor.manifest")));
        await SegmentCoreStore.RestoreBackupAsync(backup.Path, restored.Path, Options());
        await using var reopened = await SegmentCoreStore.OpenAsync(restored.Path, Options());
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, (await reopened.GetAsync("one"))!);
    }

    [TestMethod]
    public async Task LosingAllCheckpointsFailsClosedInsteadOfRecoveringOnlyNormalLogs()
    {
        using var root = new Temp();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        {
            await store.SaveAsync("one", new byte[] { 10 }); await store.CompactAsync(CompactOptions());
        }
        foreach (var path in Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, "checkpoints"))) File.Delete(path);
        await Assert.ThrowsExactlyAsync<ObjectLogCorruptionException>(() => SegmentCoreStore.OpenAsync(root.Path, Options()).AsTask());
    }

    [TestMethod]
    public async Task NewestCheckpointCorruptionCanUseTheSecondSafeGeneration()
    {
        using var root = new Temp();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        {
            await store.SaveAsync("one", new byte[] { 5 }); await store.CompactAsync(CompactOptions());
            await store.SaveAsync("two", new byte[] { 6 });
        }
        var newest = Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, "checkpoints"), "*.idbidx")
            .OrderByDescending(p => p, StringComparer.Ordinal).First();
        File.WriteAllBytes(newest, new byte[] { 0 });
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)5, (await reopened.GetAsync("one"))![0]);
        Assert.AreEqual((byte)6, (await reopened.GetAsync("two"))![0]);
    }

    [TestMethod]
    public async Task FailedCheckpointDoesNotPromoteAStaleIndependentCopyDuringRecovery()
    {
        using var root = new Temp(); using var fs = new ControlledFileSystem();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options(fs)))
        {
            await store.SaveAsync("one", new byte[] { 1 }); await store.CheckpointAsync();
            fs.BlockOutput = true; fs.FailCheckpoints = true;
            var compact = store.CompactAsync(CompactOptions()).AsTask();
            try
            {
                await fs.OutputReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await store.SaveAsync("one", new byte[] { 9 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { fs.Release.Set(); }
            await Assert.ThrowsExactlyAsync<IOException>(() => compact);
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)9, (await reopened.GetAsync("one"))![0]);
        await reopened.CompactAsync(CompactOptions());
        Assert.AreEqual((byte)9, (await reopened.GetAsync("one"))![0]);
    }

    [TestMethod]
    public async Task CancellationBeforeOutputPublicationLeavesSourceRecoverable()
    {
        using var root = new Temp();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        {
            await store.SaveAsync("one", new byte[] { 3 }); await store.CheckpointAsync();
            using var stop = new CancellationTokenSource();
            var budget = new CancelBudget(stop);
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.CompactAsync(CompactOptions(budget), stop.Token).AsTask());
            Assert.AreEqual((byte)3, (await store.GetAsync("one"))![0]);
            await store.CompactAsync(CompactOptions());
        }
        await using var reopened = await SegmentCoreStore.OpenAsync(root.Path, Options());
        Assert.AreEqual((byte)3, (await reopened.GetAsync("one"))![0]);
    }

    [TestMethod]
    public async Task LayoutUpgradeIsOptInAndAdvertisesRequiredMinor()
    {
        using var root = new Temp();
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, Options());
        var manifest = System.IO.Path.Combine(root.Path, "storage-format.manifest");
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(manifest).AsSpan(20)));
        await store.SaveAsync("one", new byte[] { 3 });
        await store.CompactAsync(CompactOptions());
        Assert.AreEqual(1, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(manifest).AsSpan(20)));
    }

    [TestMethod]
    public async Task SelectionCopyLimitDoesNotSilentlyExceedBudget()
    {
        using var root = new Temp();
        await using var store = await SegmentCoreStore.OpenAsync(root.Path, Options());
        await store.SaveAsync("one", new byte[1024]);
        var result = await store.CompactAsync(CompactOptions() with
        { Selection = new() { MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0, MaximumCopyBytes = 1 } });
        Assert.AreEqual(0L, result.BytesCopied);
        Assert.AreEqual(1024, (await store.GetAsync("one"))!.Length);
    }

    private sealed class TrackingBudget(long maximum) : IMaintenanceBudget
    {
        private readonly MaintenanceBudget _inner = new(new() { MaximumWorkUnitBytes = maximum, MaximumConcurrentUnits = 2 });
        private long _maximum;
        public long MaximumObserved => Interlocked.Read(ref _maximum);
        public long MaximumWorkUnitBytes => maximum;
        public MaintenanceBudgetStatistics Statistics => _inner.Statistics;
        public ValueTask<IDisposable> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
        { Interlocked.Exchange(ref _maximum, Math.Max(MaximumObserved, bytes)); return _inner.AcquireAsync(bytes, cancellationToken); }
    }

    private sealed class CancelBudget(CancellationTokenSource stop) : IMaintenanceBudget
    {
        public long MaximumWorkUnitBytes => 512;
        public MaintenanceBudgetStatistics Statistics => default;
        public ValueTask<IDisposable> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
        { stop.Cancel(); cancellationToken.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }

    private sealed class ControlledFileSystem : IDurableFileSystem, IDisposable
    {
        private readonly IDurableFileSystem _inner = PlatformDurableFileSystem.Instance;
        public readonly TaskCompletionSource OutputReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public volatile bool BlockOutput, FailCheckpoints;
        public string DirectoryFlushStrategy => _inner.DirectoryFlushStrategy;
        public void FlushFile(SafeFileHandle handle) => _inner.FlushFile(handle);
        public void FlushDirectory(string path) => _inner.FlushDirectory(path);
        public void ReplaceFile(string temporary, string final) => _inner.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final)
        {
            if (FailCheckpoints && final.Contains("checkpoints") && final.EndsWith(".idbidx"))
                throw new IOException("Injected checkpoint publication failure.");
            if (BlockOutput && final.Contains("compacted") && final.EndsWith(".idbseg"))
            { OutputReached.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test copy release was not signalled."); }
            _inner.PublishFile(temporary, final);
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }

    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idb-independent-" + Guid.NewGuid().ToString("N"));
        public Temp(bool create = true) { if (create) Directory.CreateDirectory(Path); }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
