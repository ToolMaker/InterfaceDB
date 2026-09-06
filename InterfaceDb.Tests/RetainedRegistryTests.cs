using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RetainedRegistryTests
{
    [TestMethod]
    public async Task ExpiryAndDisposeCannotFreeAnAdmittedReadAndRenewalIsBounded()
    {
        using var f = new Fixture();
        var clock = new ManualTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = f.Registry(new() { TimeProvider = clock, LeaseDuration = TimeSpan.FromSeconds(2), MaximumLifetime = TimeSpan.FromSeconds(5) },
            async (_, _, _) => { entered.TrySetResult(); await proceed.Task; return [1, 2]; });
        using var lease = registry.Acquire("one", 1, 2);
        clock.Advance(1); lease.Renew(TimeSpan.FromSeconds(2));
        using var use = lease.AcquireUse();
        var read = use.ReadAsync().AsTask(); await entered.Task;
        use.Dispose(); clock.Advance(4); registry.CleanupExpired();
        Assert.AreEqual(0, registry.Statistics.ActiveSessions);
        Assert.AreEqual(1, registry.Statistics.RetainedRecords);
        Assert.ThrowsExactly<RetainedVersionUnavailableException>(() => lease.Renew(TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<RetainedVersionUnavailableException>(() => lease.AcquireUse());
        proceed.TrySetResult(); CollectionAssert.AreEqual(new byte[] { 1, 2 }, await read);
        Assert.AreEqual(0, registry.Statistics.RetainedRecords);
        using var next = registry.Acquire("two", 2, 2);
        clock.Advance(1); next.Renew(TimeSpan.FromSeconds(2));
        clock.Advance(1); next.Renew(TimeSpan.FromSeconds(2));
        clock.Advance(1); next.Renew(TimeSpan.FromSeconds(2));
        clock.Advance(1);
        Assert.ThrowsExactly<RetainedVersionUnavailableException>(() => next.Renew(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task AdmissionAndPartialRelocationFailureKeepEveryAdmittedBaselineReadable()
    {
        using var f = new Fixture(); var fail = true;
        using var registry = f.Registry(new() { MaximumSessions = 3, MaximumRecords = 2, MaximumBytes = 4 },
            (_, address, _) => address == 2 && fail ? ValueTask.FromException<byte[]>(new IOException("injected read failure")) : ValueTask.FromResult(new[] { (byte)address, (byte)address }));
        using var a = registry.Acquire("a", 1, 2);
        using var shared = registry.Acquire("a", 1, 2);
        using var b = registry.Acquire("b", 2, 2);
        Assert.AreEqual(a.RecordId, shared.RecordId);
        Assert.ThrowsExactly<RetainedVersionUnavailableException>(() => registry.Acquire("c", 3, 1));
        await Assert.ThrowsExactlyAsync<IOException>(() => registry.RelocateAsync(_ => true).AsTask());
        fail = false;
        Assert.AreEqual((byte)1, (await a.ReadAsync())[0]);
        Assert.AreEqual((byte)2, (await b.ReadAsync())[0]);
        await registry.RelocateAsync(_ => true);
        Assert.AreEqual(4L, registry.Statistics.ArchivedBytes);
        a.Dispose(); Assert.AreEqual(4L, registry.Statistics.RetainedBytes);
        shared.Dispose(); b.Dispose();
        Assert.AreEqual(0L, registry.Statistics.ArchivedBytes);
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.version", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ReopenCleansOrphansWithoutRemovingAnotherActiveOwnersArchive()
    {
        using var f = new Fixture();
        using var live = f.Registry(); using var lease = live.Acquire("one", 1, 2);
        await live.RelocateAsync(_ => true);
        var orphan = Path.Combine(f.Root, ".idb-history", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan); File.WriteAllBytes(Path.Combine(orphan, "abandoned.version"), [9]);
        using var reopened = f.Registry();
        Assert.IsFalse(Directory.Exists(orphan));
        Assert.AreEqual((byte)1, (await lease.ReadAsync())[0]);
        live.Dispose();
        await Assert.ThrowsExactlyAsync<RetainedVersionUnavailableException>(() => lease.ReadAsync().AsTask());
        Assert.AreEqual(0, Directory.GetFiles(f.Root, "*.version", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task ArchiveDiskFailureDoesNotLoseTheOriginalAddressAndCancellationReleasesReads()
    {
        using var f = new Fixture(); using var registry = f.Registry();
        using var lease = registry.Acquire("one", 1, 2);
        File.WriteAllText(Path.Combine(f.Root, ".idb-history"), "injected blocked destination");
        await Assert.ThrowsExactlyAsync<IOException>(() => registry.RelocateAsync(_ => true).AsTask());
        Assert.AreEqual((byte)1, (await lease.ReadAsync())[0]);
        File.Delete(Path.Combine(f.Root, ".idb-history"));
        await registry.RelocateAsync(_ => true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lease.ReadAsync(cancelled.Token).AsTask());
        lease.Dispose(); Assert.AreEqual(0, registry.Statistics.RetainedRecords);
    }

    [TestMethod]
    public async Task FailedPhysicalDeletionIsCountedAndRetriedWithoutClaimingReclamation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires Windows file deletion sharing semantics.");
        using var f = new Fixture(); using var registry = f.Registry();
        using var lease = registry.Acquire("one", 1, 2);
        await registry.RelocateAsync(_ => true);
        var path = Directory.GetFiles(f.Root, "*.version", SearchOption.AllDirectories).Single();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            lease.Dispose(); Assert.AreEqual(0L, registry.Statistics.RetainedBytes);
            Assert.AreEqual(2L, registry.Statistics.ArchivedBytes);
            Assert.IsTrue(registry.Statistics.CleanupFailures > 0); Assert.IsTrue(File.Exists(path));
        }
        registry.CleanupExpired(); Assert.AreEqual(0L, registry.Statistics.ArchivedBytes);
        Assert.IsFalse(File.Exists(path));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "idb-registry-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal RetainedPayloadRegistry<int> Registry(RetainedVersionOptions? options = null,
            Func<string, int, CancellationToken, ValueTask<byte[]>>? read = null)
            => new(Root, options ?? new(), _ => ValueTask.FromResult<IDisposable>(new Empty()),
                read ?? ((_, id, _) => ValueTask.FromResult(new[] { (byte)id, (byte)id })));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Empty : IDisposable { public void Dispose() { } }
    internal sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
