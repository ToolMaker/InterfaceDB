// Regression copied from experiments/Atlas.Maintenance.Tests/AtlasProviderMaintenanceTests.cs; only provider configuration is adapted for the promoted hybrid.
using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Storage.AtlasCore.Tests;

public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    [DataRow(0)]

    public async Task NativeMaintenanceUsesNonOwningAliasesAndNeverEmitsEntityChanges(int variant)
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with { RepositoryNotifications = new() });
        var activity = new MaintenanceActivityHints { Activity = MaintenanceActivity.Idle };
        var options = ProviderOptions() with
        {
            ConcurrentCompaction = new() { MinimumFreeSpaceReserveBytes = 0 },
            CompactionSchedule = new() { ActivitySource = activity, MinimumProbeInterval = TimeSpan.Zero,
                Policy = new CompactionPolicy(new() { Mode = CompactionMode.IdleIncremental,
                    MinimumInterval = TimeSpan.Zero, RetryInterval = TimeSpan.Zero, MinimumReclaimableBytes = 1, MinimumReclaimableRatio = 0 }) }
        };
        var provider = BuildProvider(config, options);
        try
        {
            var maintenance = provider.GetRequiredService<ICompactionMaintenance<IAtlasProviderRecord>>();
            Assert.AreSame(maintenance, provider.GetRequiredKeyedService<ICompactionMaintenance<IAtlasProviderRecord>>(config.EnvelopeDICacheKey));
            Assert.IsFalse(maintenance is IDisposable || maintenance is IAsyncDisposable);
            var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
            var row = await repo.SaveAsync(new AtlasProviderRecord { Id = "aaa-one", Name = new string('x', 4096), Score = 1 });
            row.Score = 2; await repo.SaveAsync(row); var timestamp = row.Envelope!.RevisionTimestampUtcMicroseconds;
            var notifications = provider.GetRequiredService<IRepositoryNotifications<IAtlasProviderRecord>>();
            var entityEvents = 0;
            using var created = notifications.SubscribeCreated(_ => { Interlocked.Increment(ref entityEvents); return ValueTask.CompletedTask; });
            using var changed = notifications.SubscribeChanged(_ => { Interlocked.Increment(ref entityEvents); return ValueTask.CompletedTask; }, _ => true);
            using var deleted = notifications.SubscribeDeleted(_ => { Interlocked.Increment(ref entityEvents); return ValueTask.CompletedTask; });
            var notices = new ConcurrentQueue<MaintenanceNotification>();
            using var sub = maintenance.SubscribeMaintenance((n, _) => { notices.Enqueue(n); return ValueTask.CompletedTask; });
            Assert.IsTrue((await maintenance.EstimateCompactionAsync()).ReclaimableBytes > 0);
            var result = await maintenance.RunCompactionAsync();
            Assert.IsTrue(result.Decision.ShouldRun); Assert.IsFalse(result.Work!.Value.Deferred);
            var direct = provider.GetRequiredService<IAtlasConcurrentRepositoryMaintenance<IAtlasProviderRecord>>();
            Assert.IsTrue((await direct.CompactConcurrentAsync()).Published);
            var legacy = await provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>().CompactAsync();
            Assert.IsTrue(legacy.BytesCopied > 0);
            await sub.WaitForIdleAsync(); Assert.AreEqual(3, notices.Count);
            Assert.IsTrue(notices.All(n => n.Kind == MaintenanceEventKind.CompactionCompleted && n.BytesCopied > 0));
            await created.WaitForIdleAsync(); await changed.WaitForIdleAsync(); await deleted.WaitForIdleAsync();
            Assert.AreEqual(0, entityEvents);
            var current = await repo.QueryFirstAsync(item => item.Id == "aaa-one");
            Assert.AreEqual(2, current!.Score); Assert.AreEqual(timestamp, current.Envelope!.RevisionTimestampUtcMicroseconds);
            await provider.DisposeAsync();
            Assert.AreEqual(MaintenanceSubscriptionEndReason.PublisherDisposed, await sub.Completion);
        }
        finally { await provider.DisposeAsync(); }
    }
}
