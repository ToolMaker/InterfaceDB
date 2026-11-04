using Idb.Commercial.ConflictResolution;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Storage.SegmentCore;
#if RETENTION_ATLAS
using Idb.Storage.AtlasCore;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Idb.Commercial.ConflictResolution.Tests;

[TestClass]
public sealed class MergeTests
{
    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task DisjointChangesCommitActualMergedStateWithCurrentDeltaAndPreservedMetadata(bool segment)
    {
        await using var f = new Fixture(segment);
        var seeded = await ((IEnvelopeMetadataRepository<IItem>)f.Repository).SaveWithExtensionMetadataAsync(new Item { Id = "one" }, null, [1]);
        await using var first = await f.Repository.BeginEditAsync(seeded.Envelope!.EnvelopeCacheKey);
        await using var second = await f.Repository.BeginEditAsync(seeded.Envelope.EnvelopeCacheKey);
        first.Working.Name = "current"; var committed = await first.SaveAsync();
        second.Working.Number = 5;
        IReadOnlyList<string>? changes = null;
        using var subscription = f.Services.GetRequiredService<IRepositoryNotifications<IItem>>()
            .SubscribeChanged(change => { changes = change.ChangedProperties; Assert.AreEqual("current", change.Entity.Name); return ValueTask.CompletedTask; }, _ => true);
        await f.CompactAsync();
        var merged = await second.SaveAsync(); await subscription.WaitForIdleAsync();
        Assert.AreEqual("current", merged.Name); Assert.AreEqual(5, merged.Number);
        CollectionAssert.AreEqual(new[] { "Number" }, changes!.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1 }, merged.Envelope!.ExtensionMetadata);
        Assert.IsTrue(merged.Envelope.RevisionTimestampUtcMicroseconds > committed.Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual(0, ((IRevisionEditingRepository<IItem>)f.Repository).RetentionStatistics.ActiveSessions);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task MatchingRevisionRetainsNewAcknowledgementMetadataAndDistinctPhysicalBaselines(bool segment)
    {
        await using var f = new Fixture(segment);
        var metadata = (IEnvelopeMetadataRepository<IItem>)f.Repository;
        var seed = await metadata.SaveWithExtensionMetadataAsync(new Item { Id = "one" }, null, [1]);
        await using var original = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await metadata.SaveWithExtensionMetadataAsync(new Item { Id = "one" }, seed.Envelope.EnvelopeCacheKey, [2],
            new EnvelopeWriteCondition(seed.Envelope.RevisionTimestampUtcMicroseconds, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
        await using var acknowledged = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        Assert.AreEqual(original.ExpectedRevision, acknowledged.ExpectedRevision);
        Assert.AreNotEqual(original.OriginalVersion.RecordId, acknowledged.OriginalVersion.RecordId);
        await f.CompactAsync();
        CollectionAssert.AreEqual(new byte[] { 1 }, (await original.ReadOriginalAsync()).Envelope!.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 2 }, (await acknowledged.ReadOriginalAsync()).Envelope!.ExtensionMetadata);
        original.Working.Number = 7;
        var saved = await original.SaveAsync();
        CollectionAssert.AreEqual(new byte[] { 2 }, saved.Envelope!.ExtensionMetadata);
    }

    [TestMethod] [DataRow(false, false)] [DataRow(false, true)] [DataRow(true, false)] [DataRow(true, true)]
    public async Task OverlapIncludingEqualTargetsRejectsWithPropertyNames(bool segment, bool sameTarget)
    {
        await using var f = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one" });
        await using var first = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var second = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        first.Working.Name = "left"; await first.SaveAsync();
        second.Working.Name = sameTarget ? "left" : "right";
        var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => second.SaveAsync().AsTask());
        Assert.AreEqual("overlapping-properties", conflict.Reason);
        CollectionAssert.AreEqual(new[] { "Name" }, conflict.Properties.ToArray());
        Assert.AreEqual("left", (await conflict.ReadCurrentAsync())!.Name);
        Assert.AreEqual("", (await second.ReadOriginalAsync()).Name);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task StaleNoOpDoesNotWriteOrNotifyAndValidationRejectsDisjointInvalidResult(bool segment)
    {
        await using var f = new Fixture(segment, value => value.Number > 10 ? "invalid" : null);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one" });
        await using var unchanged = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var invalid = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        seed.Name = "new"; var current = await f.Repository.SaveAsync(seed);
        var events = 0;
        using var subscription = f.Services.GetRequiredService<IRepositoryNotifications<IItem>>().SubscribeChanged(_ => { events++; return ValueTask.CompletedTask; }, _ => true);
        var returned = await unchanged.SaveAsync(); await subscription.WaitForIdleAsync();
        Assert.AreEqual(current.Envelope!.RevisionTimestampUtcMicroseconds, returned.Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual("new", returned.Name); Assert.AreEqual(0, events);
        invalid.Working.Number = 99;
        var rejection = await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => invalid.SaveAsync().AsTask());
        Assert.AreEqual("candidate-validation", rejection.Reason);
        Assert.AreEqual(0, (await rejection.ReadCurrentAsync())!.Number);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CollectionValuesUseStructuralWholePropertyComparison(bool segment)
    {
        await using var f = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one", Bytes = [1,2], Names = ["one"] });
        await using var first = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var second = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        first.Working.Bytes[0] = 8; await first.SaveAsync();
        second.Working.Names.Add("two"); var result = await second.SaveAsync();
        CollectionAssert.AreEqual(new byte[] { 8,2 }, result.Bytes);
        CollectionAssert.AreEqual(new[] { "one", "two" }, result.Names);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task DictionariesAndNullsAreWholePropertyValues(bool segment)
    {
        await using var f = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one", Counts = new() { ["a"] = null } });
        await using var a = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var b = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        a.Working.Counts["a"] = 1; await a.SaveAsync();
        b.Working.Name = null!; var result = await b.SaveAsync();
        Assert.AreEqual(1, result.Counts["a"]); Assert.IsNull(result.Name);
        await using var c = await f.Repository.BeginEditAsync(result.Envelope!.EnvelopeCacheKey);
        await using var d = await f.Repository.BeginEditAsync(result.Envelope.EnvelopeCacheKey);
        c.Working.Counts["b"] = 2; await c.SaveAsync(); d.Working.Counts["c"] = 3;
        var rejection = await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => d.SaveAsync().AsTask());
        CollectionAssert.AreEqual(new[] { "Counts" }, rejection.Properties.ToArray());
    }

    [TestMethod]
    public void UnsupportedObjectEqualityRequiresExplicitRegistration()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => new ThreeWayMergeStrategy<IUnsupported>(new() { IdentityProperties = ["Id"] }));
        _ = new ThreeWayMergeStrategy<IUnsupported>(new()
        { IdentityProperties = ["Id"], PropertyComparers = new Dictionary<string, IEqualityComparer<object?>> { ["Detail"] = new DetailEquality() } });
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task UnconfiguredPersistedImplementationFieldsAreRejectedThroughEncryptionProxies(bool segment)
    {
        await using var f = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new FieldItem { Id = "one", Extra = 1 });
        await using var edit = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        seed.Name = "current"; await f.Repository.SaveAsync(seed); edit.Working.Number = 2;
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => edit.SaveAsync().AsTask());
        Assert.AreEqual(0, (await edit.ReadOriginalAsync()).Number);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task MissingBaselineAndIdentityModificationAreExplicitConflicts(bool segment)
    {
        await using var f = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one" });
        await using var invalid = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        seed.Name = "new"; await f.Repository.SaveAsync(seed);
        invalid.Working.Id = "different";
        var rejection = await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => invalid.SaveAsync().AsTask());
        Assert.AreEqual("identity-change", rejection.Reason);
        var stale = await invalid.ReadOriginalAsync();
        rejection = await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => f.Repository.SaveAsync(stale).AsTask());
        Assert.AreEqual("original-version-required", rejection.Reason);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task ValidatorCannotReenterStorageAndRejectedInsertDoesNotPersist(bool segment)
    {
        IRepository<IItem>? repository = null;
        await using var f = new Fixture(segment, value =>
        {
            if (value.Number == 99) return "reject";
            if (value.Number == 77) repository!.SaveAsync(new Item { Id = "nested" }).AsTask().GetAwaiter().GetResult();
            return null;
        });
        repository = f.Repository;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IItem>>(() => repository.SaveAsync(new Item { Id = "invalid", Number = 99 }).AsTask());
        var seed = await repository.SaveAsync(new Item { Id = "one" });
        await using var edit = await repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        edit.Working.Number = 77;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => edit.SaveAsync().AsTask());
        Assert.AreEqual(0, (await edit.ReadOriginalAsync()).Number);
        Assert.IsNull(await repository.QueryFirstAsync(x => x.Id == "nested"));
        Assert.IsNull(await repository.QueryFirstAsync(x => x.Id == "invalid"));
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task EncryptedFieldsMergeSemanticallyAndRemainEncryptedInStorage(bool segment)
    {
        var key = new TestKey();
        await using var f = new Fixture(segment, encryption: key);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one", Secret = "original-secret" });
        await using var a = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var b = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        a.Working.Name = "current"; await a.SaveAsync();
        b.Working.Secret = "merged-secret";
        await f.CompactAsync();
        var saved = await b.SaveAsync();
        Assert.AreEqual("current", saved.Name); Assert.AreEqual("merged-secret", saved.Secret);
        Assert.IsTrue(saved.Envelope!.EncryptedFields.ContainsKey("Secret"));
        foreach (var path in Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".lock")) continue;
            try { Assert.IsFalse(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("merged-secret"), path); }
            catch (IOException) { /* Active engine lock/read sharing is intentionally restrictive. */ }
        }
        await using var denied = await f.Repository.BeginEditAsync(saved.Envelope.EnvelopeCacheKey);
        saved.Name = "newer"; await f.Repository.SaveAsync(saved);
        denied.Working.Number = 2; key.Deny = true;
        try { await Assert.ThrowsAsync<Exception>(() => denied.SaveAsync().AsTask()); }
        finally { key.Deny = false; }
        Assert.AreEqual(0, (await denied.ReadOriginalAsync()).Number);
        Assert.AreEqual(0, (await f.Repository.QueryFirstAsync(x => x.Id == "one"))!.Number);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task EncryptedFieldMergePreservesAnExplicitNull(bool segment)
    {
        await using var f = new Fixture(segment, encryption: new TestKey());
        var seed = await f.Repository.SaveAsync(new Item { Id = "one", Secret = "original-secret" });
        await using var a = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await using var b = await f.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
        a.Working.Name = "current"; await a.SaveAsync();
        b.Working.Secret = null;
        var saved = await b.SaveAsync();
        Assert.AreEqual("current", saved.Name);
        Assert.IsNull(saved.Secret);
        Assert.IsNull((await f.Repository.QuerySingleAsync(_ => true)).Secret);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task WrongStoreAndCancelledSubmissionsCannotConsumeBaseline(bool segment)
    {
        await using var f = new Fixture(segment);
        await using var other = new Fixture(segment);
        var seed = await f.Repository.SaveAsync(new Item { Id = "one" });
        await using var edit = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await Assert.ThrowsAsync<Exception>(() => other.Repository.SaveAsync(edit.Working).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => edit.SaveAsync(cancelled.Token).AsTask());
        Assert.IsFalse(edit.IsCompleted); Assert.AreEqual("one", (await edit.ReadOriginalAsync()).Id);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task ExpiryAndDisposalDuringAdmittedMergePreserveItsBaselineUntilCommit(bool segment)
    {
        var clock = new ManualTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture(segment, asyncValidator: async (item, token) =>
        { if (item.Number == 2) { entered.TrySetResult(); await release.Task.WaitAsync(token); } return null; },
            retention: new() { TimeProvider = clock, LeaseDuration = TimeSpan.FromSeconds(2), MaximumLifetime = TimeSpan.FromSeconds(4) });
        var seed = await f.Repository.SaveAsync(new Item { Id = "one" });
        await using var edit = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        seed.Name = "current"; await f.Repository.SaveAsync(seed);
        edit.Working.Number = 2; var saving = edit.SaveAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Ticks = TimeSpan.FromSeconds(10).Ticks;
            await edit.DisposeAsync();
            Assert.AreEqual(1, ((IRevisionEditingRepository<IItem>)f.Repository).RetentionStatistics.RetainedRecords);
        }
        finally { release.TrySetResult(); }
        var result = await saving;
        Assert.AreEqual("current", result.Name); Assert.AreEqual(2, result.Number);
        Assert.AreEqual(0, ((IRevisionEditingRepository<IItem>)f.Repository).RetentionStatistics.RetainedRecords);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "idb-merge-tests-" + Guid.NewGuid().ToString("N"));
        internal ServiceProvider Services { get; }
        internal IRepository<IItem> Repository { get; }
        private readonly bool _segment;
        internal Fixture(bool segment, Func<IItem,string?>? validator = null, IKeyProvider? encryption = null,
            Func<IItem,CancellationToken,ValueTask<string?>>? asyncValidator = null, RetainedVersionOptions? retention = null)
        {
            _segment = segment;
            var policy = StoragePolicy.Default with { RetainedVersions = retention ?? new(),
                RepositoryNotifications = new() { PropertyComparers = new Dictionary<string, IEqualityComparer<object?>> { ["Counts"] = new CountsEquality() } }, RaiseNotifications = false,
                WatchStorageChanges = false, ObjectCacheMode = StorageObjectCacheMode.StrongObject };
            var services = new ServiceCollection();
            services.AddInterfaceDbAesGcmEncryption(encryption ?? new TestKey());
            services.AddSingleton<IWriteConflictStrategy<IItem>>(new ThreeWayMergeStrategy<IItem>(new()
            { IdentityProperties = ["Id"], ValidateCandidateAsync = asyncValidator ?? (validator is null ? null : (value, _) => ValueTask.FromResult(validator(value))) }));
            var config = new Config(Root, policy, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
#if RETENTION_ATLAS
            config = config with { RepositoryType = ContractRepositoryType.AtlasCore };
            services.AddAtlasCoreRepository(config, new AtlasCoreRepositoryOptions
            {
                EnableBackgroundMaintenance = false, CheckpointOnDispose = false,
                StoreOptions = new AtlasCoreOptions
                {
#if !ATLAS_BASELINE
                    ExperimentVariant = (AtlasExperimentVariant)(segment ? 3 : 2),
#endif
                }
            });
#else
            if (segment) services.AddSegmentCoreRepository(config); else services.AddFileRepository(config);
#endif
            Services = services.BuildServiceProvider(); Repository = Services.GetRequiredService<IRepository<IItem>>();
        }
        internal async Task CompactAsync()
        {
#if RETENTION_ATLAS
            await Services.GetRequiredService<IAtlasCoreRepositoryMaintenance<IItem>>().CompactAsync();
#else
            if (_segment) await Services.GetRequiredService<ISegmentCoreRepositoryMaintenance<IItem>>().CompactAsync();
#endif
        }
        public async ValueTask DisposeAsync() { await Services.DisposeAsync(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    [StorageScope(VersionTag = "1.0.0")] [SearchIndex]
    public interface IItem : IStoredContract<IItem>
    {
        string Id { get; set; }
        string Name { get; set; }
        int Number { get; set; }
        byte[] Bytes { get; set; }
        List<string> Names { get; set; }
        [EncryptedStorageField] string? Secret { get; set; }
        Dictionary<string, int?> Counts { get; set; }
    }
    public class Item : IItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Number { get; set; }
        public byte[] Bytes { get; set; } = [];
        public List<string> Names { get; set; } = [];
        public string? Secret { get; set; }
        public Dictionary<string, int?> Counts { get; set; } = new();
        public IObjectEnvelope<IItem>? Envelope { get; protected set; }
    }
    public sealed class FieldItem : Item { public int Extra; }
    private sealed class TestKey : IKeyProvider
    {
        private readonly byte[] _bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        internal bool Deny;
        public ReadOnlyMemory<byte> GetCurrentKey() => Deny ? throw new UnauthorizedAccessException("test authorization revoked") : _bytes;
    }
    private sealed class ManualTime : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
    public interface IUnsupported : IStoredContract<IUnsupported> { string Id { get; set; } Detail Detail { get; set; } }
    public sealed record Detail(int Value);
    private sealed class DetailEquality : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => left is Detail a && right is Detail b ? a.Value == b.Value : left is null && right is null;
        public int GetHashCode(object? value) => value is Detail detail ? detail.Value : 0;
    }
    private sealed class CountsEquality : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => left is Dictionary<string,int?> a && right is Dictionary<string,int?> b
            ? a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value) : left is null && right is null;
        public int GetHashCode(object? value) => 0;
    }
    internal sealed record Config(string DataRoot, StoragePolicy StoragePolicy, ContractRepositoryType RepositoryType) : IDbContractConfigDto<IItem>
    {
        public string EnvelopeDICacheKey => "merge-tests";
        public string ContractName => "merge-tests";
        public string FileExtension => ".mpk";
        public string? ActiveVersion => "1.0.0";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
    }
}
