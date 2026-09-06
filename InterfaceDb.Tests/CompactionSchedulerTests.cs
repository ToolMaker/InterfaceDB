using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Implementation.Maintenance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public class CompactionSchedulerTests
{
    [TestMethod]
    public async Task ExecutionDeferralIsReportedAsDeferralIncludingDiscardedCopyCosts()
    {
        var work = new CompactionWorkResult(3, 8192, 0, IsPartial: true)
        { Deferred = true, ReasonCode = "foreground-contention" };
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), _ => ValueTask.FromResult(work));
        var seen = new List<MaintenanceNotification>();
        using var sub = scheduler.SubscribeMaintenance((n, _) => { seen.Add(n); return ValueTask.CompletedTask; });
        Assert.AreEqual(work, (await scheduler.RunAsync(true)).Work);
        await scheduler.ObserveAsync(_ => ValueTask.FromResult(work), static value => value);
        await sub.WaitForIdleAsync(); Assert.AreEqual(2, seen.Count);
        Assert.IsTrue(seen.All(n => n.Kind == MaintenanceEventKind.CompactionDeferred && n.BytesCopied == 8192
            && n.BytesReclaimed == 0 && n.ReasonCode == "foreground-contention"));
    }

    [TestMethod]
    public async Task DirectObservationPreservesNativeResultAndDoesNotProbeOrRunPolicy()
    {
        using var scheduler = new CompactionScheduler(_ => throw new Exception("No probe"), _ => throw new Exception("No scheduled operation"));
        var seen = new List<MaintenanceNotification>();
        using var sub = scheduler.SubscribeMaintenance((n, _) => { seen.Add(n); return ValueTask.CompletedTask; });
        var result = await scheduler.ObserveAsync(_ => ValueTask.FromResult("native result"), _ => Result);
        Assert.AreEqual("native result", result);
        await sub.WaitForIdleAsync(); Assert.AreEqual(1, seen.Count);
        Assert.AreEqual(Result.BytesCopied, seen[0].BytesCopied);
        Assert.AreEqual("committed", await scheduler.ObserveAsync(_ => ValueTask.FromResult("committed"), _ => throw new Exception("mapper")));
        Assert.AreEqual(1L, scheduler.ObservationFailureCount);
    }

    [TestMethod]
    public async Task AsyncDisposalCancelsAndDrainsAdmittedDirectWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), _ => ValueTask.FromResult(Result));
        var operation = scheduler.ObserveAsync(async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return 1;
        }, _ => Result).AsTask();
        await entered.Task;
        Assert.AreEqual(CompactionDeferReason.AlreadyRunning, (await scheduler.RunAsync(true)).Decision.Reason);
        var disposal = scheduler.DisposeAsync().AsTask();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsFalse(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => scheduler.ObserveAsync(_ => ValueTask.FromResult(1), _ => Result).AsTask());
    }

    private static CompactionEstimate Useful => new(100_000_000, 50_000_000);
    private static CompactionWorkResult Result => new(10, 500, 1000, 200);
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
    }

    [TestMethod]
    public async Task SchedulerDoesNotStartWorkUntilAskedAndHonorsMonotonicIntervals()
    {
        var clock = new Clock(); int probes = 0, runs = 0;
        using var scheduler = new CompactionScheduler(_ => { probes++; return ValueTask.FromResult(Useful); },
            _ => { runs++; return ValueTask.FromResult(Result); }, new() { TimeProvider = clock });
        Assert.AreEqual(0, probes); Assert.AreEqual(0, runs);
        var startup = await scheduler.RunAsync();
        Assert.AreEqual(CompactionDeferReason.MinimumInterval, startup.Decision.Reason);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(CompactionDeferReason.MinimumInterval, (await scheduler.RunAsync()).Decision.Reason);
        Assert.AreEqual(1, probes);
        clock.Advance(TimeSpan.FromMinutes(1));
        var completed = await scheduler.RunAsync();
        Assert.AreEqual(Result, completed.Work); Assert.AreEqual(1, runs);
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.AreEqual(CompactionDeferReason.MinimumInterval, (await scheduler.RunAsync()).Decision.Reason);
        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    public async Task ConcurrentRequestsCoalesceWithoutAdditionalProbeOrExecution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), async _ =>
        { Interlocked.Increment(ref runs); entered.TrySetResult(); await release.Task; return Result; });
        var first = scheduler.RunAsync(manual: true).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => scheduler.RunAsync(manual: true).AsTask()));
            Assert.IsTrue(results.All(r => r.Decision.Reason == CompactionDeferReason.AlreadyRunning));
            Assert.AreEqual(1, runs);
        }
        finally { release.TrySetResult(); }
        Assert.IsTrue((await first).Decision.ShouldRun);
    }

    [TestMethod]
    public async Task FailedProbeIsThrottledAndFailureSummaryContainsNoExceptionText()
    {
        var clock = new Clock(); var probes = 0;
        using var scheduler = new CompactionScheduler(_ => { probes++; throw new IOException("SECRET path/token"); },
            _ => throw new Exception("Must not execute"), new() { TimeProvider = clock });
        var seen = new List<MaintenanceNotification>();
        using var subscription = scheduler.SubscribeMaintenance((n, _) => { seen.Add(n); return ValueTask.CompletedTask; });
        await Assert.ThrowsExactlyAsync<IOException>(() => scheduler.RunAsync().AsTask());
        await subscription.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, seen.Count);
        Assert.AreEqual(MaintenanceEventKind.CompactionFailed, seen[0].Kind);
        Assert.AreEqual("maintenance-failed", seen[0].ReasonCode);
        Assert.AreNotEqual(Guid.Empty, seen[0].OperationId);
        Assert.AreEqual(CompactionDeferReason.MinimumInterval, (await scheduler.RunAsync()).Decision.Reason);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public async Task NotificationsArriveAfterExecutionReturnsAndDoNotUndoSuccessfulResult()
    {
        var inside = false; var enteredCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), async _ =>
        { inside = true; await Task.Yield(); inside = false; return Result; });
        using var sub = scheduler.SubscribeMaintenance((n, _) =>
        {
            Assert.IsFalse(inside); Assert.AreEqual(Result.BytesReclaimed, n.BytesReclaimed);
            enteredCallback.TrySetResult(); throw new InvalidOperationException("consumer failure");
        });
        Assert.AreEqual(Result, (await scheduler.RunAsync(manual: true)).Work);
        await enteredCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MaintenanceSubscriptionEndReason.CallbackFailed, await sub.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task CancellationSummaryIsDistinctAndGateReopensAfterCancelledWork()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), async token =>
        {
            if (first) { first = false; entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Result;
        });
        var seen = new List<MaintenanceNotification>();
        using var sub = scheduler.SubscribeMaintenance((n, _) => { seen.Add(n); return ValueTask.CompletedTask; });
        var pending = scheduler.RunAsync(true, stop.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await sub.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(MaintenanceEventKind.CompactionCancelled, seen.Single().Kind);
        Assert.AreEqual(Result, (await scheduler.RunAsync(true)).Work);
    }

    [TestMethod]
    public async Task CancellationAfterEngineSuccessDoesNotReportCommittedWorkAsCancelled()
    {
        using var stop = new CancellationTokenSource();
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), _ =>
        { stop.Cancel(); return ValueTask.FromResult(Result); });
        Assert.AreEqual(Result, (await scheduler.RunAsync(true, stop.Token)).Work);
    }

    [TestMethod]
    public async Task InvalidAdapterSummaryDoesNotChangeAlreadyCompletedWorkIntoFailure()
    {
        var invalid = Result with { BytesCopied = -1 };
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), _ => ValueTask.FromResult(invalid));
        using var sub = scheduler.SubscribeMaintenance((_, _) => throw new Exception("Invalid summary must not be delivered."));
        Assert.AreEqual(invalid, (await scheduler.RunAsync(true)).Work);
        Assert.AreEqual(1L, scheduler.ObservationFailureCount);
        Assert.AreEqual(0L, sub.DeliveredCount);
    }

    [TestMethod]
    public async Task ActivityHintsControlIdlePolicyWithoutChangingSafety()
    {
        var hints = new MaintenanceActivityHints(); var clock = new Clock();
        var options = new CompactionScheduleOptions { ActivitySource = hints, TimeProvider = clock,
            MinimumProbeInterval = TimeSpan.Zero, Policy = new CompactionPolicy(new()
            { Mode = CompactionMode.IdleIncremental, MinimumInterval = TimeSpan.Zero, RetryInterval = TimeSpan.Zero }) };
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), _ => ValueTask.FromResult(Result), options);
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy, (await scheduler.RunAsync()).Decision.Reason);
        hints.Activity = MaintenanceActivity.Idle;
        Assert.AreEqual(Result, (await scheduler.RunAsync()).Work);
        hints.Activity = MaintenanceActivity.Busy; hints.MaintenanceWindowOpen = true;
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy, (await scheduler.RunAsync()).Decision.Reason);
    }

    [TestMethod]
    public async Task DisposalCancelsAdmittedWorkAndRejectsNewRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new CompactionScheduler(_ => ValueTask.FromResult(Useful), async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Result; });
        var pending = scheduler.RunAsync(true).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); scheduler.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => scheduler.RunAsync(true).AsTask());
    }
}
