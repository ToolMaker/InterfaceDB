using System.Collections.Concurrent;
using System.Reflection;
using Idb.Diagnostics;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Microsoft.Extensions.DependencyInjection;
using static InterfaceDb.Tests.RepositoryNotificationTests;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class ReviewFollowupTests
{
    [TestMethod]
    [DataRow(false, false)] [DataRow(true, false)]
    [DataRow(false, true)] [DataRow(true, true)]
    public async Task LegacySubscriberFailureIsObservedAndOtherSubscribersContinue(bool segment, bool failingKeepAlive)
    {
        var telemetry = new Capture();
        await using var fixture = new Fixture(segment, physical: true, telemetry: telemetry);
        await fixture.Repository.InitializeAsync();
        var envelopes = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        var delivered = 0;
        using var bad = envelopes.SubscribeToStored(
            _ => failingKeepAlive ? ValueTask.CompletedTask : throw new InvalidOperationException("private callback"),
            failingKeepAlive ? () => throw new InvalidOperationException("private keepalive") : null);
        using var good = envelopes.SubscribeToStored(_ => { delivered++; return ValueTask.CompletedTask; });
        await fixture.Repository.SaveAsync(new Record { Id = "first", Value = 1 });
        await fixture.Repository.SaveAsync(new Record { Id = "second", Value = 2 });
        Assert.IsTrue(bad.IsDisposed);
        Assert.AreEqual(2, delivered);
        Assert.IsTrue(telemetry.Records.Any(r => r.Operation == DatabaseOperation.Publish
            && r.Outcome == DatabaseOutcome.Failure && r.Log));
        Assert.AreEqual(2, (await fixture.Repository.QuerySingleAsync(r => r.Id == "second")).Value);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LostIndexSubscriptionFallsBackToCorrectResults(bool segment)
    {
        await using var fixture = new Fixture(segment, cache: StorageObjectCacheMode.MetadataOnly, physical: true);
        await fixture.Repository.InitializeAsync();
        var index = fixture.Services.GetRequiredKeyedService<IIndexManager<IRecord>>("notifications");
        Assert.IsTrue(index.ExplainSearch(r => r.Id == "physical").UsesIndexes);
        var subscription = (IContractSubscription)index.GetType()
            .GetField("_storedSubscription", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(index)!;
        subscription.Dispose(); // Simulate loss of the channel used by physical writes.
        var envelopes = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        await envelopes.WriteAsync(new Record { Id = "physical", Value = 99 });
        var matches = new List<IRecord>();
        await foreach (var row in fixture.Repository.SearchManyAsync(r => r.Id == "physical")) matches.Add(row);
        Assert.HasCount(1, matches);
        var explanation = index.ExplainSearch(r => r.Id == "physical");
        Assert.IsFalse(explanation.UsesIndexes);
        StringAssert.Contains(explanation.FallbackReason!, "subscription");
    }

    [TestMethod]
    public async Task MissingFileLocationFailsWithActionableErrorBeforeIdentityAllocation()
    {
        var root = NewRoot();
        try
        {
            var services = new ServiceCollection();
            services.AddFileRepository(new Config(root, ContractRepositoryType.FileSystem, StoragePolicy.Default),
                storageIdentityFactory: new MissingLocationIdentity());
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepository<IRecord>>();
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => repository.SaveAsync(new Record { Id = "missing" }).AsTask());
            StringAssert.Contains(error.Message, "location must be initialized");
            Assert.IsFalse(Directory.EnumerateFiles(root, "*.mpk", SearchOption.AllDirectories).Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DurableDirectoryPublicationPrecedesPayloadAndRetriesFailedBarrier()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            var publication = new RecordingPublication();
            var policy = StoragePolicy.Default with { DurabilityMode = StorageDurabilityMode.Durable };
            var coordinator = new CoalescingFileWriteCoordinator<IRecord>(new NoopLock(),
                new Config(root, ContractRepositoryType.FileSystem, policy), policy, publication);
            var folder = Path.Combine(root, "new", "nested");
            var path = Path.Combine(folder, "record.mpk");
            var prepared = false;
            publication.FailDirectory = Path.Combine(root, "new");
            await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.CommitEnvelopeAsync(path, false,
                async (_, staged, token) => { prepared = true; await File.WriteAllBytesAsync(staged, [1], token); }).AsTask());
            Assert.IsFalse(prepared, "A failed ancestor barrier must precede payload preparation.");
            Assert.AreEqual(0, publication.Replacements);
            publication.FailDirectory = null;
            await coordinator.CommitEnvelopeAsync(path, false, async (_, staged, token) =>
            {
                Assert.IsTrue(publication.Directories.Contains(folder));
                Assert.IsTrue(publication.Directories.Contains(Path.Combine(root, "new")));
                Assert.IsTrue(publication.Directories.Contains(root));
                await File.WriteAllBytesAsync(staged, [1, 2, 3], token);
            });
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ClockPublishesItsDirectoryAncestorsBeforeIssuingRevision()
    {
        var root = NewRoot();
        try
        {
            var publication = new RecordingPublication { FailDirectory = Path.GetDirectoryName(root) };
            Assert.ThrowsExactly<IOException>(() => new StoreRevisionClock(root, publication).Next(0));
            Assert.AreEqual(0, publication.Replacements);
            publication.FailDirectory = null;
            Assert.IsGreaterThan(0L, new StoreRevisionClock(root, publication).Next(0));
            Assert.IsTrue(publication.Directories.Contains(Path.GetDirectoryName(root)!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "idb-review-followup-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void NativeDirectoryPublicationCreatesNestedPathsAndPreservesExistingContents()
    {
        var root = NewRoot();
        try
        {
            var nested = Path.Combine(root, "a", "b");
            PlatformFilePublication.Instance.EnsureDirectory(nested);
            var sentinel = Path.Combine(nested, "existing");
            File.WriteAllText(sentinel, "keep");
            PlatformFilePublication.Instance.EnsureDirectory(nested);
            Assert.AreEqual("keep", File.ReadAllText(sentinel));
            Assert.IsFalse(Directory.EnumerateDirectories(root, ".idb-directory-*", SearchOption.AllDirectories).Any());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ResolvingWatcherFirstDoesNotCreateAnUnpublishedRoot()
    {
        var root = NewRoot();
        try
        {
            var publication = new RecordingPublication();
            var services = new ServiceCollection();
            services.AddSingleton<IFilePublication>(publication);
            services.AddFileRepository(new Config(root, ContractRepositoryType.FileSystem,
                StoragePolicy.Default with { WatchStorageChanges = true, DurabilityMode = StorageDurabilityMode.Durable }));
            await using var provider = services.BuildServiceProvider();
            var source = provider.GetRequiredKeyedService<IEnvelopeStorageChangeSource<IRecord>>("notifications");
            Assert.IsFalse(Directory.Exists(root), "A path comparison probe must not create the storage root.");
            await source.StartAsync();
            Assert.IsTrue(Directory.Exists(root));
            Assert.IsTrue(publication.Directories.Contains(root));
            Assert.IsTrue(publication.Directories.Contains(Path.GetDirectoryName(root)!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MissingLocationIdentity : IEnvelopeStorageIdentityFactory<IRecord>
    {
        public StorageObjectCardinality ObjectCardinality => StorageObjectCardinality.Multiple;
        public void InitEnvelope(IObjectEnvelope<IRecord> envelope) { envelope.EnvelopeCacheKey = "missing"; envelope.Location = null; }
    }
    private sealed class NoopLock : IStorageWriteLock, IAsyncDisposable
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(string providerLocator, CancellationToken cancellationToken = default) => ValueTask.FromResult<IAsyncDisposable>(this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RecordingPublication : IFilePublication
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public string? FailDirectory;
        public int Replacements;
        public void FlushDirectory(string directoryPath)
        {
            if (directoryPath == FailDirectory) throw new IOException("Injected directory barrier failure.");
            Directories.Add(directoryPath);
        }
        public void Replace(string stagedPath, string destinationPath) { File.Move(stagedPath, destinationPath, true); Replacements++; }
        public void Delete(string path) => File.Delete(path);
    }
    private sealed class Capture : IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public ConcurrentQueue<DatabaseObservation> Records { get; } = new();
        public void TryRecord(in DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(DatabaseGauge gauge, long delta) { }
    }
}
