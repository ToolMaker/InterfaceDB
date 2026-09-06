using System.Diagnostics;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public class CompactionMaintenanceTests
{
    private static CompactionPolicyContext Useful => new(100_000_000, 50_000_000,
        TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), MaintenanceActivity.Idle);
    private static MaintenanceNotification Completed => new(Guid.NewGuid(), MaintenanceEventKind.CompactionCompleted,
        TimeSpan.FromMilliseconds(1), RecordsCopied: 10, BytesCopied: 1000, BytesReclaimed: 2000);

    [TestMethod]
    public void PolicyRunsForUsefulLowTrafficButNotCleanStore()
    {
        var policy = new CompactionPolicy();
        Assert.IsTrue(policy.Evaluate(Useful).ShouldRun); // No mutation threshold required.
        Assert.AreEqual(CompactionDeferReason.InsufficientBenefit,
            policy.Evaluate(Useful with { ReclaimableBytes = 0 }).Reason);
        Assert.AreEqual(CompactionDeferReason.MinimumInterval,
            policy.Evaluate(Useful with { SinceLastAttempt = TimeSpan.Zero }).Reason);
        Assert.AreEqual(CompactionDeferReason.MinimumInterval,
            policy.Evaluate(Useful with { SinceLastCompletion = TimeSpan.Zero }).Reason);
    }

    [TestMethod]
    public void PolicyDistinguishesModesAndExplicitActivity()
    {
        var busy = Useful with { Activity = MaintenanceActivity.Busy };
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy, new CompactionPolicy().Evaluate(busy).Reason);
        Assert.IsTrue(new CompactionPolicy(new() { Mode = CompactionMode.SpaceFirst }).Evaluate(busy).ShouldRun);
        var idle = new CompactionPolicy(new() { Mode = CompactionMode.IdleIncremental });
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy,
            idle.Evaluate(Useful with { Activity = MaintenanceActivity.Unknown }).Reason);
        Assert.IsTrue(idle.Evaluate(Useful).ShouldRun);
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy,
            idle.Evaluate(Useful with { ForegroundOutstanding = 1 }).Reason);
        var manual = new CompactionPolicy(new() { Mode = CompactionMode.ManualWindowed });
        Assert.AreEqual(CompactionDeferReason.ManualOnly, manual.Evaluate(Useful).Reason);
        Assert.IsTrue(manual.Evaluate(Useful with { MaintenanceWindowOpen = true }).ShouldRun);
    }

    [TestMethod]
    public void ManualBypassesSchedulingButNeverWorkingSpace()
    {
        var policy = new CompactionPolicy(new() { Mode = CompactionMode.ManualWindowed });
        var request = Useful with { ManualRequest = true, ReclaimableBytes = 0, Activity = MaintenanceActivity.Busy,
            SinceLastCompletion = TimeSpan.Zero, SinceLastAttempt = TimeSpan.Zero };
        Assert.IsTrue(policy.Evaluate(request).ShouldRun);
        Assert.AreEqual(CompactionDeferReason.InsufficientWorkingSpace,
            policy.Evaluate(request with { HasWorkingSpace = false }).Reason);
    }

    [TestMethod]
    public void AntiStarvationIsExplicitAndDoesNotOverrideSafetyOrBenefit()
    {
        var context = Useful with { Activity = MaintenanceActivity.Busy, SinceLastCompletion = TimeSpan.FromDays(1) };
        Assert.IsFalse(new CompactionPolicy().Evaluate(context).ShouldRun);
        var policy = new CompactionPolicy(new() { AllowBusyAfterMaximumDeferral = true });
        Assert.IsTrue(policy.Evaluate(context).ShouldRun);
        Assert.IsFalse(policy.Evaluate(context with { HasWorkingSpace = false }).ShouldRun);
        Assert.IsFalse(policy.Evaluate(context with { ReclaimableBytes = 0 }).ShouldRun);
    }

    [TestMethod]
    public void InvalidPolicyInputsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CompactionPolicy(new() { MinimumReclaimableRatio = double.NaN }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CompactionPolicy(new() { RetryInterval = TimeSpan.FromSeconds(-1) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CompactionPolicy().Evaluate(Useful with { ReclaimableBytes = long.MaxValue }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CompactionPolicy().Evaluate(Useful with { ForegroundOutstanding = -1 }));
    }

    [TestMethod]
    public void CandidateSelectionRespectsProtectionSealingBenefitAndTotalBudget()
    {
        CompactionCandidate[] candidates = [
            new("active", 1000, 0, 0, false), new("pinned", 1000, 100, 900, true),
            new("a", 1000, 50, 50, true), new("b", 1000, 250, 0, true),
            new("c", 1000, 500, 0, true), new("empty", 1000, 0, 0, true)];
        var result = CompactionCandidateSelector.Select(candidates, new()
        { MaximumCandidates = 8, MaximumCopyBytes = 350, MinimumReclaimableBytes = 100 });
        CollectionAssert.AreEqual(new[] { "empty", "a", "b" }, result.Select(x => x.Id).ToArray());
        Assert.AreEqual(350L, result.Sum(x => x.RequiredBytes));
    }

    [TestMethod]
    public void CandidateSelectionRejectsDuplicateInvalidAndUnboundedEnumeration()
    {
        var candidate = new CompactionCandidate("a", 100, 10, 10, true);
        Assert.ThrowsExactly<ArgumentException>(() => CompactionCandidateSelector.Select([candidate, candidate]));
        Assert.ThrowsExactly<ArgumentException>(() => CompactionCandidateSelector.Select([candidate with { ProtectedObsoleteBytes = long.MaxValue }]));
        Assert.ThrowsExactly<InvalidOperationException>(() => CompactionCandidateSelector.Select(
            Enumerable.Range(0, 3).Select(i => candidate with { Id = i.ToString() }),
            new() { MaximumCandidates = 1, MaximumCandidatesExamined = 2 }));
    }

    [TestMethod]
    public async Task SharedBudgetLimitsConcurrentWorkAndCountsBytes()
    {
        var budget = new MaintenanceBudget(new() { MaximumConcurrentUnits = 2 });
        var first = await budget.AcquireAsync(10);
        var second = await budget.AcquireAsync(20);
        var waiting = budget.AcquireAsync(30).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(2, budget.Statistics.ActiveUnits);
        Assert.AreEqual(1, budget.Statistics.WaitingUnits);
        first.Dispose(); first.Dispose();
        using (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))
            Assert.AreEqual(2, budget.Statistics.ActiveUnits);
        second.Dispose();
        Assert.AreEqual(0, budget.Statistics.ActiveUnits);
        Assert.AreEqual(3L, budget.Statistics.AdmittedUnits);
        Assert.AreEqual(60L, budget.Statistics.AdmittedIoBytes);
    }

    [TestMethod]
    public async Task BudgetCancellationDoesNotLeakSlotsOrAcceptOversizedWork()
    {
        var budget = new MaintenanceBudget(new() { MaximumWorkUnitBytes = 100 });
        var first = await budget.AcquireAsync(100);
        using var stop = new CancellationTokenSource();
        var waiting = budget.AcquireAsync(1, stop.Token).AsTask();
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        Assert.AreEqual(0, budget.Statistics.WaitingUnits);
        first.Dispose();
        using var next = await budget.AcquireAsync(100);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => budget.AcquireAsync(101).AsTask());
        Assert.AreEqual(2L, budget.Statistics.AdmittedUnits);
    }

    [TestMethod]
    public async Task ByteRateThrottleWaitsAndCancelsWithoutStrandedReservations()
    {
        var budget = new MaintenanceBudget(new() { MaximumWorkUnitBytes = 100, BytesPerSecond = 1000 });
        using (await budget.AcquireAsync(100)) { }
        var timer = Stopwatch.StartNew();
        using (await budget.AcquireAsync(100).AsTask().WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.IsTrue(timer.Elapsed >= TimeSpan.FromMilliseconds(40), "Second burst must wait for tokens.");
        var slow = new MaintenanceBudget(new() { MaximumWorkUnitBytes = 100, BytesPerSecond = 1 });
        using (await slow.AcquireAsync(100)) { }
        using var stop = new CancellationTokenSource();
        var waiting = slow.AcquireAsync(100, stop.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        using var next = await slow.AcquireAsync(0).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, slow.Statistics.ActiveUnits);
    }

    private sealed class RefillClock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref Ticks);
    }

    [TestMethod]
    public async Task SmallUnitsCannotStealRefillsFromAnAdmittedLargeWaiter()
    {
        var clock = new RefillClock();
        var budget = new MaintenanceBudget(new()
        { MaximumConcurrentUnits = 2, MaximumWorkUnitBytes = 100, BytesPerSecond = 1, TimeProvider = clock });
        using (await budget.AcquireAsync(100)) { }
        using var stop = new CancellationTokenSource();
        var large = budget.AcquireAsync(100, stop.Token).AsTask();
        Assert.IsFalse(large.IsCompleted);
        Interlocked.Add(ref clock.Ticks, TimeSpan.FromSeconds(99).Ticks);
        var small = budget.AcquireAsync(1).AsTask();
        Assert.IsFalse(small.IsCompleted, "The earlier large unit owns refill admission.");
        stop.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => large);
        using (await small.WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.AreEqual(0, budget.Statistics.ActiveUnits);
        Assert.AreEqual(101L, budget.Statistics.AdmittedIoBytes);
    }

    [TestMethod]
    public void EmptyHubAndPolicyEvaluationAllocateNothingPerCall()
    {
        using var hub = new MaintenanceNotificationHub();
        var notification = Completed;
        var policy = new CompactionPolicy(); var context = Useful;
        for (var i = 0; i < 1000; i++) { hub.Publish(notification); _ = policy.Evaluate(context); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) { hub.Publish(notification); _ = policy.Evaluate(context); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0L, allocated);
        Assert.IsFalse(hub.HasSubscribers);
    }

    [TestMethod]
    public async Task SummaryDispatchIsOrderedAndDetachedOnCancellation()
    {
        using var hub = new MaintenanceNotificationHub();
        using var stop = new CancellationTokenSource();
        var seen = new List<long>();
        using var sub = hub.SubscribeMaintenance((item, _) => { seen.Add(item.RecordsCopied); return ValueTask.CompletedTask; }, stop.Token);
        for (var i = 0; i < 10; i++) hub.Publish(Completed with { RecordsCopied = i });
        await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(Enumerable.Range(0, 10).Select(x => (long)x).ToArray(), seen);
        stop.Cancel();
        Assert.AreEqual(MaintenanceSubscriptionEndReason.Cancelled, await sub.Completion);
        Assert.IsFalse(hub.HasSubscribers);
    }

    [TestMethod]
    public async Task OverflowDetachesSlowSubscriberAndDoesNotBlockPublisher()
    {
        using var hub = new MaintenanceNotificationHub(new() { QueueCapacityPerSubscriber = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = hub.SubscribeMaintenance(async (_, _) => { entered.TrySetResult(); await release.Task; });
        try
        {
            hub.Publish(Completed); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            hub.Publish(Completed); hub.Publish(Completed);
            Assert.AreEqual(MaintenanceSubscriptionEndReason.QueueOverflow, await sub.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(hub.HasSubscribers);
            Assert.IsFalse(sub.WaitForIdleAsync().IsCompleted); // Detachment is not forced callback termination.
        }
        finally { release.TrySetResult(); }
        await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task HandlerFailureAndPublisherDisposalHaveExplicitCompletion()
    {
        using var hub = new MaintenanceNotificationHub();
        using var failing = hub.SubscribeMaintenance((_, _) => throw new InvalidOperationException());
        hub.Publish(Completed);
        Assert.AreEqual(MaintenanceSubscriptionEndReason.CallbackFailed, await failing.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        using var other = hub.SubscribeMaintenance((_, _) => ValueTask.CompletedTask);
        hub.Dispose();
        Assert.AreEqual(MaintenanceSubscriptionEndReason.PublisherDisposed, await other.Completion);
        Assert.ThrowsExactly<ObjectDisposedException>(() => hub.SubscribeMaintenance((_, _) => ValueTask.CompletedTask));
    }

    [TestMethod]
    public async Task SubscriberGeneratedMaintenanceDoesNotLoopBack()
    {
        using var hub = new MaintenanceNotificationHub();
        var total = 0;
        using var sub = hub.SubscribeMaintenance((notice, _) =>
        {
            if (Interlocked.Increment(ref total) < 10) hub.Publish(notice);
            return ValueTask.CompletedTask;
        });
        hub.Publish(Completed);
        await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, total);
    }

    [TestMethod]
    public async Task SubscriberCannotAwaitItselfAndCancelledRegistrationIsDetached()
    {
        using var hub = new MaintenanceNotificationHub();
        IMaintenanceSubscription? sub = null;
        using (sub = hub.SubscribeMaintenance((_, _) =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => sub!.WaitForIdleAsync());
            return ValueTask.CompletedTask;
        }))
        {
            hub.Publish(Completed);
            await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1L, sub.DeliveredCount);
        }
        using var cancelled = hub.SubscribeMaintenance((_, _) => throw new Exception("Must not run"), new CancellationToken(true));
        Assert.IsTrue(cancelled.IsDisposed);
        Assert.AreEqual(MaintenanceSubscriptionEndReason.Cancelled, await cancelled.Completion);
        Assert.IsFalse(hub.HasSubscribers);
    }
}
