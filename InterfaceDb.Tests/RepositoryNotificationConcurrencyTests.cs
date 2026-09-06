using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Repositories;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RepositoryNotificationConcurrencyTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15));
    private static RepositoryNotificationHub<IRecord> Hub(Fixture fixture, RepositoryNotificationOptions? options = null)
        => new(fixture.Services.GetRequiredService<IEnvelopeRepository<IRecord>>(), options ?? new(), fixture.Repository.InitializeAsync);
    private static IRepositorySubscription Changed(RepositoryNotificationHub<IRecord> hub, Action<IRecord>? receive = null)
        => hub.Subscribe(RepositoryEventKind.Changed, (entity, _) => { receive?.Invoke(entity); return ValueTask.CompletedTask; },
            _ => true, default, null, false);

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task EmptyAndObservedDistinctKeysCanOverlap(bool segment)
    {
        await using var fixture = new Fixture(segment, enabled: false);
        var first = await fixture.Repository.SaveAsync(new Record { Id = "a" });
        var second = await fixture.Repository.SaveAsync(new Record { Id = "b" });
        using var hub = Hub(fixture);
        foreach (var observe in new[] { false, true })
        {
            using var subscription = observe ? Changed(hub) : null;
            var entered = Signal(); var release = Signal();
            async ValueTask<IRecord> Block(IRecord entity, CancellationToken token)
            { entered.TrySetResult(); await release.Task; return await fixture.Repository.SaveAsync(entity, token); }
            first.Value++; second.Value++;
            var blocked = hub.SaveAsync(first, Block, default).AsTask();
            try
            {
                await Within(entered.Task);
                await Within(hub.SaveAsync(second, fixture.Repository.SaveAsync, default).AsTask());
                Assert.IsFalse(blocked.IsCompleted, "An unrelated key must complete while the first write is blocked.");
            }
            finally { release.TrySetResult(); await Within(blocked); }
            if (subscription is not null) await Within(subscription.WaitForIdleAsync().AsTask());
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task HandoverAndCancelledIntermediatePhaseCannotOvertakePriorWrites(bool segment)
    {
        await using var fixture = new Fixture(segment, enabled: false);
        var first = await fixture.Repository.SaveAsync(new Record { Id = "a" });
        var second = await fixture.Repository.SaveAsync(new Record { Id = "b" });
        using var hub = Hub(fixture);
        var entered = Signal(); var release = Signal();
        async ValueTask<IRecord> Block(IRecord entity, CancellationToken token)
        { entered.TrySetResult(); await release.Task; return await fixture.Repository.SaveAsync(entity, token); }
        first.Value = 1;
        var oldWrite = hub.SaveAsync(first, Block, default).AsTask();
        try
        {
            await Within(entered.Task);
            using var firstSub = Changed(hub);
            using var cancel = new CancellationTokenSource();
            var cancelled = hub.SaveAsync(second, fixture.Repository.SaveAsync, cancel.Token).AsTask();
            Assert.IsFalse(cancelled.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
            firstSub.Dispose();
            using var secondSub = Changed(hub);
            second.Value = 2;
            var later = hub.SaveAsync(second, fixture.Repository.SaveAsync, default).AsTask();
            Assert.IsFalse(later.IsCompleted, "Rapid empty/observed transitions must retain the original write barrier.");
            release.TrySetResult();
            await Within(Task.WhenAll(oldWrite, later));
            await Within(secondSub.WaitForIdleAsync().AsTask());
            Assert.AreEqual(1L, secondSub.DeliveredCount);
            first.Value = 3;
            await hub.SaveAsync(first, fixture.Repository.SaveAsync, default);
            await Within(secondSub.WaitForIdleAsync().AsTask());
            Assert.AreEqual(2L, secondSub.DeliveredCount);
        }
        finally { release.TrySetResult(); await Within(oldWrite); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LastUnsubscribeWaitsForObservedWriteAndDiscardsOldBaselines(bool segment)
    {
        await using var fixture = new Fixture(segment, enabled: false);
        var entity = await fixture.Repository.SaveAsync(new Record { Id = "a" });
        using var hub = Hub(fixture);
        using var sub = Changed(hub);
        entity.Value = 1; await hub.SaveAsync(entity, fixture.Repository.SaveAsync, default);
        await Within(sub.WaitForIdleAsync().AsTask());
        var entered = Signal(); var release = Signal();
        async ValueTask<IRecord> Block(IRecord item, CancellationToken token)
        { entered.TrySetResult(); await release.Task; return await fixture.Repository.SaveAsync(item, token); }
        entity.Value = 2;
        var blocked = hub.SaveAsync(entity, Block, default).AsTask();
        try
        {
            await Within(entered.Task); sub.Dispose();
            var ran = false;
            var next = hub.SaveAsync(new Record { Id = "b" }, (item, token) =>
            { ran = true; return fixture.Repository.SaveAsync(item, token); }, default).AsTask();
            Assert.IsFalse(ran); Assert.IsFalse(next.IsCompleted);
            release.TrySetResult(); await Within(Task.WhenAll(blocked, next));
            entity.Value = 9; await hub.SaveAsync(entity, fixture.Repository.SaveAsync, default);
            using var after = Changed(hub);
            await hub.SaveAsync(entity, fixture.Repository.SaveAsync, default);
            await Within(after.WaitForIdleAsync().AsTask());
            Assert.AreEqual(0L, after.DeliveredCount, "No stale baseline may turn an unchanged save into Changed.");
        }
        finally { release.TrySetResult(); await Within(blocked); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SameKeyDifferentInstancesAndReversedBatchesRemainOrdered(bool segment)
    {
        await using var fixture = new Fixture(segment, enabled: false);
        var a = await fixture.Repository.SaveAsync(new Record { Id = "a" });
        var b = await fixture.Repository.SaveAsync(new Record { Id = "b" });
        var source = (ICommittedEnvelopeSnapshotSource<IRecord>)fixture.Services.GetRequiredService<IEnvelopeRepository<IRecord>>();
        var alias = (await source.CaptureCommittedAsync(a.Envelope!, 10000))!.Materialize();
        using var hub = Hub(fixture);
        var values = new ConcurrentQueue<int>();
        using var sub = Changed(hub, entity => values.Enqueue(entity.Value));
        var entered = Signal(); var release = Signal();
        async ValueTask<IRecord> Block(IRecord item, CancellationToken token)
        { entered.TrySetResult(); await release.Task; return await fixture.Repository.SaveAsync(item, token); }
        a.Value = 1; alias.Value = 2;
        var first = hub.SaveAsync(a, Block, default).AsTask();
        try
        {
            await Within(entered.Task);
            var next = hub.SaveAsync(alias, fixture.Repository.SaveAsync, default).AsTask();
            Assert.IsFalse(next.IsCompleted);
            release.TrySetResult(); await Within(first);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => next);
            await Within(sub.WaitForIdleAsync().AsTask());
            CollectionAssert.AreEqual(new[] { 1 }, values.ToArray());
            await Within(Task.WhenAll(
                hub.SaveManyAsync(new[] { a, b }, fixture.Repository.SaveManyAsync, default).AsTask(),
                hub.SaveManyAsync(new[] { b, a }, fixture.Repository.SaveManyAsync, default).AsTask()));
        }
        finally { release.TrySetResult(); await Within(first); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ObservedInsertPublicationBlocksAQueryThenUpdateUntilCaptured(bool segment)
    {
        await using var fixture = new Fixture(segment, enabled: false);
        using var hub = Hub(fixture);
        var createdValues = new ConcurrentQueue<int>(); var changedValues = new ConcurrentQueue<int>();
        using var created = hub.Subscribe(RepositoryEventKind.Created, (item, _) =>
        { createdValues.Enqueue(item.Value); return ValueTask.CompletedTask; }, null, default, null, false);
        using var changed = Changed(hub, item => changedValues.Enqueue(item.Value));
        var committed = Signal(); var release = Signal();
        async ValueTask<IRecord> BlockAfterCommit(IRecord item, CancellationToken token)
        { var saved = await fixture.Repository.SaveAsync(item, token); committed.TrySetResult(); await release.Task; return saved; }
        var first = hub.SaveAsync(new Record { Id = "new", Value = 1 }, BlockAfterCommit, default).AsTask();
        try
        {
            await Within(committed.Task);
            var source = (ICommittedEnvelopeSnapshotSource<IRecord>)fixture.Services.GetRequiredService<IEnvelopeRepository<IRecord>>();
            var queried = await fixture.Repository.SearchFirstAsync(item => item.Id == "new");
            var copy = (await source.CaptureCommittedAsync(queried!.Envelope!, 10000))!.Materialize();
            copy.Value = 2;
            var next = hub.SaveAsync(copy, fixture.Repository.SaveAsync, default).AsTask();
            Assert.IsFalse(next.IsCompleted);
            release.TrySetResult(); await Within(Task.WhenAll(first, next));
            await Within(Task.WhenAll(created.WaitForIdleAsync().AsTask(), changed.WaitForIdleAsync().AsTask()));
            CollectionAssert.AreEqual(new[] { 1 }, createdValues.ToArray());
            CollectionAssert.AreEqual(new[] { 2 }, changedValues.ToArray());
        }
        finally { release.TrySetResult(); await Within(first); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DetectionIsDeferredOncePerEventAndUsesStableCommittedValues(bool segment)
    {
        var comparer = new BlockingComparer();
        await using var fixture = new Fixture(segment, options: new()
        { PropertyComparers = new Dictionary<string, IEqualityComparer<object?>> { [nameof(IRecord.Value)] = comparer } });
        var entity = await fixture.Repository.SaveAsync(new Record { Id = "a", Value = 1 });
        var subscribers = Enumerable.Range(0, 10).Select(_ => fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true)).ToArray();
        try
        {
            entity.Value = 2;
            await Within(fixture.Repository.SaveAsync(entity).AsTask());
            await Within(comparer.Entered.Task);
            Assert.IsFalse(subscribers[0].WaitForIdleAsync().IsCompleted);
            entity.Value = 3; await Within(fixture.Repository.SaveAsync(entity).AsTask());
            entity.Value = 99; // Neither queued before nor queued after may alias this mutable instance.
            comparer.Release.TrySetResult();
            await Within(Task.WhenAll(subscribers.Select(sub => sub.WaitForIdleAsync().AsTask())));
            Assert.AreEqual(2, comparer.Calls, "Ten subscribers must not perform ten comparisons of the same change.");
            CollectionAssert.AreEqual(new[] { "1:2", "2:3" }, comparer.Values.ToArray());
            Assert.IsTrue(subscribers.All(sub => sub.DeliveredCount == 2 && !sub.IsDisposed));
        }
        finally { comparer.Release.TrySetResult(); foreach (var sub in subscribers) sub.Dispose(); }
    }

    private sealed class BlockingComparer : IEqualityComparer<object?>
    {
        internal readonly TaskCompletionSource Entered = Signal(), Release = Signal();
        internal readonly ConcurrentQueue<string> Values = new();
        internal int Calls;
        public new bool Equals(object? left, object? right)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Values.Enqueue($"{left}:{right}"); return object.Equals(left, right);
        }
        public int GetHashCode(object? item) => item?.GetHashCode() ?? 0;
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ComparisonFailureAndImpureComparerDoNotFailCommittedSave(bool segment)
    {
        var comparer = new ActionComparer();
        await using var fixture = new Fixture(segment, options: new()
        { PropertyComparers = new Dictionary<string, IEqualityComparer<object?>> { [nameof(IRecord.Value)] = comparer } });
        var entity = await fixture.Repository.SaveAsync(new Record { Id = "a", Value = 1 });
        comparer.Action = () => fixture.Repository.SaveAsync(entity).GetAwaiter().GetResult();
        using var sub = fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true);
        entity.Value = 2;
        var saved = await fixture.Repository.SaveAsync(entity);
        Assert.AreEqual(2, saved.Value);
        Assert.AreEqual(RepositorySubscriptionEndReason.ObservationFailed,
            await sub.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.IsInstanceOfType<InvalidOperationException>(comparer.Failure);
        Assert.AreEqual(0L, sub.DeliveredCount);
    }

    private sealed class ActionComparer : IEqualityComparer<object?>
    {
        internal Action? Action;
        internal Exception? Failure;
        public new bool Equals(object? left, object? right)
        {
            try { Action?.Invoke(); }
            catch (Exception error) { Failure = error; throw; }
            return object.Equals(left, right);
        }
        public int GetHashCode(object? item) => 0;
    }

    [TestMethod]
    public async Task DeferredComparisonUsesDetachedObjectsOnceAndChargesBothSnapshots()
    {
        var before = new ProbeSnapshot(1); var after = new ProbeSnapshot(2);
        var item = new RepositoryEvent<IRecord>(after, before, new(new()), Task.CompletedTask, null);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            CollectionAssert.AreEqual(new[] { "Value" }, item.Changes().ToArray()))));
        Assert.AreEqual(1, before.Reads); Assert.AreEqual(1, after.Reads);
        Assert.IsFalse(before.Attached); Assert.IsFalse(after.Attached);
        Assert.AreEqual(1000L, item.ByteLength);
        using var slots = new SemaphoreSlim(1);
        using var sub = new RepositoryNotificationSubscription<IRecord>(RepositoryEventKind.Changed,
            (_, _) => ValueTask.CompletedTask, _ => true, null,
            new() { MaximumQueuedBytesPerSubscriber = 750 }, slots, _ => { }, false);
        sub.Enqueue(item);
        Assert.AreEqual(RepositorySubscriptionEndReason.QueueOverflow, await sub.Completion);
    }

    private sealed class ProbeSnapshot(int value) : ICommittedEnvelopeSnapshot<IRecord>
    {
        internal int Reads; internal bool Attached;
        public int ByteLength => 500;
        public IRecord Materialize(bool attachEnvelope = true)
        { Interlocked.Increment(ref Reads); Attached = attachEnvelope; return new Record { Value = value }; }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SixteenWritersDeliverEveryCommittedRevisionInPerEntityOrder(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { QueueCapacity = 1024 });
        var entities = (await fixture.Repository.SaveManyAsync(Enumerable.Range(0, 16).Select(i => new Record { Id = i.ToString() }))).ToArray();
        var revisions = new Dictionary<string, List<int>>();
        using var sub = fixture.Notifications.SubscribeChanged(change =>
        {
            if (!revisions.TryGetValue(change.Entity.Id, out var seen)) revisions.Add(change.Entity.Id, seen = []);
            seen.Add(change.Entity.Value);
            CollectionAssert.AreEqual(new[] { "Value" }, change.ChangedProperties.ToArray());
            return ValueTask.CompletedTask;
        }, _ => true);
        await Within(Task.WhenAll(entities.Select(async entity =>
        {
            for (var value = 1; value <= 32; value++) { entity.Value = value; await fixture.Repository.SaveAsync(entity); }
        })));
        await Within(sub.WaitForIdleAsync().AsTask());
        Assert.IsFalse(sub.IsDisposed); Assert.AreEqual(512L, sub.DeliveredCount);
        foreach (var entity in entities) CollectionAssert.AreEqual(Enumerable.Range(1, 32).ToArray(), revisions[entity.Id]);
    }

    [TestMethod]
    public async Task CancelledKeyWaitersAndReversedBatchesReleaseAllKeyEntries()
    {
        await using var fixture = new Fixture(false, enabled: false);
        var a = await fixture.Repository.SaveAsync(new Record { Id = "a" });
        var b = await fixture.Repository.SaveAsync(new Record { Id = "b" });
        var source = (ICommittedEnvelopeSnapshotSource<IRecord>)fixture.Services.GetRequiredService<IEnvelopeRepository<IRecord>>();
        var alias = (await source.CaptureCommittedAsync(a.Envelope!, 10000))!.Materialize();
        var locks = new NotificationMutationLocks<IRecord>();
        using (await locks.AcquireAsync(a, default))
        {
            using var cancel = new CancellationTokenSource();
            var waiting = locks.AcquireAsync(alias, cancel.Token).AsTask();
            cancel.Cancel(); await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiting);
        }
        Assert.AreEqual(0, locks.RetainedKeyCount);
        using (await locks.AcquireAsync(new[] { b, a }, default)) { }
        Assert.AreEqual(0, locks.RetainedKeyCount);
    }
}
