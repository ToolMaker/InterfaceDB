using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

[TestClass]
public sealed partial class StorageMigrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileToSegment_PreservesPhysicalKeysRevisionsMetadataAndValues_AcrossReopen(bool encrypted)
    {
        using var data = new TestData();
        var expected = await data.SeedAsync(4, encrypted);
        await using (var services = data.Build(encrypted: encrypted))
        {
            var repo = data.Registered(services);
            await repo.InitializeAsync();
            Assert.AreEqual(ContractRepositoryType.SegmentCore, repo.RepositoryType);
            var actual = await Collect(repo.QueryManyAsync(_ => true));
            Assert.AreEqual(4, actual.Count);
            foreach (var item in actual)
            {
                var original = expected.Single(e => e.Id == item.Id);
                Assert.AreEqual(original.Envelope!.EnvelopeCacheKey, item.Envelope!.EnvelopeCacheKey);
                Assert.AreEqual(original.Envelope.RevisionTimestampUtcMicroseconds, item.Envelope.RevisionTimestampUtcMicroseconds);
                CollectionAssert.AreEqual(original.Envelope.ExtensionMetadata, item.Envelope.ExtensionMetadata);
                Assert.AreEqual(original.Secret, item.Secret);
            }
            Assert.AreEqual(4, (await Collect(repo.SearchManyAsync(x => x.Score >= 0))).Count);
            var edited = actual[0]; edited.Name = "unsaved";
            Assert.AreNotEqual("unsaved", (await repo.QueryFirstAsync(x => x.Id == edited.Id))!.Name);
        }
        // Completed startup must not need the old provider. Deliberately make source unavailable.
        Directory.Move(data.SourceRoot, data.SourceRoot + "-retired");
        await using var reopened = data.Build(encrypted: encrypted);
        Assert.AreEqual(4, (await Collect(data.Registered(reopened).QueryManyAsync(_ => true))).Count);
        Assert.IsFalse(Directory.Exists(data.SourceRoot), "Completed startup must not recreate a retired source.");
        var state = await data.State(encrypted).ReadAsync(default);
        Assert.IsTrue(state!.Completed);
        Assert.AreEqual(4, state.VerifiedRecords);
    }

    [TestMethod]
    public async Task CrashAfterCommittedImportBeforeProgress_IsReconciledWithoutDuplicates()
    {
        using var data = new TestData();
        await data.SeedAsync(5);
        await using (var first = data.Build(state: new FailState(data.State(), failWrite: 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
        var interrupted = await data.State().ReadAsync(default);
        Assert.IsNotNull(interrupted);
        Assert.IsFalse(interrupted.Completed);
        await using var restarted = data.Build();
        var repo = data.Registered(restarted);
        await repo.InitializeAsync();
        var records = await Collect(repo.QueryManyAsync(_ => true));
        Assert.AreEqual(5, records.Count);
        Assert.AreEqual(5, records.Select(x => x.Envelope!.EnvelopeCacheKey).Distinct().Count());
    }

    [TestMethod]
    public async Task CompletedMigration_DoesNotReimportDeletedOrEditedDestinationRecords()
    {
        using var data = new TestData();
        await data.SeedAsync(3);
        await using (var first = data.Build())
        {
            var repo = data.Registered(first);
            var records = await Collect(repo.QueryManyAsync(_ => true));
            await repo.DeleteAsync(records[0]);
            records[1].Name = "changed after migration";
            await repo.SaveAsync(records[1]);
        }
        await using var second = data.Build();
        var actual = await Collect(data.Registered(second).QueryManyAsync(_ => true));
        Assert.AreEqual(2, actual.Count);
        Assert.AreEqual(1, actual.Count(x => x.Name == "changed after migration"));
    }

    [TestMethod]
    public async Task SourceChangedAfterInterruption_IsRejectedAndSourceIsRetained()
    {
        using var data = new TestData();
        await data.SeedAsync(3);
        await using (var first = data.Build(state: new FailState(data.State(), 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
        await using (var writer = data.BuildSource())
        {
            var repo = writer.GetRequiredService<IRepository<IMigrationRecord>>();
            await repo.InitializeAsync();
            var item = (await repo.QueryFirstAsync(_ => true))!;
            item.Name = "source changed";
            await repo.SaveAsync(item);
        }
        await using var second = data.Build();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(second).InitializeAsync().AsTask());
        Assert.IsTrue(Directory.EnumerateFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task NonemptyUnownedDestination_IsRejectedWithoutOverwrite()
    {
        using var data = new TestData();
        await data.SeedAsync(2);
        await using var services = data.Build();
        var raw = services.GetRequiredKeyedService<IRepository<IMigrationRecord>>("new");
        await raw.SaveAsync(new MigrationRecord { Id = "unrelated", Name = "keep" });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(services).InitializeAsync().AsTask());
        Assert.AreEqual("keep", (await raw.QueryFirstAsync(x => x.Id == "unrelated"))!.Name);
        Assert.IsNull(await data.State().ReadAsync(default));
    }

    [TestMethod]
    public async Task ResumedDestinationWithModifiedPayload_IsRejected()
    {
        using var data = new TestData();
        await data.SeedAsync(3);
        await using (var first = data.Build(state: new FailState(data.State(), 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
        await using var second = data.Build();
        var raw = second.GetRequiredKeyedService<IRepository<IMigrationRecord>>("new");
        var item = (await raw.QueryFirstAsync(_ => true))!;
        item.Name = "collision";
        await raw.SaveAsync(item);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(second).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task DeletedHighRevision_PreservesClockFloorForNextDestinationWrite()
    {
        using var data = new TestData();
        await data.SeedAsync(1);
        var future = DateTimeOffset.UtcNow.AddYears(3).ToUnixTimeMilliseconds() * 1000;
        await using (var source = data.BuildSource())
        {
            var repo = source.GetRequiredService<IEnvelopeMetadataRepository<IMigrationRecord>>();
            var item = await repo.SaveWithExtensionMetadataAsync(new MigrationRecord { Id = "deleted" }, "deleted-99", null,
                new EnvelopeWriteCondition(0, EnvelopeRevisionAction.Import, future, ExpectedMissing: true));
            await repo.DeleteAsync(item);
        }
        await using var services = data.Build();
        var saved = await data.Registered(services).SaveAsync(new MigrationRecord { Id = "later" });
        Assert.IsTrue(saved.Envelope!.RevisionTimestampUtcMicroseconds > future);
    }

    [TestMethod]
    public async Task VersionBoundary_IsInclusive_ThenTransformRunsAtNextVersion()
    {
        using var data = new TestData();
        await data.SeedAsync(2);
        var options = TestData.Options with { Trigger = StorageMigrationTrigger.ContractVersion, SourceMaxVersion = "1.0.0" };
        await using (var before = data.Build(options, targetVersion: "2.0.0", transform: new Upgrade()))
        {
            var repo = data.Registered(before);
            await repo.InitializeAsync();
            Assert.AreEqual(ContractRepositoryType.FileSystem, repo.RepositoryType);
            Assert.IsNull(await data.State().ReadAsync(default));
        }
        await using var after = data.Build(options with { RunningVersion = "2.0.0" }, "2.0.0", transform: new Upgrade());
        var migrated = await Collect(data.Registered(after).QueryManyAsync(_ => true));
        Assert.AreEqual(2, migrated.Count);
        Assert.IsTrue(migrated.All(x => x.Name.StartsWith("upgraded:", StringComparison.Ordinal)));
        Assert.IsTrue(migrated.All(x => x.Envelope!.VersionTag == "2.0.0"));
    }

    [TestMethod]
    public async Task NoSourceMaximum_KeepsSourceProvider_WithoutRunningMigration()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using var services = data.Build(TestData.Options with { Trigger = StorageMigrationTrigger.ContractVersion });
        var repo = data.Registered(services); await repo.InitializeAsync();
        Assert.AreEqual(ContractRepositoryType.FileSystem, repo.RepositoryType);
        Assert.IsNull(await data.State().ReadAsync(default));
    }

    [TestMethod]
    public async Task MissingSchemaTransform_FailsBeforeAnyDestinationRecords()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using var services = data.Build(TestData.Options with { RunningVersion = "2.0.0" }, "2.0.0");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(services).InitializeAsync().AsTask());
        Assert.IsFalse(Directory.Exists(data.DestinationStore("2.0.0")));
    }

    [TestMethod]
    public async Task DisabledPendingMigration_Rejects_CompletedMigrationStillOpens()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using (var blocked = data.Build(TestData.Options with { RunOnStartup = false }))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(blocked).InitializeAsync().AsTask());
        await using (var run = data.Build()) await data.Registered(run).InitializeAsync();
        await using var completed = data.Build(TestData.Options with { RunOnStartup = false });
        Assert.IsNotNull(await data.Registered(completed).QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task NotificationSubscription_DoesNotPublishImportEchoes_AndNormalWritesStillNotify()
    {
        using var data = new TestData(); await data.SeedAsync(4);
        await using var services = data.Build();
        var repo = data.Registered(services);
        var seen = new List<string>();
        using var sub = ((IRepositoryNotifications<IMigrationRecord>)repo).SubscribeCreated(x => { seen.Add(x.Id); return ValueTask.CompletedTask; });
        await sub.Ready;
        await sub.WaitForIdleAsync(); Assert.AreEqual(0, seen.Count);
        await repo.SaveAsync(new MigrationRecord { Id = "after" });
        await sub.WaitForIdleAsync();
        CollectionAssert.AreEqual(new[] { "after" }, seen);
    }

    [TestMethod]
    public async Task ConcurrentFirstOperations_MigrateOnce_AndRespectCancellation()
    {
        using var data = new TestData(); await data.SeedAsync(4);
        await using var services = data.Build(); var repo = data.Registered(services);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => repo.InitializeAsync(canceled.Token).AsTask());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => Assert.AreEqual(4, (await Collect(repo.QueryManyAsync(_ => true))).Count)));
        Assert.IsTrue((await data.State().ReadAsync(default))!.Completed);
    }

    [TestMethod]
    public async Task MissingSource_AndMissingCompletedTarget_FailClosed()
    {
        using var data = new TestData();
        await using (var absent = data.Build())
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => data.Registered(absent).InitializeAsync().AsTask());
        await data.SeedAsync(1);
        await using (var complete = data.Build()) await data.Registered(complete).InitializeAsync();
        File.Delete(Path.Combine(data.DestinationStore(), ".idb-revision-clock"));
        await using var missing = data.Build();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(missing).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task ReceiptPlanChange_AndDowngradeToOldProvider_AreRejected()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using (var complete = data.Build()) await data.Registered(complete).InitializeAsync();
        await using (var changed = data.Build(TestData.Options with { MigrationId = "another" }))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(changed).InitializeAsync().AsTask());
        await using var downgrade = data.Build(TestData.Options with { Trigger = StorageMigrationTrigger.ContractVersion });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(downgrade).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task MaximumRecordCount_PreflightLeavesDestinationUntouched()
    {
        using var data = new TestData(); await data.SeedAsync(3);
        await using var services = data.Build(TestData.Options with { MaximumRecords = 2 });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(services).InitializeAsync().AsTask());
        Assert.IsFalse(Directory.Exists(data.DestinationStore()));
    }

    [TestMethod]
    public async Task FileSourceLease_BlocksOrdinaryWritersAndReleasesOnDispose()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        var config = data.Config("old");
        var guard = new FileStorageMigrationSourceGuard<IMigrationRecord>(config, TestData.Policy,
            new SidecarFileWriteLock<IMigrationRecord>(TestData.Policy), PlatformFilePublication.Instance);
        await using var writer = data.BuildSource();
        var repo = writer.GetRequiredService<IRepository<IMigrationRecord>>(); await repo.InitializeAsync();
        await using (await guard.AcquireAsync(default))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAsync<OperationCanceledException>(() => repo.SaveAsync(new MigrationRecord { Id = "blocked" }, stop.Token).AsTask());
        }
        Assert.IsNotNull(await repo.SaveAsync(new MigrationRecord { Id = "allowed" }));
    }

    [TestMethod]
    public async Task ReceiptEncryption_RejectsWrongContext_AndClockRejectsCorruption()
    {
        using var data = new TestData(); await data.SeedAsync(1, encrypted: true);
        await using (var migrated = data.Build(encrypted: true)) await data.Registered(migrated).InitializeAsync();
        var wrong = new FileStorageMigrationStateStore(data.ReceiptPath, "wrong-contract", TestData.Encryptor(2), PlatformFilePublication.Instance);
        await Assert.ThrowsAsync<Exception>(() => wrong.ReadAsync(default).AsTask());
        await File.WriteAllBytesAsync(Path.Combine(data.DestinationStore(), ".idb-revision-clock"), new byte[3]);
        await using var corrupted = data.Build(encrypted: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(corrupted).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task RetainedEditingAndMetadataWrites_RemainAvailableThroughMigrationWrapper()
    {
        using var data = new TestData(); var seed = await data.SeedAsync(1);
        await using var services = data.Build(); var repo = data.Registered(services);
        Assert.AreEqual(StorageDurabilityMode.Durable, ((IEnvelopeMetadataRepository<IMigrationRecord>)repo).CommitDurability);
        Assert.IsTrue(((IRevisionEditingRepository<IMigrationRecord>)repo).RetainedVersions.Supported);
        Assert.AreEqual(ContractRepositoryType.SegmentCore, repo.RepositoryType);
        Assert.IsFalse(Directory.Exists(data.DestinationStore()), "Inspecting capabilities must not start migration.");
        await repo.InitializeAsync();
        await using var editing = await repo.BeginEditAsync(seed[0].Envelope!.EnvelopeCacheKey);
        editing.Working.Name = "being edited";
        Assert.AreEqual(seed[0].Name, (await editing.ReadOriginalAsync()).Name);
        Assert.AreEqual(seed[0].Name, (await repo.QueryFirstAsync(_ => true))!.Name);
        await editing.SaveAsync();
        Assert.AreEqual("being edited", (await repo.QueryFirstAsync(_ => true))!.Name);
        var metadata = (IEnvelopeMetadataRepository<IMigrationRecord>)repo;
        var inserted = await metadata.SaveWithExtensionMetadataAsync(new MigrationRecord { Id = "metadata" }, null, new byte[] { 3 });
        CollectionAssert.AreEqual(new byte[] { 3 }, inserted.Envelope!.ExtensionMetadata);
    }

    [TestMethod]
    public async Task EmptyExistingSource_MigratesAndCompletes()
    {
        using var data = new TestData(); Directory.CreateDirectory(data.SourceRoot);
        await using var services = data.Build();
        Assert.AreEqual(0, (await Collect(data.Registered(services).QueryManyAsync(_ => true))).Count);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Completed);
    }

    [TestMethod]
    public async Task InvalidVersionRanges_AreRejectedBeforeStorageWrites()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using (var overlapping = data.Build(TestData.Options with { Trigger = StorageMigrationTrigger.ContractVersion, SourceMaxVersion = "1.0.0" }))
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(overlapping).InitializeAsync().AsTask());
        await using var beyond = data.Build(TestData.Options with { DestinationMaxVersion = "0.9.0" });
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(beyond).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task UncertainCompletionReceipt_CanBeRetriedWithFreshProviders()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using (var first = data.Build(state: new FailState(data.State(), 3)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
        Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
        await using var retry = data.Build(); await data.Registered(retry).InitializeAsync();
        Assert.IsTrue((await data.State().ReadAsync(default))!.Completed);
    }

    [TestMethod]
    public async Task MultipleKeyedRepositories_KeepTheirReceiptsAndRecordsIndependent()
    {
        using var first = new TestData(); using var second = new TestData();
        await first.SeedAsync(2); await second.SeedAsync(5);
        await using var firstProvider = first.Build(); await using var secondProvider = second.Build();
        await Task.WhenAll(first.Registered(firstProvider).InitializeAsync().AsTask(), second.Registered(secondProvider).InitializeAsync().AsTask());
        Assert.AreEqual(2, (await Collect(first.Registered(firstProvider).QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(5, (await Collect(second.Registered(secondProvider).QueryManyAsync(_ => true))).Count);
        // Logical configuration can match after removing paths from the plan. Physical
        // instance binding must still distinguish the two stores.
        Assert.AreNotEqual((await first.State().ReadAsync(default))!.DestinationInstance, (await second.State().ReadAsync(default))!.DestinationInstance);
    }

    [TestMethod]
    public async Task DisposedOrFailedEarlySubscription_DoesNotAttachToPartialMigration()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using var services = data.Build(TestData.Options with { RunOnStartup = false });
        var notifications = (IRepositoryNotifications<IMigrationRecord>)data.Registered(services);
        using var failed = notifications.SubscribeCreated(_ => throw new AssertFailedException("Unexpected notification."));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failed.Ready);
        Assert.AreEqual(RepositorySubscriptionEndReason.ObservationFailed, await failed.Completion);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    { var items = new List<T>(); await foreach (var item in values) items.Add(item); return items; }

    [TestMethod]
    public async Task IntegrityFailure_DoesNotPublishCompletion_AndResumesAfterControlledReopen()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var fail = data.Build(verification: _ => throw new InvalidDataException("Injected integrity failure.")))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(fail).InitializeAsync().AsTask());
        Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
        await using var retry = data.Build(); await data.Registered(retry).InitializeAsync();
        Assert.IsTrue((await data.State().ReadAsync(default))!.Completed);
    }

    private sealed class Upgrade : IStorageMigrationTransform<IMigrationRecord>
    {
        public string Identity => "record-v1-to-v2";
        public IMigrationRecord Transform(IMigrationRecord source, string sourceVersion, string destinationVersion)
        { source.Name = "upgraded:" + source.Name; return source; }
    }

    private sealed class FailState(IStorageMigrationStateStore inner, int failWrite) : IStorageMigrationStateStore
    {
        public string? StoragePath => inner.StoragePath;
        private int _writes;
        public ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken ct) => inner.AcquireLeaseAsync(ct);
        public ValueTask<StorageMigrationState?> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public ValueTask ValidateCapacityAsync(StorageMigrationState state, CancellationToken ct) => inner.ValidateCapacityAsync(state, ct);
        public ValueTask WriteAsync(StorageMigrationState state, CancellationToken ct)
            => ++_writes == failWrite ? throw new IOException("Injected receipt publication failure after durable import.") : inner.WriteAsync(state, ct);
    }

    private sealed class TestData : IDisposable
    {
        private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\Temp\InterfaceDBStorageMigrationTests\data" : Path.Combine(Path.GetTempPath(), "InterfaceDBStorageMigrationTests", "data");
        public string Root { get; private set; } = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        public string SourceRoot => Path.Combine(Root, "file");
        public string TargetRoot => Path.Combine(Root, "segment");
        public string ReceiptPath => Path.Combine(Root, "migration", "receipt");
        public string DestinationStore(string version = "1.0.0") => Path.Combine(TargetRoot, ".segment-core", version);
        public static StorageMigrationOptions Options => new() { MigrationId = "file-to-segment-v1", RunningVersion = "1.0.0", CheckpointEveryRecords = 1 };
        public static StoragePolicy Policy => StoragePolicy.Default with { HydrateOnInitialize = false, WatchStorageChanges = false,
            ObjectCacheMode = StorageObjectCacheMode.StrongObject, RepositoryNotifications = new(), RaiseNotifications = true,
            RetainedVersions = new() };
        public TestData() => Directory.CreateDirectory(Root);
        public void Relocate()
        {
            var next = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
            var boundary = Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(next).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Move(Root, next); Root = next;
        }
        public ConfigRecord Config(string key, string version = "1.0.0") => new(key, key == "old" ? SourceRoot : TargetRoot, version);
        public static IEncryptor Encryptor(int key) => new AesGcmEncryptor(new FixedKey(key));
        public IStorageMigrationStateStore State(bool encrypted = false) => new FileStorageMigrationStateStore(ReceiptPath,
            "messages", encrypted ? Encryptor(2) : new EncryptionNone(), PlatformFilePublication.Instance);
        public IRepository<IMigrationRecord> Registered(IServiceProvider services) => services.GetRequiredKeyedService<IRepository<IMigrationRecord>>("messages");

        public ServiceProvider BuildSource(bool encrypted = false, StoragePolicy? policy = null, string version = "1.0.0")
        {
            var services = new ServiceCollection();
            if (encrypted) services.AddKeyedSingleton<IEncryptor>("old", Encryptor(1));
            services.AddFileRepository<IMigrationRecord>(Config("old", version), storagePolicy: policy ?? Policy);
            return services.BuildServiceProvider();
        }

        public async Task<List<IMigrationRecord>> SeedAsync(int count, bool encrypted = false, string version = "1.0.0")
        {
            await using var provider = BuildSource(encrypted, version: version);
            var repo = provider.GetRequiredService<IEnvelopeMetadataRepository<IMigrationRecord>>();
            var values = new List<IMigrationRecord>();
            for (var i = 0; i < count; i++)
                values.Add(await repo.SaveWithExtensionMetadataAsync(new MigrationRecord { Id = "item-" + i, Name = "name-" + i,
                    Secret = "private-value-" + i, Score = i }, "physical-key-" + (5 + 2 * i), new byte[] { (byte)i, 99 },
                    new EnvelopeWriteCondition(0, ExpectedMissing: true)));
            return values;
        }

        public ServiceProvider Build(StorageMigrationOptions? options = null, string targetVersion = "1.0.0", bool encrypted = false,
            IStorageMigrationStateStore? state = null, IStorageMigrationTransform<IMigrationRecord>? transform = null,
            Func<CancellationToken, ValueTask>? verification = null, StoragePolicy? sourcePolicy = null,
            bool makeDefault = true, Action<IServiceCollection>? beforeRegistration = null,
            IStorageMigrationLegacyRevisionRepair<IMigrationRecord>? revisionRepair = null,
            IEnvelopeStorageIdentityFactory<IMigrationRecord>? identityFactory = null,
            IFilePublication? sourcePublication = null,
            Func<IStorageMigrationSource<IMigrationRecord>, IStorageMigrationSource<IMigrationRecord>>? wrapSource = null,
            string sourceVersion = "1.0.0")
        {
            var services = new ServiceCollection();
            beforeRegistration?.Invoke(services);
            if (encrypted) services.AddKeyedSingleton<IEncryptor>("old", Encryptor(1));
            services.AddFileRepository<IMigrationRecord>(Config("old", sourceVersion), storagePolicy: sourcePolicy ?? Policy);
            services.AddSegmentCoreRepository<IMigrationRecord>(Config("new", targetVersion),
                new SegmentCoreRepositoryOptions { EnableBackgroundMaintenance = false }, Policy, storageIdentityFactory: identityFactory,
                encrypted ? Encryptor(2) : null);
            services.AddStorageMigration<IMigrationRecord>("messages", sp => new StorageMigration<IMigrationRecord>(options ?? Options,
                (wrapSource ?? (source => source))(StorageMigrationEndpointFactory.FileSource<IMigrationRecord>(sp, "old", sourcePublication ?? PlatformFilePublication.Instance)),
                StorageMigrationEndpointFactory.LocalDestination<IMigrationRecord>(sp, "new", DestinationStore(targetVersion),
                    PlatformFilePublication.Instance, verification ?? (async ct =>
                    {
                        var maintenance = sp.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>("new");
                        await maintenance.CheckpointAsync(ct);
                        if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid)
                            throw new InvalidDataException("Destination integrity check failed.");
                    })), transform ?? new UnchangedStorageMigrationTransform<IMigrationRecord>(),
                new SerializerStorageMigrationFingerprint<IMigrationRecord>(sp.GetRequiredKeyedService<BinaryObjectSerializer<IMigrationRecord>>("new"), "binary-v1"),
                state ?? State(encrypted), new StorageMigrationHistory<IMigrationRecord>((version, ct) => OpenHistory(version, encrypted)),
                revisionRepair ?? new TimestampStorageMigrationRevisionRepair<IMigrationRecord>(new MonotonicRevisionTimestampGenerator())), makeDefault);
            return services.BuildServiceProvider();
        }

        private ValueTask<IStorageMigrationHistoryHandle<IMigrationRecord>> OpenHistory(string version, bool encrypted)
        {
            var registrations = new ServiceCollection();
            registrations.AddSegmentCoreRepository<IMigrationRecord>(Config("new", version),
                new SegmentCoreRepositoryOptions { EnableBackgroundMaintenance = false }, Policy, storageIdentityFactory: null,
                encrypted ? Encryptor(2) : null);
            var provider = registrations.BuildServiceProvider();
            var target = StorageMigrationEndpointFactory.LocalDestination<IMigrationRecord>(provider, "new", DestinationStore(version),
                PlatformFilePublication.Instance, async ct =>
                {
                    var maintenance = provider.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>("new");
                    await maintenance.CheckpointAsync(ct);
                    if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid) throw new InvalidDataException();
                });
            var source = StorageMigrationEndpointFactory.LocalSource<IMigrationRecord>(provider, "new", DestinationStore(version), PlatformFilePublication.Instance);
            return ValueTask.FromResult<IStorageMigrationHistoryHandle<IMigrationRecord>>(new StorageMigrationHistoryHandle<IMigrationRecord>(source, target, provider));
        }

        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside migration test data.");
            DeleteOwned(new DirectoryInfo(resolved));
        }
        private static void DeleteOwned(DirectoryInfo directory)
        {
            if (!directory.Exists) return;
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) child.Delete();
                else if (child is DirectoryInfo nested) DeleteOwned(nested);
                else child.Delete();
            }
            directory.Delete();
        }
    }

    private sealed class FixedKey(int seed) : IKeyProvider
    { public ReadOnlyMemory<byte> GetCurrentKey() => Enumerable.Repeat((byte)seed, 32).ToArray(); }

    private sealed class ConfigRecord(string key, string root, string version) : IDbContractConfigDto<IMigrationRecord>
    {
        public string EnvelopeDICacheKey => key;
        public string ContractName => "MigrationRecord";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => version;
        public ContractRepositoryType RepositoryType => key == "new" ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem;
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public StoragePolicy? StoragePolicy => TestData.Policy;
    }

    private sealed class Identity : IEnvelopeStorageIdentityFactory<IMigrationRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<IMigrationRecord> envelope)
        { envelope.EnvelopeCacheKey = envelope.ObjectInstance!.Id; envelope.Location ??= envelope.EnvelopeCacheKey; }
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IMigrationRecord : IStoredContract<IMigrationRecord>
{
    string Id { get; set; }
    string Name { get; set; }
    string Secret { get; set; }
    [SearchIndex] int Score { get; set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class MigrationRecord : IMigrationRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Secret { get; set; } = "";
    public int Score { get; set; }
    [IgnoreMember] public IObjectEnvelope<IMigrationRecord>? Envelope { get; private set; }
}
