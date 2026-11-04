using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

public sealed partial class StorageMigrationTests
{
    [TestMethod]
    public async Task LegacyZeroRevision_IsNotSilentlyRewrittenOrImported()
    {
        using var data = new TestData();
        var directory = Path.Combine(data.SourceRoot, "1.0.0"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy-0.mpk");
        var bytes = new BinaryObjectSerializer<IMigrationRecord>().Serialize(new FileObjectEnvelope<IMigrationRecord>
        {
            EnvelopeCacheKey = "legacy-0", VersionTag = "1.0.0", RevisionTimestampUtcMicroseconds = 0,
            ObjectInstance = new MigrationRecord { Id = "legacy", Name = "preserve", Score = 7 }, ExtensionMetadata = [1, 2]
        });
        await File.WriteAllBytesAsync(path, bytes);
        await using var provider = data.Build();
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => data.Registered(provider).InitializeAsync().AsTask());
        StringAssert.Contains(error.Message, "zero-revision");
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.IsNull(await data.State().ReadAsync(default));
        Assert.IsFalse(Directory.Exists(data.DestinationStore()));
    }

    [TestMethod]
    public async Task NonDefaultMigration_DoesNotReplaceAnotherDatabaseForTheSameContract()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        var otherRoot = Path.Combine(data.Root, "independent");
        await using var provider = data.Build(makeDefault: false, beforeRegistration: services =>
            services.AddFileRepository<IMigrationRecord>(new ConfigRecord("independent", otherRoot, "1.0.0"), storagePolicy: TestData.Policy));
        var ordinary = provider.GetRequiredService<IRepository<IMigrationRecord>>();
        await ordinary.InitializeAsync(); await ordinary.SaveAsync(new MigrationRecord { Id = "independent" });
        await data.Registered(provider).InitializeAsync();
        Assert.AreSame(ordinary, provider.GetRequiredService<IRepository<IMigrationRecord>>());
        Assert.AreEqual(1, (await Collect(ordinary.QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(2, (await Collect(data.Registered(provider).QueryManyAsync(_ => true))).Count);
        var logical = provider.GetRequiredKeyedService<IEnvelopeRepository<IMigrationRecord>>("messages");
        Assert.AreSame(provider.GetRequiredKeyedService<IEnvelopeRepository<IMigrationRecord>>("new"), logical);
        Assert.AreEqual("independent", provider.GetRequiredService<IDbContractConfigDto<IMigrationRecord>>().EnvelopeDICacheKey);
    }

    [TestMethod]
    public async Task ReceiptInsideSource_IsRejectedBeforeMigration()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        var state = new FileStorageMigrationStateStore(Path.Combine(data.SourceRoot, "receipt"), "bad-placement", new EncryptionNone(), PlatformFilePublication.Instance);
        await using var provider = data.Build(state: state);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(provider).InitializeAsync().AsTask());
        Assert.IsFalse(File.Exists(Path.Combine(data.SourceRoot, "receipt")));
    }

    [TestMethod]
    public async Task SourceWithNestedDirectoryLink_IsRejectedWithoutTraversingIt()
    {
        using var data = new TestData(); await data.SeedAsync(1);
        var linked = Path.Combine(data.SourceRoot, "link");
        try { Directory.CreateSymbolicLink(linked, data.Root); }
        catch (UnauthorizedAccessException) { Assert.Inconclusive("Directory links require platform privileges."); }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xFFFF) == 1314)
        { Assert.Inconclusive("Directory links require platform privileges."); }
        try
        {
            await using var provider = data.Build();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(provider).InitializeAsync().AsTask());
            Assert.IsNull(await data.State().ReadAsync(default));
        }
        finally { if (new DirectoryInfo(linked).LinkTarget is not null) Directory.Delete(linked); }
    }

    [TestMethod]
    public async Task MissingSource_RepeatedStartupNeverCreatesEvidenceOrCompletes_AndRestoredSourceCanRetry()
    {
        using var data = new TestData();
        for (var i = 0; i < 3; i++)
        {
            await using var provider = data.Build();
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => data.Registered(provider).InitializeAsync().AsTask());
            Assert.IsFalse(Directory.Exists(data.SourceRoot));
            Assert.IsNull(await data.State().ReadAsync(default));
        }
        await using var retry = data.Build();
        var repo = data.Registered(retry);
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => repo.InitializeAsync().AsTask());
        await data.SeedAsync(2);
        await repo.InitializeAsync();
        Assert.AreEqual(2, (await Collect(repo.QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedDatabase_RelocatesWithReceipt_WithoutSource(bool encrypted)
    {
        using var data = new TestData(); await data.SeedAsync(3, encrypted);
        await using (var provider = data.Build(encrypted: encrypted)) await data.Registered(provider).InitializeAsync();
        data.Relocate();
        await using var relocated = data.Build(encrypted: encrypted);
        Assert.AreEqual(3, (await Collect(data.Registered(relocated).QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    public async Task ReceiptFromAnotherDatabase_CannotBindToAnUnrelatedDestination()
    {
        using var one = new TestData(); using var two = new TestData();
        await one.SeedAsync(2); await two.SeedAsync(2);
        await using (var p = one.Build()) await one.Registered(p).InitializeAsync();
        await using (var p = two.Build()) await two.Registered(p).InitializeAsync();
        await two.State().WriteAsync((await one.State().ReadAsync(default))!, default);
        await using var swapped = two.Build();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => two.Registered(swapped).InitializeAsync().AsTask());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task LaterVersion_HandlesCompletedSkippedAndInterruptedRelease(bool interrupted, bool encrypted)
    {
        using var migrated = new TestData(); using var skipped = new TestData();
        var originals = await migrated.SeedAsync(3, encrypted); await skipped.SeedAsync(3, encrypted);
        var v2 = TestData.Options with { RunningVersion = "2.0.0", SourceMaxVersion = "1.0.0", Trigger = StorageMigrationTrigger.ContractVersion };
        await using (var provider = migrated.Build(v2, "2.0.0", encrypted,
            state: interrupted ? new FailState(migrated.State(encrypted), 2) : null, transform: new VersionChain()))
        {
            if (interrupted) await Assert.ThrowsExactlyAsync<IOException>(() => migrated.Registered(provider).InitializeAsync().AsTask());
            else await migrated.Registered(provider).InitializeAsync();
        }
        var v3 = v2 with { RunningVersion = "3.0.0" };
        foreach (var data in new[] { migrated, skipped })
        {
            await using var provider = data.Build(v3, "3.0.0", encrypted, transform: new VersionChain());
            var records = await Collect(data.Registered(provider).QueryManyAsync(_ => true));
            Assert.AreEqual(3, records.Count);
            foreach (var record in records)
            {
                Assert.AreEqual("v3:v2:name-" + record.Score, record.Name);
                Assert.AreEqual("3.0.0", record.Envelope!.VersionTag);
                if (ReferenceEquals(data, migrated))
                    Assert.AreEqual(originals.Single(r => r.Id == record.Id).Envelope!.RevisionTimestampUtcMicroseconds, record.Envelope.RevisionTimestampUtcMicroseconds);
            }
            Assert.AreEqual("3.0.0", (await data.State(encrypted).ReadAsync(default))!.TargetVersion);
        }
    }

    [TestMethod]
    public async Task NewVersion_PreservesPostHandoverEditsAndDeletes_AndResumesItsOwnInterruptedStage()
    {
        using var data = new TestData(); await data.SeedAsync(3);
        await using (var p = data.Build(TestData.Options with { RunningVersion = "2.0.0" }, "2.0.0", transform: new VersionChain()))
        {
            var repo = data.Registered(p); var records = await Collect(repo.QueryManyAsync(_ => true));
            await repo.DeleteAsync(records[0]); records[1].Name = "user-edited"; await repo.SaveAsync(records[1]);
        }
        var v3 = TestData.Options with { RunningVersion = "3.0.0" };
        await using (var p = data.Build(v3, "3.0.0", state: new FailState(data.State(), 2), transform: new VersionChain()))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(p).InitializeAsync().AsTask());
        await using var resumed = data.Build(v3, "3.0.0", transform: new VersionChain());
        var actual = await Collect(data.Registered(resumed).QueryManyAsync(_ => true));
        Assert.AreEqual(2, actual.Count); Assert.AreEqual(1, actual.Count(r => r.Name == "v3:user-edited"));
    }

    [TestMethod]
    public async Task EarlierInitializedSource_StillIncludesLaterCommittedRecords()
    {
        using var data = new TestData(); await data.SeedAsync(3);
        await using var provider = data.Build();
        await provider.GetRequiredKeyedService<IRepository<IMigrationRecord>>("old").InitializeAsync();
        await using (var writer = data.BuildSource())
            await writer.GetRequiredService<IRepository<IMigrationRecord>>().SaveAsync(new MigrationRecord { Id = "late", Name = "after initial catalog" });
        Assert.AreEqual(4, (await Collect(data.Registered(provider).QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    public async Task VerificationFailure_DoesNotSilentlyRetryOnNextRead()
    {
        using var data = new TestData(); await data.SeedAsync(2); var checks = 0;
        await using var provider = data.Build(verification: _ => { checks++; throw new InvalidDataException("injected verification failure"); });
        var repo = data.Registered(provider);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => repo.InitializeAsync().AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Collect(repo.QueryManyAsync(_ => true)));
        Assert.AreEqual(1, checks); Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
    }

    [TestMethod]
    public async Task InfrastructureAliases_AreGatedAndRouteToCompletedDestination()
    {
        using var data = new TestData(); await data.SeedAsync(2);
        await using var provider = data.Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>());
        Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>());
        await data.Registered(provider).InitializeAsync();
        var envelopes = provider.GetRequiredService<IEnvelopeRepository<IMigrationRecord>>();
        Assert.AreSame(envelopes.ReaderAndWriter.Reader, provider.GetRequiredService<IObjectReader<IMigrationRecord>>());
        Assert.AreSame(envelopes.ReaderAndWriter.Writer, provider.GetRequiredService<IObjectWriter<IMigrationRecord>>());
        await envelopes.WriteAsync(new MigrationRecord { Id = "destination", Name = "gated write" });
        Assert.AreEqual(3, (await Collect(data.Registered(provider).QueryManyAsync(_ => true))).Count);
        Assert.IsTrue((await provider.GetRequiredService<ISegmentCoreRepositoryMaintenance<IMigrationRecord>>().VerifyIntegrityAsync()).IsValid);
        await using var old = data.BuildSource();
        await old.GetRequiredService<IRepository<IMigrationRecord>>().InitializeAsync();
        Assert.AreEqual(2, (await Collect(old.GetRequiredService<IRepository<IMigrationRecord>>().QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreBoundaryFile_KeepsNormalWatcherAndHydrationConfiguration(bool withMigrationWrapper)
    {
        using var data = new TestData(); await data.SeedAsync(1);
        var options = TestData.Options with { Trigger = StorageMigrationTrigger.ContractVersion, SourceMaxVersion = "1.0.0" };
        var policy = TestData.Policy with { WatchStorageChanges = true, HydrateOnInitialize = true };
        await using var provider = withMigrationWrapper
            ? data.Build(options, "2.0.0", transform: new VersionChain(), sourcePolicy: policy)
            : data.BuildSource(policy: policy);
        var repo = withMigrationWrapper ? data.Registered(provider) : provider.GetRequiredService<IRepository<IMigrationRecord>>();
        await repo.InitializeAsync();
        await using (var writer = data.BuildSource())
            await writer.GetRequiredService<IRepository<IMigrationRecord>>().SaveAsync(new MigrationRecord { Id = "external", Name = "watcher" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await Collect(repo.QueryManyAsync(_ => true, timeout.Token))).Count != 2) await Task.Delay(20, timeout.Token);
        Assert.AreEqual(ContractRepositoryType.FileSystem, repo.RepositoryType); Assert.IsNull(await data.State().ReadAsync(default));
    }

    [TestMethod]
    public async Task ReceiptLease_RemovesOnlyOwnedAbandonedStagingFiles()
    {
        using var data = new TestData(); Directory.CreateDirectory(Path.GetDirectoryName(data.ReceiptPath)!);
        var orphan = data.ReceiptPath + ".creating-" + Guid.NewGuid().ToString("N");
        var unrelated = data.ReceiptPath + ".creating-not-a-guid";
        await File.WriteAllTextAsync(orphan, "partial"); await File.WriteAllTextAsync(unrelated, "keep");
        await using (await data.State().AcquireLeaseAsync(default))
        { Assert.IsFalse(File.Exists(orphan)); Assert.IsTrue(File.Exists(unrelated)); }
    }

    private sealed class VersionChain : IStorageMigrationTransform<IMigrationRecord>
    {
        public string Identity => "record-version-chain-v1";
        public IMigrationRecord Transform(IMigrationRecord source, string from, string to)
        {
            for (var version = Version.Parse(from).Major + 1; version <= Version.Parse(to).Major; version++)
                source.Name = "v" + version + ":" + source.Name;
            return source;
        }
    }
}
