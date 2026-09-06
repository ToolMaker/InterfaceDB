using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Maintenance;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.SegmentCore.Tests;

public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    public async Task BackgroundPolicyRunsLowTrafficStoreBelowLegacyThresholdAndDisposesCleanly()
    {
        using var root = new ProviderTemporaryDirectory();
        var activity = new MaintenanceActivityHints { Activity = MaintenanceActivity.Idle };
        var options = IndependentProviderOptions(new()
        {
            ActivitySource = activity, MinimumProbeInterval = TimeSpan.Zero,
            Policy = new CompactionPolicy(new() { Mode = CompactionMode.IdleIncremental,
                MinimumInterval = TimeSpan.Zero, RetryInterval = TimeSpan.Zero,
                MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0 })
        }) with { EnableBackgroundMaintenance = true, CheckpointInterval = TimeSpan.FromSeconds(1) };
        await using var provider = BuildProvider(new ProviderConfig(root.Path), options);
        var maintenance = provider.GetRequiredService<ICompactionMaintenance<IProviderRecord>>();
        var completed = new TaskCompletionSource<MaintenanceNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = maintenance.SubscribeMaintenance((notice, _) =>
        { if (notice.Kind == MaintenanceEventKind.CompactionCompleted) completed.TrySetResult(notice); return ValueTask.CompletedTask; });
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        var record = await repo.SaveAsync(new ProviderRecord { Id = "one", Name = new string('x', 4096) });
        record.Score = 7; await repo.SaveAsync(record);
        var notice = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(notice.RecordsCopied > 0);
        Assert.AreEqual(7, (await repo.QueryFirstAsync(row => row.Id == "one"))!.Score);
        Assert.IsNull(provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>().Status.LastMaintenanceError);
    }

    private static SegmentCoreRepositoryOptions IndependentProviderOptions(CompactionScheduleOptions? schedule = null) =>
        ProviderOptions() with
        {
            CompactionSchedule = schedule,
            CompactionOptions = new()
            {
                Mode = SegmentCoreCompactionMode.IndependentOutput, MinimumFreeSpaceReserveBytes = 0,
                Selection = new() { MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0, MaximumCandidates = 128 }
            }
        };

    [TestMethod]
    public async Task CompactionMaintenanceAliasesAreNonOwningAndPhysicalMaintenanceEmitsOnlySummary()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with
        { RepositoryNotifications = new(), ObjectCacheMode = Idb.Libraries.Abstractions.Enums.StorageObjectCacheMode.MetadataOnly });
        await using var provider = BuildProvider(config, IndependentProviderOptions());
        var maintenance = provider.GetRequiredService<ICompactionMaintenance<IProviderRecord>>();
        Assert.AreSame(maintenance, provider.GetRequiredKeyedService<ICompactionMaintenance<IProviderRecord>>(config.EnvelopeDICacheKey));
        Assert.IsFalse(maintenance is IDisposable || maintenance is IAsyncDisposable);
        var repository = provider.GetRequiredService<IRepository<IProviderRecord>>();
        var record = await repository.SaveAsync(new ProviderRecord { Id = "one", Name = new string('x', 8000), Score = 1 });
        record.Score = 2; await repository.SaveAsync(record);
        var revision = record.Envelope!.RevisionTimestampUtcMicroseconds;
        var logical = provider.GetRequiredService<IRepositoryNotifications<IProviderRecord>>();
        var count = 0;
        using var created = logical.SubscribeCreated(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
        using var changed = logical.SubscribeChanged(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; }, _ => true);
        using var deleted = logical.SubscribeDeleted(_ => { Interlocked.Increment(ref count); return ValueTask.CompletedTask; });
        var notices = new ConcurrentQueue<MaintenanceNotification>();
        using var subscription = maintenance.SubscribeMaintenance((notice, _) =>
        { notices.Enqueue(notice); return ValueTask.CompletedTask; });
        var estimate = await maintenance.EstimateCompactionAsync();
        Assert.IsTrue(estimate.ReclaimableBytes > 0);
        var direct = provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();
        var result = await direct.CompactAsync();
        await subscription.WaitForIdleAsync();
        Assert.AreEqual(1, notices.Count); Assert.AreEqual(MaintenanceEventKind.CompactionCompleted, notices.Single().Kind);
        Assert.AreEqual(result.BytesCopied, notices.Single().BytesCopied);
        Assert.IsTrue(result.BytesCopied > 0);
        var scheduled = await maintenance.RunCompactionAsync(manual: true);
        Assert.IsTrue(scheduled.Decision.ShouldRun);
        await subscription.WaitForIdleAsync(); Assert.AreEqual(2, notices.Count);
        await created.WaitForIdleAsync(); await changed.WaitForIdleAsync(); await deleted.WaitForIdleAsync();
        Assert.AreEqual(0, count);
        var loaded = await repository.QueryFirstAsync(item => item.Id == "one");
        Assert.AreEqual(2, loaded!.Score); Assert.AreEqual(revision, loaded.Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.IsTrue((await direct.VerifyIntegrityAsync()).IsValid);
    }

    [TestMethod]
    public async Task CompactionPolicyDefersBusyAndRunsIdleWithoutLegacyMutationThreshold()
    {
        using var root = new ProviderTemporaryDirectory();
        var activity = new MaintenanceActivityHints { Activity = MaintenanceActivity.Busy };
        var options = IndependentProviderOptions(new()
        {
            ActivitySource = activity, MinimumProbeInterval = TimeSpan.Zero,
            Policy = new CompactionPolicy(new() { Mode = CompactionMode.IdleIncremental,
                MinimumInterval = TimeSpan.Zero, RetryInterval = TimeSpan.Zero,
                MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0 })
        });
        await using var provider = BuildProvider(new ProviderConfig(root.Path), options);
        var repo = provider.GetRequiredService<IRepository<IProviderRecord>>();
        var row = await repo.SaveAsync(new ProviderRecord { Id = "one", Name = new string('a', 4096) });
        row.Score++; await repo.SaveAsync(row);
        var maintenance = provider.GetRequiredService<ICompactionMaintenance<IProviderRecord>>();
        Assert.AreEqual(CompactionDeferReason.ForegroundBusy, (await maintenance.RunCompactionAsync()).Decision.Reason);
        activity.Activity = MaintenanceActivity.Idle;
        var result = await maintenance.RunCompactionAsync();
        Assert.IsTrue(result.Decision.ShouldRun); Assert.IsTrue(result.Work!.Value.RecordsCopied > 0);
    }

    [TestMethod]
    public async Task DisposalDetachesMaintenanceSubscriberAndDoesNotChangePersistedEntity()
    {
        using var root = new ProviderTemporaryDirectory(); var config = new ProviderConfig(root.Path);
        var provider = BuildProvider(config, IndependentProviderOptions());
        var maintenance = provider.GetRequiredService<ICompactionMaintenance<IProviderRecord>>();
        using var subscription = maintenance.SubscribeMaintenance((_, _) => ValueTask.CompletedTask);
        await provider.GetRequiredService<IRepository<IProviderRecord>>().SaveAsync(new ProviderRecord { Id = "saved", Score = 42 });
        await provider.DisposeAsync();
        Assert.AreEqual(MaintenanceSubscriptionEndReason.PublisherDisposed, await subscription.Completion);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => maintenance.RunCompactionAsync(true).AsTask());
        await using var reopened = BuildProvider(config, IndependentProviderOptions());
        Assert.AreEqual(42, (await reopened.GetRequiredService<IRepository<IProviderRecord>>().QueryFirstAsync(x => x.Id == "saved"))!.Score);
    }
}
