using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RetainedEditingTests
{
    [TestMethod]
    [DataRow(false, StorageObjectCacheMode.StrongObject)]
    [DataRow(false, StorageObjectCacheMode.WeakObject)]
    [DataRow(true, StorageObjectCacheMode.StrongObject)]
    [DataRow(true, StorageObjectCacheMode.WeakObject)]
    public async Task OrdinaryCachedReadsPublishOnlySuccessfulSaves(bool segment, StorageObjectCacheMode cache)
    {
        await using var fixture = new EditFixture(segment, cache: cache);
        var input = new Record { Id = "one", Value = 1, Names = ["B"], Bytes = [1] };
        var saved = await ((IEnvelopeMetadataRepository<IRecord>)fixture.Repository)
            .SaveWithExtensionMetadataAsync(input, null, [1]);
        input.Names[0] = "unsaved input";
        saved.Value = 99;
        saved.Bytes[0] = 99;
        var reader = (await fixture.Repository.QueryFirstByStorageKeyAsync(saved.Envelope!.EnvelopeCacheKey))!;
        Assert.AreEqual(1, reader.Value);
        Assert.AreEqual("B", reader.Names[0]);
        Assert.AreEqual((byte)1, reader.Bytes[0]);
        Assert.AreEqual((byte)1, reader.Envelope!.ExtensionMetadata![0]);
        reader.Envelope.ExtensionMetadata[0] = 2;
        reader.Value = 2; reader.Names[0] = "C"; reader.Bytes[0] = 2;
        Assert.IsNull(await fixture.Repository.SearchFirstAsync(row => row.Value == 2));
        var original = (await fixture.Repository.SearchFirstAsync(row => row.Value == 1))!;
        Assert.IsNotNull(original);
        Assert.AreNotSame(reader, original);
        Assert.AreNotSame(reader.Names, original.Names);
        Assert.AreEqual("B", original.Names[0]);
        Assert.AreEqual((byte)1, original.Envelope!.ExtensionMetadata![0]);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Repository.SaveAsync(reader, cancelled.Token).AsTask());
        }
        Assert.AreEqual(1, (await fixture.Repository.QueryFirstAsync(row => row.Id == "one"))!.Value);
        await fixture.Repository.SaveAsync(reader);
        reader.Value = 3; reader.Names[0] = "later draft"; reader.Bytes[0] = 3;
        reader.Envelope.ExtensionMetadata![0] = 3;
        var committed = (await fixture.Repository.SearchFirstAsync(row => row.Value == 2))!;
        Assert.IsNotNull(committed);
        Assert.AreEqual("C", committed.Names[0]);
        Assert.AreEqual((byte)2, committed.Bytes[0]);
        Assert.AreEqual((byte)2, committed.Envelope!.ExtensionMetadata![0]);
        original.Value = 4;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => fixture.Repository.SaveAsync(original).AsTask());
        Assert.IsNull(await fixture.Repository.SearchFirstAsync(row => row.Value == 4));
        Assert.AreEqual(2, (await fixture.Repository.QueryFirstAsync(row => row.Id == "one"))!.Value);
        GC.KeepAlive(input); GC.KeepAlive(saved); GC.KeepAlive(reader);
    }

    [TestMethod]
    [DataRow(false, StorageObjectCacheMode.StrongObject, false)]
    [DataRow(false, StorageObjectCacheMode.StrongObject, true)]
    [DataRow(false, StorageObjectCacheMode.WeakObject, false)]
    [DataRow(false, StorageObjectCacheMode.WeakObject, true)]
    [DataRow(true, StorageObjectCacheMode.StrongObject, false)]
    [DataRow(true, StorageObjectCacheMode.StrongObject, true)]
    [DataRow(true, StorageObjectCacheMode.WeakObject, false)]
    [DataRow(true, StorageObjectCacheMode.WeakObject, true)]
    public async Task CachedReadersSeeCommittedValuesUntilIsolatedEditIsSaved(
        bool segment, StorageObjectCacheMode cache, bool commit)
    {
        await using var fixture = new EditFixture(segment, cache: cache);
        var original = await fixture.Repository.SaveAsync(
            new Record { Id = "one", Value = 1, Names = ["original"], Bytes = [1] });
        await using var edit = await fixture.Repository.BeginEditAsync(original.Envelope!.EnvelopeCacheKey);
        edit.Working.Value = 2;
        edit.Working.Names[0] = "editing";
        edit.Working.Bytes[0] = 2;

        var duringEdit = (await fixture.Repository.QueryFirstAsync(row => row.Id == "one"))!;
        Assert.AreNotSame(edit.Working, duringEdit);
        Assert.AreNotSame(edit.Working.Names, duringEdit.Names);
        Assert.AreNotSame(edit.Working.Bytes, duringEdit.Bytes);
        Assert.AreEqual(1, duringEdit.Value);
        Assert.AreEqual("original", duringEdit.Names[0]);
        Assert.AreEqual((byte)1, duringEdit.Bytes[0]);
        Assert.IsNotNull(await fixture.Repository.SearchFirstAsync(row => row.Value == 1));
        Assert.IsNull(await fixture.Repository.SearchFirstAsync(row => row.Value == 2));

        if (commit) await edit.SaveAsync();
        else await edit.DisposeAsync();

        // The edit buffer must not become the published cache object after save either.
        edit.Working.Value = 3;
        edit.Working.Names[0] = "later draft";
        edit.Working.Bytes[0] = 3;
        var expected = commit ? 2 : 1;
        var afterEdit = (await fixture.Repository.QueryFirstAsync(row => row.Id == "one"))!;
        Assert.AreEqual(expected, afterEdit.Value);
        Assert.AreEqual(commit ? "editing" : "original", afterEdit.Names[0]);
        Assert.AreEqual((byte)expected, afterEdit.Bytes[0]);
        Assert.IsNotNull(await fixture.Repository.SearchFirstAsync(row => row.Value == expected));
        Assert.IsNull(await fixture.Repository.SearchFirstAsync(row => row.Value == 3));
        GC.KeepAlive(original);
        GC.KeepAlive(duringEdit);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CommittedBaselineAndWorkingGraphsAreIsolatedAndSuccessfulSessionIsSingleUse(bool segment)
    {
        await using var fixture = new EditFixture(segment);
        var original = await fixture.Repository.SaveAsync(new Record { Id = "one", Names = ["original"], Bytes = [1] });
        original.Names[0] = "cached edit"; original.Bytes[0] = 99;
        await using var edit = await fixture.Repository.BeginEditAsync(original.Envelope!.EnvelopeCacheKey);
        Assert.AreEqual("original", edit.Working.Names[0]); Assert.AreEqual((byte)1, edit.Working.Bytes[0]);
        edit.Working.Names[0] = "working";
        Assert.AreEqual("original", (await edit.ReadOriginalAsync()).Names[0]);
        var saved = await edit.SaveAsync(); Assert.AreEqual("working", saved.Names[0]);
        saved.Value = 10; await fixture.Repository.SaveAsync(saved);
        Assert.IsTrue(edit.IsCompleted);
        Assert.AreEqual(0, fixture.Editing.RetentionStatistics.ActiveSessions);
        await Assert.ThrowsExactlyAsync<RetainedVersionUnavailableException>(() => edit.SaveAsync().AsTask());
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task StrictRejectionRetainsOriginalAndNewerTokenCannotBeInjected(bool segment)
    {
        await using var fixture = new EditFixture(segment);
        var original = await fixture.Repository.SaveAsync(new Record { Id = "one" });
        await using var edit = await fixture.Repository.BeginEditAsync(original.Envelope!.EnvelopeCacheKey);
        original.Value = 5; await fixture.Repository.SaveAsync(original);
        edit.Working.Language = "changed";
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => edit.SaveAsync().AsTask());
        Assert.AreEqual(0, (await edit.ReadOriginalAsync()).Value);
        Assert.AreEqual(1, fixture.Editing.RetentionStatistics.ActiveSessions);
        edit.Working.Envelope!.RevisionTimestampUtcMicroseconds++;
        await Assert.ThrowsExactlyAsync<RetainedVersionUnavailableException>(() => edit.SaveAsync().AsTask());
        edit.Working.Envelope.RevisionTimestampUtcMicroseconds = edit.ExpectedRevision;
        edit.Working.Envelope.VersionTag = "changed-schema";
        await Assert.ThrowsExactlyAsync<RetainedVersionUnavailableException>(() => edit.SaveAsync().AsTask());
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task CustomFreeNoChangeSkipsWriteAndEvents(bool segment)
    {
        await using var fixture = new EditFixture(segment, new ReturnCurrent());
        var original = await fixture.Repository.SaveAsync(new Record { Id = "one" });
        var revision = original.Envelope!.RevisionTimestampUtcMicroseconds;
        var events = 0;
        using var subscription = fixture.Services.GetRequiredService<IRepositoryNotifications<IRecord>>()
            .SubscribeChanged(_ => { events++; return ValueTask.CompletedTask; }, _ => true);
        await using var edit = await fixture.Repository.BeginEditAsync(original.Envelope.EnvelopeCacheKey);
        edit.Working.Value = 99;
        var saved = await edit.SaveAsync(); await subscription.WaitForIdleAsync();
        Assert.AreEqual(revision, saved.Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual(0, saved.Value); Assert.AreEqual(0, events);
        Assert.AreEqual(0, (await fixture.Repository.QueryFirstAsync(x => x.Id == "one"))!.Value);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task DeleteRecreateRejectsRetainedEditorEvenWithPermissiveStrategy(bool segment)
    {
        await using var fixture = new EditFixture(segment, new AcceptAny());
        var original = await fixture.Repository.SaveAsync(new Record { Id = "one" });
        await using var edit = await fixture.Repository.BeginEditAsync(original.Envelope!.EnvelopeCacheKey);
        await fixture.Repository.DeleteAsync(original);
        await fixture.Repository.SaveAsync(new Record { Id = "one" });
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => edit.SaveAsync().AsTask());
    }

    private sealed class ReturnCurrent : IWriteConflictStrategy<IRecord>
    {
        public async ValueTask<EnvelopeWriteDecision<IRecord>> DecideAsync(EnvelopeWriteConflict<IRecord> context, CancellationToken cancellationToken = default)
            => context.IsInsert || context.Candidate.ObjectInstance!.Value != 99 ? new EnvelopeWriteDecision<IRecord>(true)
                : new EnvelopeWriteNoChange<IRecord>((await context.ReadCurrentAsync(cancellationToken))!.Envelope!);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task MixedNoChangeBatchStoresOnlyAcceptedMutations(bool segment)
    {
        await using var f = new EditFixture(segment, new ReturnCurrent());
        var a = await f.Repository.SaveAsync(new Record { Id = "one" });
        var b = await f.Repository.SaveAsync(new Record { Id = "two" });
        var originalRevision = a.Envelope!.RevisionTimestampUtcMicroseconds;
        a.Value = 99; b.Value = 7;
        var saved = await f.Repository.SaveManyAsync([a, b]);
        Assert.AreEqual(0, saved[0].Value); Assert.AreEqual(originalRevision, saved[0].Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual(7, saved[1].Value);
    }

    [TestMethod] [DataRow(false)] [DataRow(true)]
    public async Task StrictSessionPreservesAcknowledgementMetadataAtMatchingRevision(bool segment)
    {
        await using var f = new EditFixture(segment);
        var metadata = (IEnvelopeMetadataRepository<IRecord>)f.Repository;
        var seed = await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "one" }, null, [1]);
        await using var edit = await f.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
        await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "one" }, seed.Envelope.EnvelopeCacheKey, [2],
            new EnvelopeWriteCondition(seed.Envelope.RevisionTimestampUtcMicroseconds, EnvelopeRevisionAction.MetadataOnly, ExpectedExtensionMetadata: [1]));
        edit.Working.Value = 7;
        var saved = await edit.SaveAsync();
        CollectionAssert.AreEqual(new byte[] { 2 }, saved.Envelope!.ExtensionMetadata);
    }

    [TestMethod]
    public async Task HistoryCannotBecomeLiveDataEvenWhenPayloadExtensionMatchesTheArchiveExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-history-namespace-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = new EditFixture(false, root: root, extension: ".version");
            var seed = await first.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
            await using var edit = await first.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
            seed.Value = 2; await first.Repository.SaveAsync(seed);
            var archive = Directory.GetFiles(Path.Combine(root, ".idb-history"), "*.version", SearchOption.AllDirectories).Single();
            await using var second = new EditFixture(false, root: root, extension: ".version", hydrate: true);
            await second.Repository.InitializeAsync();
            var values = new List<IRecord>();
            await foreach (var value in second.Repository.QueryManyAsync(_ => true)) values.Add(value);
            Assert.AreEqual(1, values.Count); Assert.AreEqual(2, values[0].Value);
            await using var changes = new FileEnvelopeStorageChangeSource<IRecord>(second.Configuration, second.Configuration.StoragePolicy!);
            var events = 0; changes.Changed += _ => { events++; return ValueTask.CompletedTask; };
            await changes.StartAsync();
            File.SetLastWriteTimeUtc(archive, DateTime.UtcNow.AddSeconds(1));
            await changes.RescanAsync(); Assert.AreEqual(0, events);
            seed.Envelope!.Location = archive;
            await Assert.ThrowsAsync<Exception>(() => first.Repository.SaveAsync(seed).AsTask());
            Assert.AreEqual(1, (await edit.ReadOriginalAsync()).Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ReservedFileMarkerExtensionRejectsEditingBeforeCapture()
    {
        await using var f = new EditFixture(false, extension: ".edit-incarnation");
        Assert.IsFalse(f.Editing.RetainedVersions.Supported);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => f.Repository.BeginEditAsync("one").AsTask());
        Assert.AreEqual(0, f.Editing.RetentionStatistics.RetainedRecords);
    }

    [TestMethod] [DataRow(StorageDurabilityMode.Durable)] [DataRow(StorageDurabilityMode.Performance)]
    public async Task FileInstancesPreserveEachOthersBaselinesAndDeleteRecreateInvalidatesThem(StorageDurabilityMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "idb-retained-instances-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = new EditFixture(false, new AcceptAny(), root: root, durability: mode);
            await using var second = new EditFixture(false, new AcceptAny(), root: root, durability: mode);
            var seed = await first.Repository.SaveAsync(new Record { Id = "one", Value = 1 });
            await using var a = await first.Repository.BeginEditAsync(seed.Envelope!.EnvelopeCacheKey);
            await using var b = await second.Repository.BeginEditAsync(seed.Envelope.EnvelopeCacheKey);
            b.Working.Value = 2; var next = await b.SaveAsync();
            Assert.AreEqual(1, (await a.ReadOriginalAsync()).Value);
            await second.Repository.DeleteAsync(next);
            await second.Repository.SaveAsync(new Record { Id = "one", Value = 3 });
            await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => a.SaveAsync().AsTask());
            Assert.AreEqual(1, (await a.ReadOriginalAsync()).Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class AcceptAny : IWriteConflictStrategy<IRecord>
    { public ValueTask<EnvelopeWriteDecision<IRecord>> DecideAsync(EnvelopeWriteConflict<IRecord> context, CancellationToken cancellationToken = default) => ValueTask.FromResult(new EnvelopeWriteDecision<IRecord>(true)); }

    internal sealed class EditFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "idb-edit-tests-" + Guid.NewGuid().ToString("N"));
        internal ServiceProvider Services { get; }
        internal IRepository<IRecord> Repository { get; }
        internal IRevisionEditingRepository<IRecord> Editing => (IRevisionEditingRepository<IRecord>)Repository;
        internal IDbContractConfigDto<IRecord> Configuration { get; }
        private readonly bool _deleteRoot;
        internal EditFixture(bool segment, IWriteConflictStrategy<IRecord>? strategy = null, RetainedVersionOptions? options = null,
            string? root = null, StorageDurabilityMode durability = StorageDurabilityMode.Durable, string extension = ".mpk", bool hydrate = false,
            StorageObjectCacheMode cache = StorageObjectCacheMode.StrongObject,
            IObjectSerializer<IRecord>? serializer = null)
        {
            _deleteRoot = root is null;
            if (root is not null) Root = root;
            var policy = StoragePolicy.Default with { RetainedVersions = options ?? new(), RaiseNotifications = false,
                DurabilityMode = durability,
                HydrateOnInitialize = hydrate,
                WatchStorageChanges = false, ObjectCacheMode = cache, RepositoryNotifications = new() };
            IDbContractConfigDto<IRecord> config = new ExtensionConfig(new Config(Root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy), extension);
            Configuration = config;
            var services = new ServiceCollection();
            if (serializer is not null) services.AddKeyedSingleton(config.EnvelopeDICacheKey, serializer);
            if (strategy is not null) services.AddSingleton(strategy);
            if (segment) services.AddSegmentCoreRepository(config); else services.AddFileRepository(config);
            Services = services.BuildServiceProvider(); Repository = Services.GetRequiredService<IRepository<IRecord>>();
        }
        public async ValueTask DisposeAsync() { await Services.DisposeAsync(); if (_deleteRoot && Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed record ExtensionConfig(IDbContractConfigDto<IRecord> Inner, string FileExtension) : IDbContractConfigDto<IRecord>
    {
        public string DataRoot => Inner.DataRoot!;
        public StoragePolicy StoragePolicy => Inner.StoragePolicy!;
        public ContractRepositoryType RepositoryType => Inner.RepositoryType;
        public string EnvelopeDICacheKey => Inner.EnvelopeDICacheKey;
        public string ContractName => Inner.ContractName;
        public string? ActiveVersion => Inner.ActiveVersion;
        public string? ImplementationsPath => Inner.ImplementationsPath;
        public string? ContractPath => Inner.ContractPath;
        public string? MigrationsPath => Inner.MigrationsPath;
    }
}
