// Regression copied from experiments/Atlas.Maintenance.Tests/ConcurrentCompactionTests.cs; only provider configuration is adapted for the promoted hybrid.
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Idb.Storage.AtlasCore;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.Maintenance.Tests;

[TestClass]
public sealed class ConcurrentCompactionTests
{
    [TestMethod]
    [DataRow(0)]

    public async Task ElapsedBudgetCancelsAnAdmissionWaitWithoutCancellingForegroundStorage(int variant)
    {
        using var root = new Temp();
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        await store.SaveAsync("one", new byte[] { 4 });
        var budget = new BlockingBudget();
        var result = await store.CompactConcurrentAsync(Compaction(budget) with { MaximumCopyDuration = TimeSpan.FromMilliseconds(250) })
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(result.Published); Assert.IsTrue(result.Work.Deferred); Assert.AreEqual("time-budget", result.DeferReason);
        Assert.AreEqual((byte)4, (await store.GetAsync("one"))![0]);
        Assert.IsTrue((await store.CompactConcurrentAsync(Compaction())).Published);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task BackupAfterConcurrentCompactionRestoresCurrentSet(int variant)
    {
        using var parent = new Temp(); var source = Path.Combine(parent.Path, "source");
        var backup = Path.Combine(parent.Path, "backup"); var restore = Path.Combine(parent.Path, "restore");
        var options = Options(variant);
        await using (var store = await AtlasCoreStore.OpenAsync(source, options))
        {
            await store.SaveAsync("one", new byte[] { 1 }); await store.SaveAsync("one", new byte[] { 2 });
            Assert.IsTrue((await store.CompactConcurrentAsync(Compaction())).Published);
            await store.BackupAsync(backup);
        }
        await AtlasCoreStore.RestoreAsync(backup, restore, options);
        await using var reopened = await AtlasCoreStore.OpenAsync(restore, options);
        Assert.AreEqual((byte)2, (await reopened.GetAsync("one"))![0]);
        Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task CatchUpBudgetDefersContentionWithoutLosingAcknowledgedWrites(int variant)
    {
        using var root = new Temp(); var version = 1;
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        await store.SaveAsync("one", new byte[] { 1 }); var generation = store.Statistics.GroupSetGeneration;
        var budget = new MutatingBudget(async () => await store.SaveAsync("one", new[] { (byte)Interlocked.Increment(ref version) }));
        var result = await store.CompactConcurrentAsync(Compaction(budget) with { MaximumCatchUpPasses = 2 });
        Assert.IsFalse(result.Published); Assert.IsTrue(result.Work.Deferred);
        Assert.AreEqual("foreground-contention", result.DeferReason); Assert.AreEqual(2, result.CatchUpPasses);
        Assert.AreEqual(generation, store.Statistics.GroupSetGeneration);
        Assert.AreEqual((byte)version, (await store.GetAsync("one"))![0]);
        Assert.IsTrue((await store.CompactConcurrentAsync(Compaction())).Published);
        Assert.AreEqual((byte)version, (await store.GetAsync("one"))![0]);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task CancellationDiscardsOnlyUnpublishedCandidateAndAllowsRetry(int variant)
    {
        using var root = new Temp();
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        await store.SaveAsync("one", new byte[] { 7 });
        using var stop = new CancellationTokenSource();
        var budget = new BlockingBudget(); var operation = store.CompactConcurrentAsync(Compaction(budget), stop.Token).AsTask();
        await budget.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        Assert.AreEqual((byte)7, (await store.GetAsync("one"))![0]);
        Assert.IsTrue((await store.CompactConcurrentAsync(Compaction())).Published);
    }

    private sealed class MutatingBudget(Func<ValueTask> mutation) : IMaintenanceBudget
    {
        public long MaximumWorkUnitBytes => 1024 * 1024;
        public MaintenanceBudgetStatistics Statistics => default;
        public async ValueTask<IDisposable> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
        { await mutation(); return new Lease(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private static AtlasCoreOptions Options(int variant, IDurableFileSystem? fs = null) => new()
    {
        MaximumConcurrentPartitionWrites = 4, MaximumSegmentBytes = 16 * 1024, DurabilityStrategy = AtlasCoreDurabilityStrategy.ExplicitFlush,
        DurableFileSystem = fs ?? PlatformDurableFileSystem.Instance,
    };
    private static AtlasConcurrentCompactionOptions Compaction(IMaintenanceBudget? budget = null) => new()
    { MaintenanceBudget = budget, MinimumFreeSpaceReserveBytes = 0, MaximumCopyConcurrency = 2 };

    [TestMethod]
    [DataRow(0)]

    public async Task IndependentCopyAllowsWritesDeletesRecreatesAndAtomicTransactions(int variant)
    {
        using var root = new Temp(); var options = Options(variant);
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, options))
        {
            foreach (var key in new[] { "aaa-keep", "bbb-update", "ccc-delete", "ddd-recreate" }) await store.SaveAsync(key, new byte[] { 1 });
            var budget = new BlockingBudget();
            var operation = store.CompactConcurrentAsync(Compaction(budget)).AsTask();
            try
            {
                await budget.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await store.SaveAsync("bbb-update", new byte[] { 8 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await store.DeleteAsync("ccc-delete");
                await store.DeleteAsync("ddd-recreate"); await store.SaveAsync("ddd-recreate", new byte[] { 9 });
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.SaveAsync("eee-atomic", new byte[] { 10 }); await transaction.SaveAsync("fff-atomic", new byte[] { 20 });
                await transaction.CommitAsync();
            }
            finally { budget.Release.TrySetResult(); }
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsTrue(result.Published, result.DeferReason); Assert.IsTrue(result.CatchUpPasses >= 2);
            Assert.IsTrue(result.Work.BytesCopied > 0);
            Assert.IsTrue((await store.VerifyIntegrityAsync()).IsValid);
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, options);
        Assert.AreEqual((byte)1, (await reopened.GetAsync("aaa-keep"))![0]);
        Assert.AreEqual((byte)8, (await reopened.GetAsync("bbb-update"))![0]);
        Assert.IsNull(await reopened.GetAsync("ccc-delete"));
        Assert.AreEqual((byte)9, (await reopened.GetAsync("ddd-recreate"))![0]);
        Assert.AreEqual((byte)10, (await reopened.GetAsync("eee-atomic"))![0]);
        Assert.AreEqual((byte)20, (await reopened.GetAsync("fff-atomic"))![0]);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task RetainedBaselinesSurviveCopyAndReleaseTheirArchive(int variant)
    {
        using var root = new Temp();
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        store.EnableRetainedVersions(new());
        await store.SaveAsync("one", Enumerable.Repeat((byte)1, 4096).ToArray());
        using var first = await store.AcquireRetainedAsync("one");
        await store.SaveAsync("one", Enumerable.Repeat((byte)2, 6144).ToArray());
        using var second = await store.AcquireRetainedAsync("one");
        await store.SaveAsync("one", Enumerable.Repeat((byte)3, 8192).ToArray());
        var budget = new MaintenanceBudget(new() { MaximumWorkUnitBytes = 32 * 1024, MaximumConcurrentUnits = 1 });
        var result = await store.CompactConcurrentAsync(Compaction(budget));
        Assert.IsTrue(result.Published, result.DeferReason);
        Assert.AreEqual((byte)1, (await first.ReadAsync())[0]); Assert.AreEqual((byte)2, (await second.ReadAsync())[0]);
        Assert.AreEqual((byte)3, (await store.GetAsync("one"))![0]);
        Assert.IsTrue(budget.Statistics.AdmittedIoBytes > 2 * result.Work.BytesCopied);
        Assert.AreEqual(0, budget.Statistics.ActiveUnits);
        first.Dispose(); second.Dispose(); Assert.AreEqual(0L, store.RetentionStatistics.ArchivedBytes);
        Assert.IsTrue((await store.CompactConcurrentAsync(Compaction())).Published);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task ByteAndRecordAdmissionDeferWithoutChangingAuthoritativeSet(int variant)
    {
        using var root = new Temp();
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        await store.SaveAsync("one", new byte[4096]); var generation = store.Statistics.GroupSetGeneration;
        var estimate = await store.EstimateConcurrentCompactionAsync(Compaction());
        Assert.IsTrue(estimate.PhysicalBytes >= 4096);
        var result = await store.CompactConcurrentAsync(Compaction() with { MaximumCopyBytes = 1 });
        Assert.IsFalse(result.Published); Assert.AreEqual("copy-byte-budget", result.DeferReason);
        var smallUnit = new MaintenanceBudget(new() { MaximumWorkUnitBytes = 128 });
        result = await store.CompactConcurrentAsync(Compaction(smallUnit));
        Assert.IsFalse(result.Published); Assert.AreEqual("record-unit-limit", result.DeferReason);
        Assert.AreEqual(generation, store.Statistics.GroupSetGeneration);
        Assert.AreEqual(4096, (await store.GetAsync("one"))!.Length);
    }

    [TestMethod]
    [DataRow(0)]

    public async Task UncertainManifestPublicationPreservesCandidateAndRequiresReopen(int variant)
    {
        using var root = new Temp(); var fs = new UncertainFileSystem();
        await using (var store = await AtlasCoreStore.OpenAsync(root.Path, Options(variant, fs)))
        {
            await store.SaveAsync("one", new byte[] { 5 }); fs.FailAfterCommit = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => store.CompactConcurrentAsync(Compaction()).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.GetAsync("one").AsTask());
        }
        await using var reopened = await AtlasCoreStore.OpenAsync(root.Path, Options(variant));
        Assert.AreEqual((byte)5, (await reopened.GetAsync("one"))![0]);
        Assert.IsTrue((await reopened.VerifyIntegrityAsync()).IsValid);
    }

    private sealed class BlockingBudget : IMaintenanceBudget
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long MaximumWorkUnitBytes => 1024 * 1024;
        public MaintenanceBudgetStatistics Statistics => default;
        public async ValueTask<IDisposable> AcquireAsync(long bytes, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return new Lease(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
    private sealed class UncertainFileSystem : IDurableFileSystem
    {
        private readonly IDurableFileSystem _inner = PlatformDurableFileSystem.Instance;
        public bool FailAfterCommit;
        public string DirectoryFlushStrategy => _inner.DirectoryFlushStrategy;
        public void FlushFile(SafeFileHandle handle) => _inner.FlushFile(handle);
        public void FlushDirectory(string path) => _inner.FlushDirectory(path);
        public void ReplaceFile(string temporary, string final) => _inner.ReplaceFile(temporary, final);
        public void PublishFile(string temporary, string final)
        {
            _inner.PublishFile(temporary, final);
            if (FailAfterCommit && final.EndsWith(".commit") && Path.GetFileName(final).StartsWith("set-"))
            { FailAfterCommit = false; throw new IOException("Injected post-publication failure."); }
        }
    }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "idb-atlas-maintenance-" + Guid.NewGuid().ToString("N"));
        public Temp() { Directory.CreateDirectory(Path); }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
