using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Repositories;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class StorageReviewHardeningTests
{
    [TestMethod]
    public async Task LiveRevisionWorkingSetSurvivesMissingEntryChurnWithoutStorageReads()
    {
        await using var fixture = new Fixture(true, physical: true, enabled: false);
        await fixture.Repository.InitializeAsync();
        var envelopes = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        var coordinator = (EnvelopeWriteCoordinator<IRecord>)Field(envelopes, "_revisions");
        for (var i = 0; i < 10_000; i++) coordinator.Committed("live-" + i, i + 1);
        for (var i = 0; i < 10_000; i++) coordinator.Committed("missing-" + i, null);
        var reads = 0;
        ValueTask<byte[]?> UnexpectedRead(CancellationToken _)
        {
            reads++;
            return ValueTask.FromResult<byte[]?>(null);
        }
        for (var i = 0; i < 10_000; i++)
            Assert.AreEqual(i + 1L, await coordinator.ReadCommittedRevisionAsync("live-" + i, UnexpectedRead, default));
        Assert.AreEqual(0, reads, "Unrelated missing entries must not evict current revisions.");
        Assert.IsTrue(((IDictionary)Field(coordinator, "_committed")).Count <= 10_000 + 1024);

        // Retiring an older tombstone for a reused key must not remove its new live revision.
        coordinator.Committed("reused", null);
        coordinator.Committed("reused", 42);
        for (var i = 0; i < 1025; i++) coordinator.Committed("another-miss-" + i, null);
        Assert.AreEqual(42L, await coordinator.ReadCommittedRevisionAsync("reused", UnexpectedRead, default));
        Assert.AreEqual(0, reads);

        for (var i = 0; i < 10_000; i++) coordinator.Committed("live-" + i, null);
        Assert.IsTrue(((IDictionary)Field(coordinator, "_committed")).Count <= 1 + 1024);
        coordinator.ObserveCommittedRevision("live-0", 1); // Delayed read of a deleted record.
        Assert.IsNull(await coordinator.ReadCommittedRevisionAsync("live-0", UnexpectedRead, default));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task DeleteChurnBoundsTrackingAndLateReadCannotResurrectRetiredRevision()
    {
        await using var fixture = new Fixture(true, cache: StorageObjectCacheMode.MetadataOnly, physical: true, enabled: false);
        await fixture.Repository.InitializeAsync();
        for (var i = 0; i < 4200; i++)
        {
            var saved = await fixture.Repository.SaveAsync(new Record { Id = "churn" + i, Value = i });
            Assert.IsTrue(await fixture.Repository.DeleteAsync(saved));
        }
        var index = fixture.Services.GetRequiredKeyedService<IIndexManager<IRecord>>("notifications");
        var orders = (IDictionary)Field(index, "_lastMutationOrder");
        Assert.IsTrue(orders.Count <= 1024, $"Retained {orders.Count} deleted keys.");
        var envelopes = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        var coordinator = (EnvelopeWriteCoordinator<IRecord>)Field(envelopes, "_revisions");
        Assert.IsTrue(((IDictionary)Field(coordinator, "_committed")).Count <= 4096);
        coordinator.ObserveCommittedRevision("retired-key", 1);
        var reads = 0;
        var revision = await coordinator.ReadCommittedRevisionAsync("retired-key", _ =>
        {
            reads++;
            return ValueTask.FromResult<byte[]?>(null);
        }, CancellationToken.None);
        Assert.IsNull(revision);
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task MissingQueryIsNullableInContractAndImplementation()
    {
        await using var fixture = new Fixture(true, enabled: false);
        await fixture.Repository.InitializeAsync();
        Assert.IsNull(await fixture.Repository.QueryAsync(_ => false));
        var method = typeof(InterfaceRepositoryDb<IRecord>).GetMethods().Single(m => m.Name == "QueryAsync" && !m.IsGenericMethod);
        Assert.AreEqual(NullabilityState.Nullable,
            new NullabilityInfoContext().Create(method.ReturnParameter).GenericTypeArguments[0].ReadState);
    }

    [TestMethod]
    public void InterruptedClockPublicationPreservesLastDurableFloorAndCorruptionFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-clock-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, ".idb-revision-clock");
            const long floor = long.MaxValue / 2;
            var bytes = new byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, floor);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), ~floor);
            File.WriteAllBytes(path, bytes); // Legacy format remains readable.
            Assert.ThrowsExactly<IOException>(() => new StoreRevisionClock(root, new InterruptedPublication()).Next(0));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            var issued = new StoreRevisionClock(root).Next(0);
            Assert.IsTrue(issued > floor);
            Assert.IsTrue(new StoreRevisionClock(root).Next(0) > issued);
            Assert.AreEqual(16L, new FileInfo(path).Length);
            File.WriteAllBytes(path, new byte[16]);
            Assert.ThrowsExactly<InvalidDataException>(() => new StoreRevisionClock(root).Next(0));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class InterruptedPublication : IFilePublication
    {
        public void Replace(string stagedPath, string destinationPath)
        {
            File.WriteAllBytes(stagedPath, [1, 2, 3]);
            throw new IOException("Simulated interrupted staging write before publication.");
        }
        public void Delete(string path) => throw new NotSupportedException();
        public void FlushDirectory(string directoryPath) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task DurableFileCommitUsesPublicationBeforeAcknowledgementIncludingDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-publication-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var publication = new RecordingPublication();
            var policy = StoragePolicy.Default with { DurabilityMode = StorageDurabilityMode.Durable };
            var coordinator = new CoalescingFileWriteCoordinator<IRecord>(new NoopLock(),
                new Config(root, ContractRepositoryType.FileSystem, policy), policy, publication);
            var path = Path.Combine(root, "record.mpk");
            await coordinator.CommitEnvelopeAsync(path, false, async (_, staged, token) =>
                await File.WriteAllBytesAsync(staged, [1, 2, 3], token), afterCommit: () => Assert.AreEqual(1, publication.Replacements));
            await coordinator.CommitEnvelopeAsync(path, true, (_, _, _) => ValueTask.CompletedTask,
                afterCommit: () => Assert.AreEqual(1, publication.Deletions));
            Assert.IsFalse(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private sealed class NoopLock : IStorageWriteLock, IAsyncDisposable
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(string providerLocator, CancellationToken cancellationToken = default) => ValueTask.FromResult<IAsyncDisposable>(this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RecordingPublication : IFilePublication
    {
        public int Replacements, Deletions;
        public void Replace(string stagedPath, string destinationPath) { PlatformFilePublication.Instance.Replace(stagedPath, destinationPath); Replacements++; }
        public void Delete(string path) { PlatformFilePublication.Instance.Delete(path); Deletions++; }
        public void FlushDirectory(string directoryPath) => PlatformFilePublication.Instance.FlushDirectory(directoryPath);
    }
}
