using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

public sealed partial class StorageMigrationTests
{
    private static StorageMigrationOptions CleanupOptions => TestData.Options with { CleanupOriginalSourceAfterCompletion = true };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Retirement_DeletesOnlyVerifiedSourcePayloads_AndOpensWithoutSourceConfiguration(bool encrypted)
    {
        using var data = new TestData(); await data.SeedAsync(4, encrypted);
        var unrelated = Path.Combine(data.SourceRoot, "owner-notes.txt"); await File.WriteAllTextAsync(unrelated, "keep");
        await using (var provider = data.Build(CleanupOptions, encrypted: encrypted))
        {
            var repository = data.Registered(provider); await repository.InitializeAsync();
            Assert.AreEqual(4, (await Collect(repository.QueryManyAsync(_ => true))).Count);
            Assert.AreEqual(StorageMigrationRetirement.Retired, ((IStorageMigrationProgress)repository).Progress.Retirement);
            Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
            Assert.AreEqual("keep", await File.ReadAllTextAsync(unrelated));
            var state = (await data.State(encrypted).ReadAsync(default))!;
            Assert.IsTrue(state.Completed && state.Retired);
            Assert.IsNull(state.CleanupRecords);
            Assert.AreEqual(0, state.RepairedRevisions.Count);
            Assert.IsTrue(new FileInfo(data.ReceiptPath).Length < 4096);
        }
        Directory.Move(data.SourceRoot, data.SourceRoot + "-unavailable");
        await using var retired = BuildRetired(data, encrypted);
        var records = await Collect(data.Registered(retired).QueryManyAsync(_ => true));
        Assert.AreEqual(4, records.Count);
        records[0].Name = "edited after retirement"; await data.Registered(retired).SaveAsync(records[0]);
        Assert.IsFalse(Directory.Exists(data.SourceRoot));
    }

    [TestMethod]
    public async Task IncompleteMigration_PreservesEverySourceRecord_AndRejectsRetiredRegistration()
    {
        using var data = new TestData(); await data.SeedAsync(1); var files = await SeedLegacyAsync(data, 2);
        var options = PartialOptions with { CleanupOriginalSourceAfterCompletion = true };
        await using (var provider = data.Build(options))
        {
            var repository = data.Registered(provider); await repository.InitializeAsync();
            Assert.AreEqual(1, ((IStorageMigrationProgress)repository).Progress.PendingRecords);
            Assert.AreEqual(3, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
            foreach (var file in files) CollectionAssert.AreEqual(file.Value, await File.ReadAllBytesAsync(file.Key));
            Assert.IsFalse((await data.State().ReadAsync(default))!.Retired);
        }
        await using (var wrong = BuildRetired(data))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(wrong).InitializeAsync().AsTask());
        await using var resumed = data.Build(options);
        await data.Registered(resumed).InitializeAsync();
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CleanupFailure_IsResumable_WhileDestinationEditsDeletesAndAddsSurvive(bool failAfterDelete)
    {
        using var data = new TestData(); await data.SeedAsync(3);
        await using (var provider = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(failAfterDelete)))
        {
            var repository = data.Registered(provider); await repository.InitializeAsync();
            var progress = ((IStorageMigrationProgress)repository).Progress;
            Assert.AreEqual(StorageMigrationPhase.Complete, progress.Phase);
            Assert.AreEqual(StorageMigrationRetirement.Pending, progress.Retirement);
            Assert.AreEqual("CleanupRetryRequired", progress.RetirementIssue);
            var items = await Collect(repository.QueryManyAsync(_ => true));
            await repository.DeleteAsync(items.Single(x => x.Id == "item-0"));
            var edit = items.Single(x => x.Id == "item-1"); edit.Name = "destination edit"; await repository.SaveAsync(edit);
            await repository.SaveAsync(new MigrationRecord { Id = "new" });
            Assert.IsNotNull((await data.State().ReadAsync(default))!.CleanupRecords);
        }
        await using var resumed = data.Build(CleanupOptions);
        var values = await Collect(data.Registered(resumed).QueryManyAsync(_ => true));
        Assert.AreEqual(3, values.Count); Assert.IsFalse(values.Any(x => x.Id == "item-0"));
        Assert.AreEqual("destination edit", values.Single(x => x.Id == "item-1").Name);
        Assert.IsTrue(values.Any(x => x.Id == "new"));
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task Cleanup_PreservesChangedAndUnknownSourceRecords_AndReportsThem()
    {
        using var data = new TestData(); await data.SeedAsync(3);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(false)))
            await data.Registered(first).InitializeAsync();
        await using (var source = data.BuildSource())
        {
            var repository = source.GetRequiredService<IRepository<IMigrationRecord>>();
            await repository.InitializeAsync();
            var changed = (await repository.QueryFirstAsync(x => x.Id == "item-0"))!;
            changed.Name = "late old-source edit"; await repository.SaveAsync(changed);
            await repository.SaveAsync(new MigrationRecord { Id = "never migrated" });
        }
        await using (var second = data.Build(CleanupOptions))
        {
            var repository = data.Registered(second); await repository.InitializeAsync();
            var progress = ((IStorageMigrationProgress)repository).Progress;
            Assert.AreEqual(2, progress.RetainedSourceRecords);
            Assert.AreEqual("SourceRecordsRetained", progress.RetirementIssue);
            Assert.AreEqual(3, (await Collect(repository.QueryManyAsync(_ => true))).Count);
            Assert.AreEqual(2, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
            Assert.IsFalse((await data.State().ReadAsync(default))!.Retired);
        }
        await using var retained = data.BuildSource();
        var remainingRepository = retained.GetRequiredService<IRepository<IMigrationRecord>>();
        await remainingRepository.InitializeAsync();
        var remaining = await Collect(remainingRepository.QueryManyAsync(_ => true));
        Assert.AreEqual("late old-source edit", remaining.Single(x => x.Id == "item-0").Name);
        Assert.IsTrue(remaining.Any(x => x.Id == "never migrated"));
    }

    [TestMethod]
    public async Task UnavailableCleanupSource_DoesNotRecreateFolders_OrBlockCompletedData()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(false)))
            await data.Registered(first).InitializeAsync();
        Directory.Move(data.SourceRoot, data.SourceRoot + "-offline");
        await using var second = data.Build(CleanupOptions);
        var repository = data.Registered(second); await repository.InitializeAsync();
        Assert.AreEqual(1, (await Collect(repository.QueryManyAsync(_ => true))).Count);
        Assert.IsFalse(Directory.Exists(data.SourceRoot));
        Assert.AreEqual("SourceUnavailable", ((IStorageMigrationProgress)repository).Progress.RetirementIssue);
    }

    [TestMethod]
    public async Task RetirementMarkerPublicationFailure_RetriesWithoutReimportingDeletedSource()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        // Cleanup mode stores the plan, completion, and then retirement; no per-record journal rewrites.
        await using (var first = data.Build(CleanupOptions, state: new FailState(data.State(), 3)))
        {
            var repository = data.Registered(first); await repository.InitializeAsync();
            Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
            Assert.IsFalse((await data.State().ReadAsync(default))!.Retired);
            Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        }
        await using var second = data.Build(CleanupOptions);
        Assert.AreEqual(2, (await Collect(data.Registered(second).QueryManyAsync(_ => true))).Count);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task CompletedOlderReceipt_CanPrepareCleanupOnlyFromMatchingFrozenSource()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var first = data.Build(TestData.Options with { RetireAfterCompletion = false }))
            await data.Registered(first).InitializeAsync();
        await using var second = data.Build(CleanupOptions);
        await data.Registered(second).InitializeAsync();
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task RetiredRegistration_RejectsMissingWrongAndFutureVersionMarkers()
    {
        using var data = new TestData();
        await using (var missing = BuildRetired(data))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(missing).InitializeAsync().AsTask());
        Assert.IsFalse(Directory.Exists(data.DestinationStore()));
        await data.SeedAsync(1);
        await using (var first = data.Build(CleanupOptions)) await data.Registered(first).InitializeAsync();
        await using (var wrong = BuildRetired(data, migrationId: "unrelated"))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(wrong).InitializeAsync().AsTask());
        await using var future = BuildRetired(data, version: "2.0.0");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => data.Registered(future).InitializeAsync().AsTask());
        Assert.IsFalse(Directory.Exists(data.DestinationStore("2.0.0")));
    }

    [TestMethod]
    public async Task RetiredInstallAndSkippedRelease_BothAdvanceUsingTheSameConfiguration()
    {
        using var migrated = new TestData(); using var skipped = new TestData();
        await migrated.SeedAsync(2); await skipped.SeedAsync(2);
        await using (var first = migrated.Build(CleanupOptions)) await migrated.Registered(first).InitializeAsync();
        var next = CleanupOptions with { RunningVersion = "2.0.0" };
        await using var upgraded = migrated.Build(next, "2.0.0", transform: new Upgrade());
        await using var direct = skipped.Build(next, "2.0.0", transform: new Upgrade());
        Assert.AreEqual(2, (await Collect(migrated.Registered(upgraded).QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(2, (await Collect(skipped.Registered(direct).QueryManyAsync(_ => true))).Count);
        Assert.IsTrue((await migrated.State().ReadAsync(default))!.Retired);
        Assert.IsTrue((await skipped.State().ReadAsync(default))!.Retired);
    }

    private sealed class FailingCleanupPublication(bool afterDelete) : IFilePublication
    {
        public bool Fail { get; set; } = true;
        public void Replace(string stagedPath, string destinationPath) => PlatformFilePublication.Instance.Replace(stagedPath, destinationPath);
        public void FlushDirectory(string directoryPath) => PlatformFilePublication.Instance.FlushDirectory(directoryPath);
        public void Delete(string path)
        {
            if (!Fail || afterDelete) PlatformFilePublication.Instance.Delete(path);
            if (Fail) throw new IOException("Injected cleanup failure.");
        }
    }

    [TestMethod]
    public async Task FailedCompletionPublication_DoesNotDeleteAnySourceRecord()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        var originals = Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        await using (var failed = data.Build(CleanupOptions, state: new FailState(data.State(), 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(failed).InitializeAsync().AsTask());
        foreach (var (path, contents) in originals) CollectionAssert.AreEqual(contents, await File.ReadAllBytesAsync(path));
        Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
        await using var resumed = data.Build(CleanupOptions);
        Assert.AreEqual(2, (await Collect(data.Registered(resumed).QueryManyAsync(_ => true))).Count);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task ExplicitCleanupRetry_DoesNotRequireReopeningAHealthyDestination()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        var publication = new FailingCleanupPublication(false);
        await using var provider = data.Build(CleanupOptions, sourcePublication: publication);
        var repository = data.Registered(provider); await repository.InitializeAsync();
        var progress = (IStorageMigrationProgress)repository;
        Assert.AreEqual(StorageMigrationRetirement.Pending, progress.Progress.Retirement);
        await repository.SaveAsync(new MigrationRecord { Id = "after completion" });
        publication.Fail = false;
        await progress.RetryAsync();
        Assert.AreEqual(StorageMigrationRetirement.Retired, progress.Progress.Retirement);
        Assert.AreEqual(3, (await Collect(repository.QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task CorruptSourceAfterCompletion_IsRetained_AndHealthySourceCopiesCanBeCleaned()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(false)))
            await data.Registered(first).InitializeAsync();
        var damaged = Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).First();
        await File.WriteAllBytesAsync(damaged, [1, 2, 3]);
        await using var second = data.Build(CleanupOptions);
        var repository = data.Registered(second); await repository.InitializeAsync();
        Assert.AreEqual(2, (await Collect(repository.QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(1, ((IStorageMigrationProgress)repository).Progress.RetainedSourceRecords);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(damaged));
        Assert.AreEqual(1, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task CleanupLocatorOutsideSource_IsRejectedBeforeAnyDeletion()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(false)))
            await data.Registered(first).InitializeAsync();
        var outside = Path.Combine(data.Root, "unrelated.mpk"); await File.WriteAllTextAsync(outside, "do not delete");
        var state = (await data.State().ReadAsync(default))!;
        var records = state.CleanupRecords!.ToArray(); records[^1] = records[^1] with { Locator = "../unrelated.mpk" };
        await data.State().WriteAsync(state with { CleanupRecords = records }, default);
        await using var second = data.Build(CleanupOptions);
        await data.Registered(second).InitializeAsync();
        Assert.AreEqual("do not delete", await File.ReadAllTextAsync(outside));
        Assert.AreEqual(2, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsFalse((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task CleanupPending_SurvivesLaterVersionUpgrade_WithoutBlockingItsData()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(false)))
            await data.Registered(first).InitializeAsync();
        Directory.Move(data.SourceRoot, data.SourceRoot + "-offline");
        var next = CleanupOptions with { RunningVersion = "2.0.0" };
        await using (var upgraded = data.Build(next, "2.0.0", transform: new Upgrade()))
        {
            Assert.AreEqual(2, (await Collect(data.Registered(upgraded).QueryManyAsync(_ => true))).Count);
            var state = (await data.State().ReadAsync(default))!;
            Assert.AreEqual("2.0.0", state.TargetVersion);
            Assert.IsTrue(state.Completed && state.CleanupRequired && !state.Retired);
            Assert.IsNotNull(state.CleanupRecords);
        }
        Directory.Move(data.SourceRoot + "-offline", data.SourceRoot);
        await using var restored = data.Build(next, "2.0.0", transform: new Upgrade());
        await data.Registered(restored).InitializeAsync();
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    [TestMethod]
    public async Task RetiredMarkerCannotBeSwappedBetweenDatabases()
    {
        using var first = new TestData(); using var second = new TestData();
        await first.SeedAsync(1); await second.SeedAsync(1);
        await using (var a = first.Build(CleanupOptions)) await first.Registered(a).InitializeAsync();
        await using (var b = second.Build(CleanupOptions)) await second.Registered(b).InitializeAsync();
        File.Copy(first.ReceiptPath, second.ReceiptPath, true);
        await using var wrong = BuildRetired(second);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.Registered(wrong).InitializeAsync().AsTask());
    }

    [TestMethod]
    public async Task CleanupPlanRemainsValidAfterWholeDatabaseRelocation()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using (var first = data.Build(CleanupOptions, sourcePublication: new FailingCleanupPublication(true)))
            await data.Registered(first).InitializeAsync();
        data.Relocate();
        await using var relocated = data.Build(CleanupOptions);
        Assert.AreEqual(2, (await Collect(data.Registered(relocated).QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(0, Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories).Length);
        Assert.IsTrue((await data.State().ReadAsync(default))!.Retired);
    }

    private static ServiceProvider BuildRetired(TestData data, bool encrypted = false,
        string migrationId = "file-to-segment-v1", string version = "1.0.0")
    {
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IMigrationRecord>(data.Config("new", version),
            new SegmentCoreRepositoryOptions { EnableBackgroundMaintenance = false }, TestData.Policy,
            encryptor: encrypted ? TestData.Encryptor(2) : null);
        services.AddStorageMigration<IMigrationRecord>("messages", sp => new RetiredStorageMigration<IMigrationRecord>(migrationId,
            StorageMigrationEndpointFactory.LocalDestination<IMigrationRecord>(sp, "new", data.DestinationStore(version),
                PlatformFilePublication.Instance, async ct =>
                {
                    var maintenance = sp.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>("new");
                    await maintenance.CheckpointAsync(ct);
                    if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid) throw new InvalidDataException();
                }), data.State(encrypted)));
        return services.BuildServiceProvider();
    }
}
