using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RevisionCompatibilityTests
{
    private static readonly SegmentCoreRepositoryOptions Options = new() { EnableBackgroundMaintenance = false, CheckpointOnDispose = false };
    private static string StoreRoot(string root) => Path.Combine(root, Options.DirectoryName, "1.0.0");
    private static ServiceProvider Open(string root, bool segment)
    {
        var policy = StoragePolicy.Default with { ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
            RaiseNotifications = false, WatchStorageChanges = false, HydrateOnInitialize = false, DurabilityMode = StorageDurabilityMode.Durable };
        var config = new Config(root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy);
        var services = new ServiceCollection();
        if (segment) services.AddSegmentCoreRepository(config, Options, storageIdentityFactory: new Identity(root));
        else services.AddFileRepository(config, storageIdentityFactory: new Identity(root));
        return services.BuildServiceProvider();
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task LegacyZeroIsUpgradedOnceAndOtherZeroCopiesConflict(bool segment)
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-legacy-revision-" + Guid.NewGuid());
        try
        {
            var envelope = new FileObjectEnvelope<IRecord> { EnvelopeCacheKey = "legacy", VersionTag = "1.0.0",
                ObjectInstance = new Record { Id = "legacy", Value = 1 }, ExtensionMetadata = [4, 5, 6] };
            var bytes = new BinaryObjectSerializer<IRecord>().Serialize(envelope);
            var legacy = "IDB5"u8.ToArray().Concat(bytes.AsSpan(12).ToArray()).ToArray();
            if (segment)
            {
                await using var store = await SegmentCoreStore.OpenAsync(StoreRoot(root), Options.StoreOptions);
                await store.SaveAsync("legacy", legacy);
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(root, "1.0.0"));
                await File.WriteAllBytesAsync(Path.Combine(root, "1.0.0", "legacy.mpk"), legacy);
            }
            await using var provider = Open(root, segment);
            var repo = provider.GetRequiredService<IRepository<IRecord>>();
            await repo.InitializeAsync();
            var first = (await repo.QueryFirstAsync(x => x.Id == "legacy"))!;
            var second = (await repo.QueryFirstAsync(x => x.Id == "legacy"))!;
            Assert.AreEqual(0L, first.Envelope!.RevisionTimestampUtcMicroseconds);
            first.Value = 2; var saved = await repo.SaveAsync(first);
            Assert.IsTrue(saved.Envelope!.RevisionTimestampUtcMicroseconds > 0);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, saved.Envelope.ExtensionMetadata);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveAsync(second).AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task ReopenAfterDeletionNeverReusesImportedFutureRevision(bool segment)
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-recreate-revision-" + Guid.NewGuid());
        const long future = 1_000_000_000_000_000_000;
        IRecord old;
        string key;
        try
        {
            await using (var provider = Open(root, segment))
            {
                var repo = provider.GetRequiredService<IEnvelopeMetadataRepository<IRecord>>();
                var saved = await repo.SaveAsync(new Record { Id = "same", Value = 1 });
                key = saved.Envelope!.EnvelopeCacheKey;
                old = await repo.SaveWithExtensionMetadataAsync(new Record { Id = "same", Value = 2 }, key, null,
                    new EnvelopeWriteCondition(saved.Envelope.RevisionTimestampUtcMicroseconds, EnvelopeRevisionAction.Import, future));
                var deleting = (await repo.QueryFirstAsync(x => x.Id == "same"))!;
                await repo.DeleteAsync(deleting);
            }
            await using var reopened = Open(root, segment);
            var repository = reopened.GetRequiredService<IRepository<IRecord>>();
            var recreated = await repository.SaveAsync(new Record { Id = "same", Value = 3 });
            Assert.AreEqual(key, recreated.Envelope!.EnvelopeCacheKey);
            Assert.IsTrue(recreated.Envelope.RevisionTimestampUtcMicroseconds > future);
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repository.SaveAsync(old).AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SegmentBackupIncludesClockAndRestoredStoreAdvancesAfterRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-revision-backup-" + Guid.NewGuid());
        var backup = root + "-backup"; var restore = root + "-restore";
        const long future = 1_000_000_000_000_000_000;
        try
        {
            await using (var provider = Open(root, true))
            {
                var repo = provider.GetRequiredService<IEnvelopeMetadataRepository<IRecord>>();
                var saved = await repo.SaveWithExtensionMetadataAsync(new Record { Id = "same" }, null, null,
                    new EnvelopeWriteCondition(0, EnvelopeRevisionAction.Import, future));
                await repo.DeleteAsync(saved);
                await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IRecord>>().BackupAsync(backup);
                Assert.IsTrue(File.Exists(Path.Combine(backup, ".idb-revision-clock")));
            }
            await SegmentCoreStore.RestoreBackupAsync(backup, StoreRoot(restore), Options.StoreOptions);
            await using var restored = Open(restore, true);
            var recreated = await restored.GetRequiredService<IRepository<IRecord>>().SaveAsync(new Record { Id = "same" });
            Assert.IsTrue(recreated.Envelope!.RevisionTimestampUtcMicroseconds > future);
        }
        finally { foreach (var path in new[] { root, backup, restore }) if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class Identity(string root) : IEnvelopeStorageIdentityFactory<IRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Single;
        public void InitEnvelope(IObjectEnvelope<IRecord> envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)) envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id;
            envelope.Location ??= Path.Combine(root, "1.0.0", envelope.EnvelopeCacheKey + ".mpk");
        }
    }
}
