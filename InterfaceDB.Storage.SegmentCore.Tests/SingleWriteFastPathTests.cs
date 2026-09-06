using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore.ObjectLog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.SegmentCore.Tests;

public sealed partial class SegmentCoreProviderTests
{
    [TestMethod]
    [DataRow(SegmentCoreTransactionProtocol.ParticipantCommit)]
    [DataRow(SegmentCoreTransactionProtocol.MutationWriteAheadLog)]
    public async Task SingleAndBatchWritesRecoverTogetherWithoutCheckpoint(SegmentCoreTransactionProtocol protocol)
    {
        using var root = new ProviderTemporaryDirectory();
        var profile = ProviderOptions();
        var options = profile with { CheckpointOnDispose = false,
            StoreOptions = profile.StoreOptions with { TransactionProtocol = protocol } };
        var policy = StoragePolicy.Default with { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            DurabilityMode = StorageDurabilityMode.Durable, RaiseNotifications = false, HydrateOnInitialize = false };
        var config = new ProviderConfig(root.Path, policy);
        var revisions = new Dictionary<string, long>();
        await using (var services = BuildProvider(config, options))
        {
            var repository = services.GetRequiredService<IRepository<IProviderRecord>>();
            var maintenance = services.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>();
            var singles = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
                await repository.SaveAsync(new ProviderRecord { Id = $"single-{i}", Name = "initial", Score = i }))));
            Assert.AreEqual(0L, maintenance.Status.StoreStatistics!.Value.CommittedTransactions);
            var batch = await repository.SaveManyAsync(CreateRecords(64));
            Assert.AreEqual(1L, maintenance.Status.StoreStatistics!.Value.CommittedTransactions);
            var mixed = singles.Concat(batch).ToArray();
            for (var i = 0; i < mixed.Length; i++)
            {
                if (i % 3 == 0) { Assert.IsTrue(await repository.DeleteAsync(mixed[i])); continue; }
                mixed[i].Name = "updated"; mixed[i].Score += 1000;
                var saved = await repository.SaveAsync(mixed[i]);
                revisions[saved.Id] = saved.Envelope!.RevisionTimestampUtcMicroseconds;
            }
            Assert.AreEqual(1L, maintenance.Status.StoreStatistics!.Value.CommittedTransactions);
        }
        await using var reopened = BuildProvider(config, options);
        var reader = reopened.GetRequiredService<IRepository<IProviderRecord>>();
        await reader.InitializeAsync();
        var count = 0;
        await foreach (var row in reader.SearchManyAsync(x => x.Name == "updated"))
        {
            Assert.IsTrue(row.Score >= 1000);
            Assert.AreEqual(revisions[row.Id], row.Envelope!.RevisionTimestampUtcMicroseconds);
            count++;
        }
        Assert.AreEqual(revisions.Count, count);
        var verify = await reopened.GetRequiredService<ISegmentCoreRepositoryMaintenance<IProviderRecord>>().VerifyIntegrityAsync();
        Assert.AreEqual(count, verify.EntriesChecked);
        // A reused recovery cursor must remain writable and produce a strictly newer version.
        var latest = await reader.SearchFirstAsync(x => x.Name == "updated");
        var before = latest!.Envelope!.RevisionTimestampUtcMicroseconds;
        latest.Score++;
        Assert.IsTrue((await reader.SaveAsync(latest)).Envelope!.RevisionTimestampUtcMicroseconds > before);
    }

    [TestMethod]
    public async Task SingleSaveIsInvisibleAndUnacknowledgedUntilDurabilityCompletes()
    {
        using var root = new ProviderTemporaryDirectory();
        var disk = new GatedSingleFlush();
        var original = ProviderOptions();
        var options = original with { CheckpointOnDispose = false,
            StoreOptions = original.StoreOptions with { DurableFileSystem = disk } };
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with
        { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly, DurabilityMode = StorageDurabilityMode.Durable,
            HydrateOnInitialize = false, RaiseNotifications = false });
        await using var services = BuildProvider(config, options);
        var repository = services.GetRequiredService<IRepository<IProviderRecord>>();
        await repository.InitializeAsync();
        var saved = await repository.SaveAsync(new ProviderRecord { Id = "one", Name = "before", Score = 1 });
        saved.Name = "after";
        disk.Armed = true;
        var pending = repository.SaveAsync(saved).AsTask();
        try
        {
            await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pending.IsCompleted);
            Assert.AreEqual("before", (await repository.SearchFirstAsync(x => x.Name == "before"))!.Name);
        }
        finally { disk.Release.Set(); }
        Assert.AreEqual("after", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Name);
        Assert.IsNull(await repository.SearchFirstAsync(x => x.Name == "before"));
        Assert.IsNotNull(await repository.SearchFirstAsync(x => x.Name == "after"));
    }

    private sealed class GatedSingleFlush : IDurableFileSystem
    {
        public volatile bool Armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public string DirectoryFlushStrategy => "single-save gate";
        public void FlushFile(SafeFileHandle handle)
        {
            if (Armed) { Entered.TrySetResult(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            PlatformDurableFileSystem.Instance.FlushFile(handle);
        }
        public void FlushDirectory(string path) => PlatformDurableFileSystem.Instance.FlushDirectory(path);
        public void PublishFile(string from, string to) => PlatformDurableFileSystem.Instance.PublishFile(from, to);
        public void ReplaceFile(string from, string to) => PlatformDurableFileSystem.Instance.ReplaceFile(from, to);
    }
}
