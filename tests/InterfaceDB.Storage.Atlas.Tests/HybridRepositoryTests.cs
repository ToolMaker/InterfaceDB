using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.AtlasCore.Tests;

public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    public async Task RepositoryInitializationCreatesFourEmptyFixedOwnersBeforeTheFirstEntitySave()
    {
        using var root = new ProviderTemporaryDirectory(); var options = ProviderOptions() with { StoreOptions = FixedLayoutTests.Options() };
        await using var provider = BuildProvider(new ProviderConfig(root.Path), options);
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repo.InitializeAsync();
        var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
        Assert.AreEqual(4, maintenance.Partitions.Count);
        Assert.AreEqual(4, maintenance.Status.StoreStatistics!.Value.OpenSegmentGroups);
        Assert.AreEqual(0L, maintenance.Status.StoreStatistics!.Value.Saves);
        var generation = maintenance.Status.StoreStatistics!.Value.PlacementGeneration;
        await repo.SaveManyAsync(Enumerable.Range(0, 32).Select(i => (IAtlasProviderRecord)new AtlasProviderRecord
        { Id = $"{i % 4:X3}/record-{i}", Name = "fixed", Score = i }));
        await maintenance.FlushPartitionMaintenanceAsync();
        Assert.AreEqual(4, maintenance.Partitions.Count); Assert.AreEqual(generation, maintenance.Status.StoreStatistics!.Value.PlacementGeneration);
        Assert.AreEqual(0L, maintenance.PartitionMaintenanceStatistics.Attempts);
        Assert.AreEqual(31, (await repo.QueryFirstAsync(r => r.Score == 31))!.Score);
    }

    [TestMethod]
    public async Task RepositorySplitPreservesIndexesRevisionsAndDoesNotEmitLogicalEntityChanges()
    {
        using var root = new ProviderTemporaryDirectory();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with
        { RepositoryNotifications = new(), ObjectCacheMode = StorageObjectCacheMode.MetadataOnly });
        await using var provider = BuildProvider(config, ProviderOptions() with { StoreOptions = HybridPartitionTests.Options() });
        var repo = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        var rows = await repo.SaveManyAsync(Enumerable.Range(0, 8).Select(i => (IAtlasProviderRecord)new AtlasProviderRecord
        { Id = $"ABC{(i % 2 == 0 ? 'F' : 'G')}{i:D3}", Name = "before", Score = i }));
        var first = rows.First(); var timestamp = first.Envelope!.RevisionTimestampUtcMicroseconds;
        var snapshots = (ICommittedEnvelopeSnapshotSource<IAtlasProviderRecord>)provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var baseSnapshot = (await snapshots.CaptureCommittedAsync(first.Envelope, int.MaxValue))!;
        var notifications = provider.GetRequiredService<IRepositoryNotifications<IAtlasProviderRecord>>(); var events = 0;
        using var created = notifications.SubscribeCreated(_ => { Interlocked.Increment(ref events); return ValueTask.CompletedTask; });
        using var changed = notifications.SubscribeChanged(_ => { Interlocked.Increment(ref events); return ValueTask.CompletedTask; }, _ => true);
        using var deleted = notifications.SubscribeDeleted(_ => { Interlocked.Increment(ref events); return ValueTask.CompletedTask; });
        var maintenance = provider.GetRequiredService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>();
        Assert.AreSame(maintenance, provider.GetRequiredKeyedService<IAtlasCoreRepositoryMaintenance<IAtlasProviderRecord>>(config.EnvelopeDICacheKey));
        Assert.IsTrue((await maintenance.SplitBucketAsync(first.Envelope.EnvelopeCacheKey)).Published);
        await maintenance.FlushPartitionMaintenanceAsync();
        await created.WaitForIdleAsync(); await changed.WaitForIdleAsync(); await deleted.WaitForIdleAsync();
        Assert.AreEqual(0, events);
        var current = (await repo.QueryFirstAsync(r => r.Id == first.Id))!;
        Assert.AreEqual(timestamp, current.Envelope!.RevisionTimestampUtcMicroseconds);
        current.Score = 99; await repo.SaveAsync(current);
        var stale = baseSnapshot.Materialize(); stale.Score = 88;
        await Assert.ThrowsAsync<WriteConflictException<IAtlasProviderRecord>>(() => repo.SaveAsync(stale).AsTask());
        Assert.AreEqual(first.Id, (await repo.QueryFirstAsync(r => r.Score == 99))!.Id);
        await changed.WaitForIdleAsync(); Assert.AreEqual(1, events);
    }
}
