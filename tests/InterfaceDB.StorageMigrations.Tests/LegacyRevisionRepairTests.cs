using Idb.Commercial.Repositories.StorageMigrations;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Commercial.Repositories;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDB.Commercial.StorageMigrations.Tests;

public sealed partial class StorageMigrationTests
{
    private static StorageMigrationOptions RepairOptions => TestData.Options with
    { LegacyRevisions = LegacyRevisionHandling.RepairOnStartup };
    private static StorageMigrationOptions PartialOptions => RepairOptions with
    { IncompleteBehavior = IncompleteMigrationBehavior.ContinueWithAvailableRecords, MaximumRevisionRepairsPerAttempt = 1 };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitSameLabel_MixedIdb2AndCurrentRecords_SurviveRetirementAndReopen(bool interrupt)
    {
        const string label = "v1_0_0";
        const string legacyKey = "duplicate-17";
        using var data = new TestData();
        var options = RepairOptions with { RunningVersion = label };
        var original = new MigrationRecord { Id = "duplicate", Name = "original", Secret = "example", Score = 42 };
        var positiveRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var source = data.BuildSource(version: label))
        {
            var writer = source.GetRequiredService<IEnvelopeMetadataRepository<IMigrationRecord>>();
            foreach (var suffix in new byte[] { 5, 9 })
            {
                var key = $"duplicate-{suffix}";
                var record = await writer.SaveWithExtensionMetadataAsync(new MigrationRecord
                {
                    Id = original.Id, Name = original.Name, Secret = original.Secret, Score = original.Score
                }, key, [suffix, 99], new EnvelopeWriteCondition(0, ExpectedMissing: true));
                Assert.AreEqual(label, record.Envelope!.VersionTag);
                positiveRevisions.Add(key, record.Envelope.RevisionTimestampUtcMicroseconds);
            }
        }
        var legacyPath = Path.Combine(data.SourceRoot, label, legacyKey + ".mpk");
        using (var stream = new MemoryStream())
        {
            stream.Write("IDB2"u8);
            MessagePackSerializer.Serialize(stream, label);
            MessagePackSerializer.Serialize(stream, legacyKey);
            MessagePackSerializer.Serialize(stream, typeof(MigrationRecord).AssemblyQualifiedName!);
            MessagePackSerializer.Serialize(stream, original);
            await File.WriteAllBytesAsync(legacyPath, stream.ToArray());
        }
        var sourceClock = new FileStorageMigrationRevisionClock(data.SourceRoot, PlatformFilePublication.Instance);
        var clockFloor = await sourceClock.ReadAsync(default);
        Assert.IsTrue(clockFloor >= positiveRevisions.Values.Max());
        var payloads = Directory.GetFiles(data.SourceRoot, "*.mpk", SearchOption.AllDirectories)
            .Append(Path.Combine(data.SourceRoot, ".idb-revision-clock")).ToDictionary(path => path, File.ReadAllBytes);
        Assert.AreEqual(4, payloads.Count);

        long? repairedRevision = null;
        if (interrupt)
        {
            await using var first = data.Build(options, label, state: new FailState(data.State(), 2), sourceVersion: label);
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
            var interrupted = (await data.State().ReadAsync(default))!;
            Assert.IsFalse(interrupted.Completed);
            Assert.AreEqual(label, interrupted.SourceVersion);
            Assert.AreEqual(label, interrupted.TargetVersion);
            repairedRevision = interrupted.RepairedRevisions[legacyKey];
        }

        await using (var migrated = data.Build(options, label,
            revisionRepair: interrupt ? new RejectRepairCalls() : null, sourceVersion: label))
        {
            var repository = data.Registered(migrated);
            await repository.InitializeAsync();
            Assert.AreEqual(StorageMigrationPhase.Complete, ((IStorageMigrationProgress)repository).Progress.Phase);
            repairedRevision = await AssertRecordsAsync(repository, repairedRevision);
        }
        foreach (var payload in payloads)
            CollectionAssert.AreEqual(payload.Value, await File.ReadAllBytesAsync(payload.Key), payload.Key);
        var receipt = (await data.State().ReadAsync(default))!;
        Assert.IsTrue(receipt.Completed && receipt.Retired);
        Assert.AreEqual(label, receipt.SourceVersion);
        Assert.AreEqual(label, receipt.TargetVersion);
        Assert.AreEqual(0, receipt.PendingKeys.Length);

        Directory.Move(data.SourceRoot, data.SourceRoot + "-retired");
        await using var reopened = data.Build(options, label, revisionRepair: new RejectRepairCalls(), sourceVersion: label);
        await AssertRecordsAsync(data.Registered(reopened), repairedRevision);
        Assert.IsFalse(Directory.Exists(data.SourceRoot), "A retired reopen must not recreate the source.");

        async Task<long> AssertRecordsAsync(IRepository<IMigrationRecord> repository, long? expectedLegacyRevision)
        {
            var values = await Collect(repository.QueryManyAsync(_ => true));
            var keyed = values.ToDictionary(record => record.Envelope!.EnvelopeCacheKey, StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(positiveRevisions.Keys.Append(legacyKey).ToArray(), keyed.Keys.ToArray());
            foreach (var record in keyed.Values)
            {
                Assert.IsInstanceOfType<MigrationRecord>(record);
                Assert.AreEqual(label, record.Envelope!.VersionTag);
                Assert.AreEqual(original.Id, record.Id);
                Assert.AreEqual(original.Name, record.Name);
                Assert.AreEqual(original.Secret, record.Secret);
                Assert.AreEqual(original.Score, record.Score);
            }
            foreach (var key in positiveRevisions.Keys)
            {
                var envelope = keyed[key].Envelope!;
                Assert.AreEqual(positiveRevisions[key], envelope.RevisionTimestampUtcMicroseconds);
                CollectionAssert.AreEqual(new byte[] { byte.Parse(key[^1..]), 99 }, envelope.ExtensionMetadata);
            }
            var revision = keyed[legacyKey].Envelope!.RevisionTimestampUtcMicroseconds;
            Assert.IsTrue(revision > clockFloor);
            if (expectedLegacyRevision.HasValue)
                Assert.AreEqual(expectedLegacyRevision.Value, revision);
            return revision;
        }
    }

    [TestMethod]
    [DataRow("v1_0_0", "1.0.0", "1.0.0", StorageMigrationTrigger.Explicit, null, null)]
    [DataRow("v1_0_0", "v1_0_0", "V1_0_0", StorageMigrationTrigger.Explicit, null, null)]
    [DataRow("v1_0_0", "v1_0_0", "v1_0_0", StorageMigrationTrigger.ContractVersion, null, null)]
    [DataRow("v1_0_0", "v1_0_0", "v1_0_0", StorageMigrationTrigger.Explicit, "1.0.0", null)]
    [DataRow("v1_0_0", "v1_0_0", "v1_0_0", StorageMigrationTrigger.Explicit, null, "1.0.0")]
    [DataRow("1.0.0", "1.0.0", "1.0.0", StorageMigrationTrigger.ContractVersion, "bad-ceiling", null)]
    public async Task LabelsInOrderedMigration_AreRejectedBeforeTransfer(string source, string target, string running,
        StorageMigrationTrigger trigger, string? sourceCeiling, string? targetCeiling)
    {
        using var data = new TestData();
        var options = TestData.Options with
        {
            RunningVersion = running, Trigger = trigger, SourceMaxVersion = sourceCeiling,
            DestinationMaxVersion = targetCeiling
        };
        await using var services = data.Build(options, target, sourceVersion: source);
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(services).InitializeAsync().AsTask());
        StringAssert.Contains(exception.Message, "must be numeric");
        Assert.IsFalse(File.Exists(data.ReceiptPath));
        Assert.IsFalse(Directory.Exists(data.TargetRoot));
    }

    [TestMethod]
    public void UnchangedTransform_ExactLabelsAndExistingNumericEquivalence_AreSupported()
    {
        var transform = new UnchangedStorageMigrationTransform<IMigrationRecord>();
        var record = new MigrationRecord();
        Assert.AreSame(record, transform.Transform(record, "v1_0_0", "v1_0_0"));
        Assert.AreSame(record, transform.Transform(record, "01.0.0", "1.0.0"));
        Assert.ThrowsExactly<ArgumentException>(() => transform.Transform(record, "v1_0_0", "1.0.0"));
        Assert.ThrowsExactly<InvalidOperationException>(() => transform.Transform(record, "1.0.0", "2.0.0"));
    }

    [TestMethod]
    public async Task DifferentReceiptLabel_RefusesUnorderedReopen()
    {
        const string label = "v1_0_0";
        using var data = new TestData();
        await data.SeedAsync(1, version: label);
        var options = TestData.Options with { RunningVersion = label };
        await using (var migrated = data.Build(options, label, sourceVersion: label))
            await data.Registered(migrated).InitializeAsync();

        var store = data.State();
        await using (var lease = await store.AcquireLeaseAsync(default))
        {
            var completed = (await store.ReadAsync(default))!;
            Assert.IsTrue(completed.Completed);
            await store.WriteAsync(completed with { TargetVersion = "1.0.0" }, default);
        }

        await using var reopened = data.Build(options, label, sourceVersion: label);
        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() => data.Registered(reopened).InitializeAsync().AsTask());
        StringAssert.Contains(exception.Message, "Destination version must be numeric");
        Assert.AreEqual("1.0.0", (await store.ReadAsync(default))!.TargetVersion);
    }

    private static async Task<Dictionary<string, byte[]>> SeedLegacyAsync(TestData data, int count, bool encrypted = false, string version = "1.0.0")
    {
        var directory = Path.Combine(data.SourceRoot, version); Directory.CreateDirectory(directory);
        var files = new Dictionary<string, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(directory, $"legacy-{i}.mpk");
            var envelope = new FileObjectEnvelope<IMigrationRecord>
            {
                EnvelopeCacheKey = $"legacy-{i}", VersionTag = version, RevisionTimestampUtcMicroseconds = 0,
                ObjectInstance = new MigrationRecord { Id = $"legacy-{i}", Name = "original", Secret = "secret", Score = i },
                ExtensionMetadata = [8, 9]
            };
            var binary = new BinaryObjectSerializer<IMigrationRecord>();
            var bytes = encrypted ? new EncryptingObjectSerializer<IMigrationRecord>(binary, TestData.Encryptor(1))
                .Serialize(envelope, StorageEncryptionContext.Create(data.Config("old", version), "file", envelope.EnvelopeCacheKey)) : binary.Serialize(envelope);
            await File.WriteAllBytesAsync(path, bytes); files.Add(path, bytes);
        }
        return files;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyRepair_PreservesSourcePositiveRevisionsKeysAndMetadata(bool encrypted)
    {
        using var data = new TestData(); var positives = await data.SeedAsync(2, encrypted);
        var files = await SeedLegacyAsync(data, 3, encrypted);
        await using var provider = data.Build(RepairOptions, encrypted: encrypted);
        var repository = data.Registered(provider);
        var values = await Collect(repository.QueryManyAsync(_ => true));
        Assert.AreEqual(5, values.Count);
        foreach (var positive in positives)
            Assert.AreEqual(positive.Envelope!.RevisionTimestampUtcMicroseconds,
                values.Single(x => x.Id == positive.Id).Envelope!.RevisionTimestampUtcMicroseconds);
        var repaired = values.Where(x => x.Id.StartsWith("legacy", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(repaired.All(x => x.Envelope!.RevisionTimestampUtcMicroseconds > positives.Max(p => p.Envelope!.RevisionTimestampUtcMicroseconds)));
        Assert.AreEqual(3, repaired.Select(x => x.Envelope!.RevisionTimestampUtcMicroseconds).Distinct().Count());
        foreach (var value in repaired)
        {
            Assert.AreEqual(value.Id, value.Envelope!.EnvelopeCacheKey);
            Assert.AreEqual("secret", value.Secret);
            CollectionAssert.AreEqual(new byte[] { 8, 9 }, value.Envelope.ExtensionMetadata);
        }
        foreach (var file in files) CollectionAssert.AreEqual(file.Value, await File.ReadAllBytesAsync(file.Key));
        Assert.IsTrue((await data.State(encrypted).ReadAsync(default))!.Completed);
        Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
    }

    [TestMethod]
    [DataRow("1.0.0")]
    [DataRow("v1_0_0")]
    public async Task PartialAvailability_AllowsEditsDeletesAddsAndRetainedEdits_PreservesThemAcrossRestart(string version)
    {
        using var data = new TestData();
        await data.SeedAsync(3, version: version);
        await SeedLegacyAsync(data, 3, version: version);
        var options = PartialOptions with { RunningVersion = version };
        string deletedKey; long repairedRevision;
        await using (var provider = data.Build(options, version, sourceVersion: version))
        {
            var repository = data.Registered(provider); await repository.InitializeAsync();
            var progress = provider.GetRequiredKeyedService<IStorageMigrationProgress<IMigrationRecord>>("messages");
            Assert.AreEqual(new(StorageMigrationPhase.Incomplete, 6, 4, 2), progress.Progress);
            Assert.IsFalse(((IRepositoryDataAvailability)repository).IsDataComplete);
            var records = await Collect(repository.QueryManyAsync(_ => true));
            repairedRevision = records.Single(r => r.Id == "legacy-0").Envelope!.RevisionTimestampUtcMicroseconds;
            var deleted = records.Single(r => r.Id == "item-0"); deletedKey = deleted.Envelope!.EnvelopeCacheKey;
            Assert.IsTrue(await repository.DeleteAsync(deleted));
            var edit = records.Single(r => r.Id == "item-1"); edit.Name = "application edit"; await repository.SaveAsync(edit);
            await repository.SaveAsync(new MigrationRecord { Id = "new", Name = "application addition" });
            await using var retained = await ((IRevisionEditingRepository<IMigrationRecord>)repository).BeginEditAsync(
                records.Single(r => r.Id == "item-2").Envelope!.EnvelopeCacheKey);
            retained.Working.Name = "retained edit"; await retained.SaveAsync();
            Assert.ThrowsExactly<InvalidOperationException>(() => RepositoryDataAvailability.RequireComplete(repository));
            Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredKeyedService<IEnvelopeRepository<IMigrationRecord>>("messages"));
            Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
        }
        await using (var restarted = data.Build(options, version, sourceVersion: version))
        {
            var repository = data.Registered(restarted); await repository.InitializeAsync();
            Assert.AreEqual(1, ((IStorageMigrationProgress)repository).Progress.PendingRecords);
            var values = await Collect(repository.QueryManyAsync(_ => true));
            Assert.IsFalse(values.Any(x => x.Envelope!.EnvelopeCacheKey == deletedKey));
            Assert.AreEqual("application edit", values.Single(x => x.Id == "item-1").Name);
            Assert.AreEqual("retained edit", values.Single(x => x.Id == "item-2").Name);
            Assert.AreEqual("application addition", values.Single(x => x.Id == "new").Name);
            Assert.AreEqual(repairedRevision, values.Single(x => x.Id == "legacy-0").Envelope!.RevisionTimestampUtcMicroseconds);
            await ((IStorageMigrationProgress)repository).RetryAsync();
            Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
            RepositoryDataAvailability.RequireComplete(repository);
            Assert.AreEqual(6, (await Collect(repository.QueryManyAsync(_ => true))).Count);
            Assert.IsTrue((await data.State().ReadAsync(default))!.Completed);
        }
        await using var complete = data.Build(RepairOptions with { RunningVersion = version }, version, sourceVersion: version);
        Assert.AreEqual(6, (await Collect(data.Registered(complete).QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    public async Task PendingKeys_RejectExplicitAndFactoryAssignedWrites_BeforeMutation()
    {
        using var data = new TestData(); await SeedLegacyAsync(data, 2);
        await using var provider = data.Build(PartialOptions, identityFactory: new PendingKeyFactory());
        var repository = data.Registered(provider); await repository.InitializeAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => repository.SaveAsync(new MigrationRecord { Id = "collision" }).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ((IEnvelopeMetadataRepository<IMigrationRecord>)repository)
            .SaveWithExtensionMetadataAsync(new MigrationRecord { Id = "collision" }, "legacy-1", null,
                new EnvelopeWriteCondition(0, ExpectedMissing: true)).AsTask());
        Assert.AreEqual(1, (await Collect(repository.QueryManyAsync(_ => true))).Count);
        await ((IStorageMigrationProgress)repository).RetryAsync();
        Assert.AreEqual("original", (await repository.QueryFirstAsync(x => x.Id == "legacy-1"))!.Name);
    }

    private sealed class PendingKeyFactory : IEnvelopeStorageIdentityFactory<IMigrationRecord>
    {
        public Idb.Libraries.Abstractions.Enums.StorageObjectCardinality ObjectCardinality => Idb.Libraries.Abstractions.Enums.StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<IMigrationRecord> envelope)
        { if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey)) envelope.EnvelopeCacheKey = "legacy"; }
    }

    [TestMethod]
    public async Task StrictMode_ReportsPending_ExplicitRetryCompletesWithoutPartialExposure()
    {
        using var data = new TestData(); await SeedLegacyAsync(data, 2);
        await using var provider = data.Build(RepairOptions with { MaximumRevisionRepairsPerAttempt = 1 });
        var repository = data.Registered(provider);
        var error = await Assert.ThrowsExactlyAsync<StorageMigrationIncompleteException>(() => repository.InitializeAsync().AsTask());
        Assert.AreEqual(1, error.PendingRecords);
        Assert.IsFalse((await data.State().ReadAsync(default))!.Completed);
        await ((IStorageMigrationProgress)repository).RetryAsync();
        Assert.AreEqual(2, (await Collect(repository.QueryManyAsync(_ => true))).Count);
    }

    [TestMethod]
    public async Task CrashAfterImport_ReusesDurableRevisionAssignment()
    {
        using var data = new TestData(); await SeedLegacyAsync(data, 3);
        await using (var first = data.Build(RepairOptions, state: new FailState(data.State(), 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(first).InitializeAsync().AsTask());
        var before = (await data.State().ReadAsync(default))!;
        Assert.AreEqual(3, before.RepairedRevisions.Count);
        await using var resumed = data.Build(RepairOptions, revisionRepair: new RejectRepairCalls());
        foreach (var value in await Collect(data.Registered(resumed).QueryManyAsync(_ => true)))
            Assert.AreEqual(before.RepairedRevisions[value.Envelope!.EnvelopeCacheKey], value.Envelope.RevisionTimestampUtcMicroseconds);
    }

    private sealed class RejectRepairCalls : IStorageMigrationLegacyRevisionRepair<IMigrationRecord>
    {
        public ValueTask<long?> AssignAsync(StorageMigrationRecord<IMigrationRecord> record, long observedFloor, CancellationToken cancellationToken)
            => throw new AssertFailedException("A durable revision was allocated twice.");
    }

    [TestMethod]
    public async Task DeferredRepair_IsExplicit_NotAnIgnoredException()
    {
        using var data = new TestData(); await data.SeedAsync(1); await SeedLegacyAsync(data, 1);
        var repair = new DeferredRepair();
        await using var provider = data.Build(PartialOptions, revisionRepair: repair);
        var repository = data.Registered(provider); await repository.InitializeAsync();
        Assert.AreEqual(1, (await Collect(repository.QueryManyAsync(_ => true))).Count);
        repair.Ready = true;
        await ((IStorageMigrationProgress)repository).RetryAsync();
        Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
        Assert.AreEqual(2, (await Collect(repository.QueryManyAsync(_ => true))).Count);
    }

    private sealed class DeferredRepair : IStorageMigrationLegacyRevisionRepair<IMigrationRecord>
    {
        public bool Ready;
        public ValueTask<long?> AssignAsync(StorageMigrationRecord<IMigrationRecord> record, long observedFloor, CancellationToken cancellationToken)
            => ValueTask.FromResult<long?>(Ready ? checked(observedFloor + 1) : null);
    }

    [TestMethod]
    public async Task RetryCommitFailure_PreservesApplicationChangesAndAssignments_AcrossReopen()
    {
        using var data = new TestData(); await data.SeedAsync(1); await SeedLegacyAsync(data, 2);
        await using (var first = data.Build(PartialOptions))
        {
            var repository = data.Registered(first);
            var value = (await repository.QueryFirstAsync(x => x.Id == "item-0"))!;
            value.Name = "keep edit"; await repository.SaveAsync(value);
        }
        await using (var failing = data.Build(PartialOptions, state: new FailState(data.State(), 2)))
            await Assert.ThrowsExactlyAsync<IOException>(() => data.Registered(failing).InitializeAsync().AsTask());
        var assigned = (await data.State().ReadAsync(default))!.RepairedRevisions;
        await using var recovered = data.Build(RepairOptions, revisionRepair: new RejectRepairCalls());
        var records = await Collect(data.Registered(recovered).QueryManyAsync(_ => true));
        Assert.AreEqual("keep edit", records.Single(x => x.Id == "item-0").Name);
        foreach (var value in records.Where(x => x.Id.StartsWith("legacy", StringComparison.Ordinal)))
            Assert.AreEqual(assigned[value.Id], value.Envelope!.RevisionTimestampUtcMicroseconds);
        var later = await data.Registered(recovered).SaveAsync(new MigrationRecord { Id = "later" });
        Assert.IsTrue(later.Envelope!.RevisionTimestampUtcMicroseconds > records.Max(x => x.Envelope!.RevisionTimestampUtcMicroseconds));
    }

    [TestMethod]
    public async Task RepairRetry_SerializesWithApplicationWrites_WithoutLosingThem()
    {
        using var data = new TestData(); await data.SeedAsync(1); await SeedLegacyAsync(data, 2);
        var repair = new PausedRepair();
        await using var provider = data.Build(PartialOptions, revisionRepair: repair);
        var repository = data.Registered(provider); await repository.InitializeAsync();
        var value = (await repository.QueryFirstAsync(x => x.Id == "item-0"))!;
        value.Name = "concurrent application edit";
        var retry = ((IStorageMigrationProgress)repository).RetryAsync().AsTask();
        await repair.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saving = repository.SaveAsync(value).AsTask();
        Assert.IsFalse(saving.IsCompleted);
        repair.Continue.SetResult();
        await Task.WhenAll(retry, saving).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual("concurrent application edit", (await repository.QueryFirstAsync(x => x.Id == "item-0"))!.Name);
        Assert.IsTrue(((IRepositoryDataAvailability)repository).IsDataComplete);
    }

    private sealed class PausedRepair : IStorageMigrationLegacyRevisionRepair<IMigrationRecord>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async ValueTask<long?> AssignAsync(StorageMigrationRecord<IMigrationRecord> record, long observedFloor, CancellationToken cancellationToken)
        {
            if (++_calls == 2) { Entered.SetResult(); await Continue.Task.WaitAsync(cancellationToken); }
            return checked(observedFloor + 1);
        }
    }

    [TestMethod]
    public async Task IncompleteRepository_CannotStartResilientReconciliation_IndependentStoreStaysWritable()
    {
        using var data = new TestData(); await SeedLegacyAsync(data, 2);
        await using var provider = data.Build(PartialOptions);
        var repository = data.Registered(provider); await repository.InitializeAsync();
        using var otherData = new TestData(); await using var other = otherData.BuildSource(policy: TestData.Policy with
        { DurabilityMode = Idb.Libraries.Abstractions.Enums.StorageDurabilityMode.Durable });
        var independent = other.GetRequiredService<IRepository<IMigrationRecord>>();
        await independent.SaveAsync(new MigrationRecord { Id = "independent" });
        await using var resilient = new ResilientRepository<IMigrationRecord>(repository, independent, new MigrationRecordMapper());
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resilient.InitializeAsync().AsTask());
        StringAssert.Contains(error.Message, "incomplete");
        await using var peer = new Idb.Commercial.Repositories.Peers.PeerReplicatedRepository<IMigrationRecord>(
            (IEnvelopeMetadataRepository<IMigrationRecord>)repository, (IRepositoryNotifications<IMigrationRecord>)repository,
            new MigrationRecordMapper(), null!, null!, new Idb.Commercial.Repositories.Peers.FilePeerJournal(Path.Combine(data.Root, "peer-journal")), null!);
        var peerError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => peer.InitializeAsync().AsTask());
        StringAssert.Contains(peerError.Message, "incomplete");
        Assert.AreEqual(1, (await Collect(independent.QueryManyAsync(_ => true))).Count);
        Assert.AreEqual(1, ((IStorageMigrationProgress)repository).Progress.PendingRecords);
    }

    private sealed class MigrationRecordMapper : IResilientEntityMapper<IMigrationRecord>
    {
        public string GetStorageKey(IMigrationRecord entity) => entity.Id;
        public IMigrationRecord CreateDetachedCopy(IMigrationRecord source)
        { var result = new MigrationRecord(); CopyValues(source, result); return result; }
        public void CopyValues(IMigrationRecord source, IMigrationRecord target)
        { target.Id = source.Id; target.Name = source.Name; target.Secret = source.Secret; target.Score = source.Score; }
    }

    [TestMethod]
    public async Task RepairReceipt_ExceedsOld64KbLimit_RoundTrips_AndEnforcesConfiguredBoundBeforeReplacing()
    {
        using var data = new TestData();
        var revisions = Enumerable.Range(0, 4000).ToDictionary(i => $"legacy-record-with-long-key-{i}", i => (long)i + 1);
        var state = new StorageMigrationState("plan", "digest", revisions.Count, 5000, 0, false) { RepairedRevisions = revisions };
        await data.State().WriteAsync(state, default);
        Assert.AreEqual(4000, (await data.State().ReadAsync(default))!.RepairedRevisions.Count);
        var original = await File.ReadAllBytesAsync(data.ReceiptPath);
        var small = new FileStorageMigrationStateStore(data.ReceiptPath, "messages", new EncryptionNone(), PlatformFilePublication.Instance, 64 * 1024);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => small.WriteAsync(state, default).AsTask());
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(data.ReceiptPath));
    }
}
