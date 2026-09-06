using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RevisionWriteTests
{
    private static long Revision(IRecord item) => item.Envelope!.RevisionTimestampUtcMicroseconds;
    private static IRecord Copy(Fixture fixture, IRecord item)
    {
        var serializer = fixture.Services.GetRequiredService<IObjectSerializer<IRecord>>();
        var envelope = serializer.Deserialize(serializer.Serialize(item.Envelope!));
        envelope.Location = item.Envelope!.Location;
        return fixture.Services.GetRequiredService<IEnvelopeLifecycle<IRecord>>().Unwrap(envelope);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task FirstSaveMatchingAndBothDirectionsOfMismatch(bool segment)
    {
        await using var f = new Fixture(segment, enabled: false);
        await f.Repository.InitializeAsync();
        var original = new Record { Id = "one", Value = 1 };
        var saved = await f.Repository.SaveAsync(original);
        Assert.IsTrue(Revision(saved) > 0);
        Assert.AreEqual(Revision(saved), Revision(original));
        var stale = Copy(f, saved);
        var newer = Copy(f, saved);
        newer.Envelope!.RevisionTimestampUtcMicroseconds += 1_000_000_000;
        var loaded = Revision(saved);
        saved.Value = 2;
        var committed = await f.Repository.SaveAsync(saved);
        Assert.IsTrue(Revision(committed) > loaded);
        foreach (var losing in new[] { stale, newer })
        {
            var expected = Revision(losing);
            var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.SaveAsync(losing).AsTask());
            Assert.AreEqual(expected, conflict.ExpectedRevision);
            Assert.AreEqual(Revision(committed), conflict.CurrentRevision);
            var current = await conflict.ReadCurrentAsync();
            Assert.AreEqual(2, current!.Value);
            current.Value = 900;
            Assert.AreEqual(2, (await conflict.ReadCurrentAsync())!.Value);
            Assert.AreEqual(expected, Revision(losing));
        }
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task TwoLoadedCallersOnlyOneWinsAndIndexesUseCommittedValues(bool segment)
    {
        await using var f = new Fixture(segment, cache: StorageObjectCacheMode.MetadataOnly);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "race", Value = 0 });
        var first = Copy(f, saved); var second = Copy(f, saved);
        first.Value = 10; second.Value = 20;
        var notifications = 0;
        using var sub = f.Notifications.SubscribeChanged(_ => { Interlocked.Increment(ref notifications); return ValueTask.CompletedTask; }, _ => true);
        async Task<IRecord?> TrySave(IRecord item)
        {
            try { return await f.Repository.SaveAsync(item); }
            catch (WriteConflictException<IRecord>) { return null; }
        }
        var outcomes = await Task.WhenAll(TrySave(first), TrySave(second));
        Assert.AreEqual(1, outcomes.Count(x => x is not null));
        var winner = outcomes.Single(x => x is not null)!;
        var read = await f.Repository.SearchFirstAsync(x => x.Id == "race");
        Assert.AreEqual(winner.Value, read!.Value);
        for (var i = 0; i < 500 && Volatile.Read(ref notifications) == 0; i++) await Task.Delay(10);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task DeleteRejectsStaleUpdateAndUpdateRejectsStaleDelete(bool segment)
    {
        await using var f = new Fixture(segment, enabled: false);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "delete" });
        var stale = Copy(f, saved);
        saved.Value++;
        var current = await f.Repository.SaveAsync(saved);
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.DeleteAsync(stale).AsTask());
        var beforeDelete = Copy(f, current);
        Assert.IsTrue(await f.Repository.DeleteAsync(current));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.SaveAsync(beforeDelete).AsTask());
        var replacement = await f.Repository.SaveAsync(new Record { Id = "delete" });
        Assert.IsTrue(Revision(replacement) > Revision(beforeDelete));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.SaveAsync(beforeDelete).AsTask());
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task MetadataAndReplicationKeepDestinationPreconditions(bool segment)
    {
        await using var f = new Fixture(segment, enabled: false);
        await f.Repository.InitializeAsync();
        var repo = (IEnvelopeMetadataRepository<IRecord>)f.Repository;
        var saved = await repo.SaveWithExtensionMetadataAsync(new Record { Id = "meta", Value = 1 }, null, [1]);
        var key = saved.Envelope!.EnvelopeCacheKey;
        var revision = Revision(saved);
        var acknowledged = await repo.SaveWithExtensionMetadataAsync(new Record { Id = "meta", Value = 999 }, key, [2],
            new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
        Assert.AreEqual(revision, Revision(acknowledged));
        Assert.AreEqual(1, acknowledged.Value);
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveWithExtensionMetadataAsync(
            new Record { Id = "meta" }, key, [3], new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1])).AsTask());
        var imported = await repo.SaveWithExtensionMetadataAsync(new Record { Id = "meta", Value = 4 }, key, [4],
            new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.Import, revision + 500));
        Assert.AreEqual(revision + 500, Revision(imported));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveWithExtensionMetadataAsync(
            new Record { Id = "meta" }, key, [5], new EnvelopeWriteCondition(revision, EnvelopeRevisionAction.Import, revision + 1000)).AsTask());
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task FailedOrCancelledSaveDoesNotPublishRevision(bool segment)
    {
        var serializer = new FailureSerializer();
        await using var f = new Fixture(segment, serializer: serializer, enabled: false);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "failure", Value = 1 });
        var revision = Revision(saved);
        saved.Value = 2; serializer.Fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Repository.SaveAsync(saved).AsTask());
        Assert.AreEqual(revision, Revision(saved));
        serializer.Fail = false;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Repository.SaveAsync(saved, cancellation.Token).AsTask());
        Assert.AreEqual(revision, Revision(saved));
        var committed = await f.Services.GetRequiredService<IEnvelopeRepository<IRecord>>()
            .CaptureForTest(saved.Envelope!);
        Assert.AreEqual(1, committed.Value);
        Assert.AreEqual(revision, Revision(committed));
    }

    [TestMethod]
    public async Task SegmentConflictRollsBackWholeBatchAndRejectsDuplicatePhysicalKeys()
    {
        await using var f = new Fixture(true, cache: StorageObjectCacheMode.MetadataOnly, enabled: false);
        await f.Repository.InitializeAsync();
        var a = await f.Repository.SaveAsync(new Record { Id = "a", Value = 1 });
        var b = await f.Repository.SaveAsync(new Record { Id = "b", Value = 2 });
        var old = Copy(f, a); a.Value = 3; await f.Repository.SaveAsync(a);
        b.Value = 4;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.SaveManyAsync([b, old]).AsTask());
        Assert.AreEqual(2, (await f.Repository.SearchFirstAsync(x => x.Id == "b"))!.Value);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.Repository.SaveManyAsync([b, Copy(f, b)]).AsTask());
    }

    [TestMethod]
    public void ClockSameTickRollbackAndDurableRangeReopen()
    {
        var time = new ManualClock();
        var generator = new MonotonicRevisionTimestampGenerator(time);
        var first = generator.Next(0); var second = generator.Next(0);
        time.Now = DateTimeOffset.UnixEpoch;
        Assert.IsTrue(generator.Next(second) > second); Assert.IsTrue(second > first);
        var root = Path.Combine(Path.GetTempPath(), "idb-revision-clock-" + Guid.NewGuid());
        try
        {
            var one = new StoreRevisionClock(root).Next(long.MaxValue / 2);
            var two = new StoreRevisionClock(root).Next(0);
            Assert.IsTrue(two > one);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod] [DataRow(false, false)] [DataRow(true, false)] [DataRow(false, true)] [DataRow(true, true)]
    public async Task ReadersAndUnrelatedWritersProceedWhileAWriteIsStaged(bool segment, bool cancel)
    {
        var serializer = new BlockingSerializer();
        await using var f = new Fixture(segment, cache: StorageObjectCacheMode.MetadataOnly, enabled: false, serializer: serializer);
        await f.Repository.InitializeAsync();
        var first = await f.Repository.SaveAsync(new Record { Id = "blocked", Value = 1 });
        IRecord other;
        do { other = await f.Repository.SaveAsync(new Record { Id = Guid.NewGuid().ToString(), Value = 1 }); }
        while ((uint)StringComparer.Ordinal.GetHashCode(other.Envelope!.EnvelopeCacheKey) % 256 == (uint)StringComparer.Ordinal.GetHashCode(first.Envelope!.EnvelopeCacheKey) % 256);
        var revision = Revision(first); first.Value = 2;
        using var cancellation = new CancellationTokenSource();
        var write = Task.Run(async () => await f.Repository.SaveAsync(first, cancellation.Token));
        try
        {
            await serializer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(revision, Revision(first));
            var read = await f.Repository.SearchFirstAsync(x => x.Id == "blocked").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, read!.Value); Assert.AreEqual(revision, Revision(read));
            other.Value = 3;
            await f.Repository.SaveAsync(other).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cancellation.Cancel();
        }
        finally
        {
            serializer.Release.Set();
            try { await write; } catch (OperationCanceledException) when (cancel) { }
        }
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => write);
            Assert.AreEqual(revision, Revision(first));
            Assert.AreEqual(1, (await f.Repository.SearchFirstAsync(x => x.Id == "blocked"))!.Value);
        }
        else Assert.IsTrue(Revision(await write) > revision);
    }

    private sealed class BlockingSerializer : IRevisionObjectSerializer<IRecord>
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope)
        {
            if (envelope.ObjectInstance!.Id == "blocked" && envelope.ObjectInstance.Value == 2)
            { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); }
            return _inner.Serialize(envelope);
        }
        public IObjectEnvelope<IRecord> Deserialize(byte[] payload) => _inner.Deserialize(payload);
        public long ReadRevision(ReadOnlyMemory<byte> payload) => _inner.ReadRevision(payload);
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => _inner.ReadRevisionAsync(source, cancellationToken);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task FileBulkCancellationPublishesOnlyCommittedItems(bool physical)
    {
        using var cancellation = new CancellationTokenSource();
        var serializer = new CancelSecondSerializer(cancellation);
        await using var f = new Fixture(false, cache: StorageObjectCacheMode.MetadataOnly,
            physical: physical, serializer: serializer, concurrency: 1);
        await f.Repository.InitializeAsync();
        var a = await f.Repository.SaveAsync(new Record { Id = "a", Value = 1 });
        var b = await f.Repository.SaveAsync(new Record { Id = "b", Value = 1 });
        var beforeA = Revision(a); var beforeB = Revision(b);
        a.Value = 2; b.Value = 2; serializer.Armed = true;
        var events = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var subscription = f.Notifications.SubscribeChanged(item => { events.Add(item.Entity.Value); return ValueTask.CompletedTask; }, _ => true);
        var failure = await Assert.ThrowsExactlyAsync<Idb.Libraries.Abstractions.Exceptions.StorageBulkOperationCanceledException>(
            () => f.Repository.SaveManyAsync([a, b], cancellation.Token).AsTask());
        CollectionAssert.AreEqual(new[] { 0 }, failure.SucceededIndexes.ToArray());
        Assert.IsTrue(Revision(a) > beforeA); Assert.AreEqual(beforeB, Revision(b));
        Assert.AreEqual("a", (await f.Repository.SearchFirstAsync(x => x.Value == 2))!.Id);
        Assert.AreEqual(1, (await f.Repository.SearchFirstAsync(x => x.Id == "b"))!.Value);
        await subscription.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 2 }, events.ToArray());
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task ResolverCommitsResolvedValuesAndStagesItsRevision(bool segment)
    {
        var strategy = new ResolvingStrategy();
        var serializer = new FailureSerializer();
        await using var f = new Fixture(segment, cache: StorageObjectCacheMode.MetadataOnly,
            enabled: false, serializer: serializer, strategy: strategy);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "resolve", Value = 1 });
        var stale = Copy(f, saved); saved.Value = 2; saved = await f.Repository.SaveAsync(saved);
        strategy.Resolved = Copy(f, saved); strategy.Resolved.Value = 99;
        var resolvedRevision = Revision(strategy.Resolved);
        serializer.Fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => f.Repository.SaveAsync(stale).AsTask());
        Assert.AreEqual(resolvedRevision, Revision(strategy.Resolved));
        serializer.Fail = false;
        var committed = await f.Repository.SaveAsync(stale);
        Assert.AreEqual(99, committed.Value);
        Assert.IsTrue(Revision(committed) > resolvedRevision);
        Assert.AreEqual(99, (await f.Repository.SearchFirstAsync(x => x.Value == 99))!.Value);
        Assert.IsNull(await f.Repository.SearchFirstAsync(x => x.Value == 2));
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task MutatingCachedEnvelopeRevisionCannotChangeCommittedPrecondition(bool segment)
    {
        await using var f = new Fixture(segment, enabled: false);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "cache", Value = 1 });
        var committedRevision = Revision(saved);
        saved.Envelope!.RevisionTimestampUtcMicroseconds += 10;
        var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => f.Repository.SaveAsync(saved).AsTask());
        Assert.AreEqual(committedRevision, conflict.CurrentRevision);
    }


    [TestMethod]
    public async Task FileRejectsUncoordinatedWrites()
    {
        await using var f = new Fixture(false, enabled: false, useSidecarWriteLocks: false);
        await f.Repository.InitializeAsync();
        var candidate = new Record { Id = "unsupported" };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => f.Repository.SaveAsync(candidate).AsTask());
        Assert.IsNull(candidate.Envelope);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public void ProvidersRejectSerializersWithoutRevisionCapability(bool segment)
    {
        Assert.ThrowsExactly<NotSupportedException>(() => new Fixture(segment, serializer: new UnsupportedSerializer()));
    }

    private sealed class UnsupportedSerializer : IObjectSerializer<IRecord>
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope) => _inner.Serialize(envelope);
        public IObjectEnvelope<IRecord> Deserialize(byte[] bytes) => _inner.Deserialize(bytes);
    }


    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task NotificationKeepsItsOwnCommitWhenPhysicalCallbackWritesAgain(bool segment)
    {
        await using var f = new Fixture(segment, cache: StorageObjectCacheMode.MetadataOnly, physical: true);
        await f.Repository.InitializeAsync();
        var saved = await f.Repository.SaveAsync(new Record { Id = "nested", Value = 1 });
        var lower = f.Services.GetRequiredService<IEnvelopeRepository<IRecord>>();
        var events = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var notification = f.Notifications.SubscribeChanged(change => { events.Add(change.Entity.Value); return ValueTask.CompletedTask; }, _ => true);
        using var callback = lower.SubscribeToStored(async envelope =>
        {
            if (envelope.ObjectInstance?.Value != 2) return;
            var next = await lower.CaptureForTest(envelope);
            next.Value = 3;
            await lower.WriteAsync(next);
        });
        saved.Value = 2;
        var result = await f.Repository.SaveAsync(saved).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, result.Value);
        await notification.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { 2 }, events.ToArray());
        Assert.AreEqual(3, (await f.Repository.SearchFirstAsync(x => x.Value == 3))!.Value);
        Assert.IsNull(await f.Repository.SearchFirstAsync(x => x.Value == 2));
    }

    private sealed class ResolvingStrategy : IWriteConflictStrategy<IRecord>
    {
        internal IRecord? Resolved;
        public ValueTask<EnvelopeWriteDecision<IRecord>> DecideAsync(EnvelopeWriteConflict<IRecord> context,
            CancellationToken cancellationToken = default)
            => Resolved is null ? new StrictTimestampMatchStrategy<IRecord>().DecideAsync(context, cancellationToken)
                : ValueTask.FromResult(new EnvelopeWriteDecision<IRecord>(true, Resolved.Envelope));
    }

    private sealed class CancelSecondSerializer(CancellationTokenSource cancellation) : IRevisionObjectSerializer<IRecord>
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        internal bool Armed;
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope)
        {
            if (Armed && envelope.ObjectInstance!.Id == "b") cancellation.Cancel();
            return _inner.Serialize(envelope);
        }
        public IObjectEnvelope<IRecord> Deserialize(byte[] bytes) => _inner.Deserialize(bytes);
        public long ReadRevision(ReadOnlyMemory<byte> bytes) => _inner.ReadRevision(bytes);
        public ValueTask<long> ReadRevisionAsync(Stream stream, CancellationToken cancellationToken = default)
            => _inner.ReadRevisionAsync(stream, cancellationToken);
    }

    private sealed class FailureSerializer : IRevisionObjectSerializer<IRecord>
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        internal bool Fail;
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope) => Fail ? throw new IOException("Injected failure") : _inner.Serialize(envelope);
        public IObjectEnvelope<IRecord> Deserialize(byte[] payload) => _inner.Deserialize(payload);
        public long ReadRevision(ReadOnlyMemory<byte> payload) => _inner.ReadRevision(payload);
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => _inner.ReadRevisionAsync(source, cancellationToken);
    }
}

internal static class RevisionTestSnapshot
{
    internal static async Task<RepositoryNotificationTests.IRecord> CaptureForTest(this IEnvelopeRepository<RepositoryNotificationTests.IRecord> repo,
        IObjectEnvelope<RepositoryNotificationTests.IRecord> envelope)
        => (await ((ICommittedEnvelopeSnapshotSource<RepositoryNotificationTests.IRecord>)repo).CaptureCommittedAsync(envelope, int.MaxValue))!.Materialize();
}
