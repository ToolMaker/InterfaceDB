using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDb.Tests;

[TestClass]
public sealed partial class RepositoryNotificationTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExplicitKeyCreateWithMetadataPublishesCreatedAndStillRejectsDuplicate(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var created = new ConcurrentQueue<IRecord>();
        using var subscription = fixture.Notifications.SubscribeCreated(entity => { created.Enqueue(entity); return ValueTask.CompletedTask; });
        var metadata = (IEnvelopeMetadataRepository<IRecord>)fixture.Repository;
        var saved = await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "explicit", Value = 4 }, "explicit", [1],
            new EnvelopeWriteCondition(0, ExpectedMissing: true));
        await Idle(subscription);
        Assert.AreEqual(1, created.Count);
        Assert.AreEqual(4, created.Single().Value);
        Assert.IsFalse(string.IsNullOrEmpty(saved.Envelope!.Location));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(async () =>
            await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "explicit", Value = 99 }, "explicit", [2],
                new EnvelopeWriteCondition(0, ExpectedMissing: true)));
        await Idle(subscription);
        Assert.AreEqual(1, created.Count);
        Assert.AreEqual(4, (await fixture.Repository.QuerySingleAsync(_ => true)).Value);
    }

    [TestMethod]
    [DataRow(false, StorageObjectCacheMode.MetadataOnly, false)]
    [DataRow(false, StorageObjectCacheMode.WeakObject, true)]
    [DataRow(false, StorageObjectCacheMode.StrongObject, true)]
    [DataRow(true, StorageObjectCacheMode.MetadataOnly, true)]
    [DataRow(true, StorageObjectCacheMode.WeakObject, false)]
    [DataRow(true, StorageObjectCacheMode.StrongObject, false)]
    public async Task EventsHaveCommittedValuesAndCorrectEnvelopeLifetimes(bool segment, StorageObjectCacheMode cache, bool physical)
    {
        await using var fixture = new Fixture(segment, cache, physical);
        var created = new ConcurrentQueue<IRecord>();
        var changes = new ConcurrentQueue<RepositoryChange<IRecord>>();
        var deleted = new ConcurrentQueue<IRecord>();
        using var a = fixture.Notifications.SubscribeCreated(entity => { created.Enqueue(entity); return ValueTask.CompletedTask; });
        using var b = fixture.Notifications.SubscribeChanged(change => { changes.Enqueue(change); return ValueTask.CompletedTask; }, entity => entity.Language == "de");
        using var c = fixture.Notifications.SubscribeDeleted(entity => { deleted.Enqueue(entity); return ValueTask.CompletedTask; }, entity => entity.Language == "de");
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "one", Language = "de", Value = 1, Bytes = [1, 2], Names = ["a"] });
        await Idle(a, b, c);
        Assert.AreEqual(1, created.Count);
        Assert.AreEqual(0, changes.Count);
        Assert.IsNotNull(created.Single().Envelope);
        Assert.AreNotSame(saved, created.Single());
        Assert.AreNotSame(saved.Envelope, created.Single().Envelope);

        saved.Value = 2; saved.Bytes[0] = 9; saved.Names.Add("b");
        saved = await fixture.Repository.SaveAsync(saved);
        await Idle(a, b, c);
        CollectionAssert.AreEqual(new[] { "Bytes", "Names", "Value" }, changes.Single().ChangedProperties.ToArray());
        Assert.IsNotNull(changes.Single().Entity.Envelope);
        Assert.AreEqual(1, created.Single().Value, "A queued/retained event must not track later writes.");
        await fixture.Repository.SaveAsync(saved);
        await Idle(a, b, c);
        Assert.AreEqual(1, changes.Count, "No-op saves must not notify Changed.");
        saved.Value = 999;
        Assert.IsTrue(await fixture.Repository.DeleteAsync(saved));
        await Idle(a, b, c);
        Assert.AreEqual(2, deleted.Single().Value, "Deletion must use committed, not caller-edited, values.");
        Assert.IsNull(deleted.Single().Envelope);
        Assert.IsNotNull(saved.Envelope, "Clearing event identity must not clear the caller's envelope.");
        Assert.IsFalse(await fixture.Repository.DeleteAsync(saved));
        await Idle(a, b, c);
        Assert.AreEqual(1, deleted.Count);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SubscriberPayloadsAndIndexesAreIsolatedAndReady(bool segment)
    {
        await using var fixture = new Fixture(segment);
        using var mutation = fixture.Notifications.SubscribeCreated(entity =>
        { entity.Value = 999; entity.Bytes[0] = 255; entity.Envelope!.Location = "invalid"; return ValueTask.CompletedTask; });
        var read = new TaskCompletionSource<IRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var other = fixture.Notifications.SubscribeCreated(async entity =>
        {
            try
            {
                var found = await fixture.Repository.SearchFirstAsync(record => record.Id == entity.Id);
                Assert.IsNotNull(found); Assert.AreEqual(3, found.Value);
                read.TrySetResult(entity);
            }
            catch (Exception exception) { read.TrySetException(exception); throw; }
        });
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "indexed", Value = 3, Bytes = [4] });
        var received = await read.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Idle(mutation, other);
        Assert.AreEqual(3, received.Value); Assert.AreEqual(4, received.Bytes[0]);
        Assert.AreEqual(3, saved.Value); Assert.AreNotEqual("invalid", saved.Envelope!.Location);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task FiltersApplyToAfterStateAndMetadataDoesNotProduceChanges(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var metadata = (IEnvelopeMetadataRepository<IRecord>)fixture.Repository;
        var changes = new ConcurrentQueue<RepositoryChange<IRecord>>();
        using var sub = fixture.Notifications.SubscribeChanged(change => { changes.Enqueue(change); return ValueTask.CompletedTask; }, record => record.Language == "de");
        var entity = await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "meta", Language = "de" }, null, [1]);
        entity = await metadata.SaveWithExtensionMetadataAsync(new Record { Id = "meta", Language = "de" }, entity.Envelope!.EnvelopeCacheKey, [2], new EnvelopeWriteCondition(entity.Envelope!.RevisionTimestampUtcMicroseconds));
        await Idle(sub); Assert.AreEqual(0, changes.Count);
        entity.Language = "fr"; await fixture.Repository.SaveAsync(entity);
        await Idle(sub); Assert.AreEqual(0, changes.Count); Assert.AreEqual(1L, sub.FilteredCount);
        entity.Language = "de"; await fixture.Repository.SaveAsync(entity);
        await Idle(sub); CollectionAssert.AreEqual(new[] { "Language" }, changes.Single().ChangedProperties.ToArray());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LifecycleCancellationKeepAliveAndFaultsDoNotFailSaves(bool segment)
    {
        await using var fixture = new Fixture(segment);
        using var token = new CancellationTokenSource(); token.Cancel();
        using var cancelled = fixture.Notifications.SubscribeCreated(_ => throw new AssertFailedException(), lifetime: token.Token);
        Assert.AreEqual(RepositorySubscriptionEndReason.Cancelled, await cancelled.Completion);
        using var dead = fixture.Notifications.SubscribeCreated(_ => throw new AssertFailedException(), keepAlive: () => ValueTask.FromResult(false));
        using var faulty = fixture.Notifications.SubscribeCreated(_ => throw new InvalidOperationException("Handler fault"));
        using var healthy = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "lifetime" });
        Assert.IsNotNull(saved.Envelope);
        await Idle(cancelled, dead, faulty, healthy);
        Assert.AreEqual(RepositorySubscriptionEndReason.KeepAliveExpired, await dead.Completion);
        Assert.AreEqual(RepositorySubscriptionEndReason.CallbackFailed, await faulty.Completion);
        Assert.AreEqual(1L, healthy.DeliveredCount);
        healthy.Dispose(); healthy.Dispose();
        await fixture.Repository.SaveAsync(new Record { Id = "after-dispose" });
        Assert.AreEqual(1L, healthy.DeliveredCount);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReentrantSaveAndSelfDisposalDoNotDeadlock(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRepositorySubscription? subscription = null;
        subscription = fixture.Notifications.SubscribeCreated(async entity =>
        {
            subscription!.Dispose();
            await fixture.Repository.SaveAsync(new Record { Id = "nested" });
            done.TrySetResult();
        });
        await fixture.Repository.SaveAsync(new Record { Id = "outer" });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Idle(subscription);
        Assert.AreEqual(1L, subscription.DeliveredCount);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task OverflowIsObservableAndDoesNotBlockStorage(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { QueueCapacity = 1 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = fixture.Notifications.SubscribeCreated(async _ => { entered.TrySetResult(); await release.Task; });
        try
        {
            await fixture.Repository.SaveAsync(new Record { Id = "blocked" });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await fixture.Repository.SaveAsync(new Record { Id = "queued" });
            await fixture.Repository.SaveAsync(new Record { Id = "overflow" });
            Assert.AreEqual(RepositorySubscriptionEndReason.QueueOverflow, await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally { release.TrySetResult(); }
        await Idle(subscription);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task BatchAndConcurrentMutationsMaintainPreciseChanges(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { QueueCapacity = 1024 });
        var created = new ConcurrentQueue<IRecord>();
        var changed = new ConcurrentQueue<RepositoryChange<IRecord>>();
        using var a = fixture.Notifications.SubscribeCreated(entity => { created.Enqueue(entity); return ValueTask.CompletedTask; });
        using var b = fixture.Notifications.SubscribeChanged(entity => { changed.Enqueue(entity); return ValueTask.CompletedTask; }, _ => true);
        var records = await fixture.Repository.SaveManyAsync(Enumerable.Range(0, 10).Select(index => new Record { Id = index.ToString() }));
        await Idle(a, b); Assert.AreEqual(10, created.Count);
        var metadata = (IEnvelopeMetadataRepository<IRecord>)fixture.Repository;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => Task.WhenAll(Enumerable.Range(1, 10).Select(value => metadata.SaveWithExtensionMetadataAsync(
            new Record { Id = records[0].Id, Value = value }, records[0].Envelope!.EnvelopeCacheKey, null, new EnvelopeWriteCondition(records[0].Envelope!.RevisionTimestampUtcMicroseconds)).AsTask())));
        await Idle(a, b);
        Assert.AreEqual(1, changed.Count);
        Assert.IsTrue(changed.All(change => change.ChangedProperties.SequenceEqual(new[] { "Value" })));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DisabledAndZeroSubscriberModesPreserveSaveResults(bool segment)
    {
        await using var disabled = new Fixture(segment, enabled: false);
        Assert.ThrowsExactly<InvalidOperationException>(() => disabled.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask));
        var entity = await disabled.Repository.SaveAsync(new Record { Id = "disabled" });
        Assert.IsNotNull(entity.Envelope);
        await using var enabled = new Fixture(segment);
        var saved = await enabled.Repository.SaveAsync(new Record { Id = "zero" });
        saved.Value = 7;
        using var subscriber = enabled.Notifications.SubscribeChanged(change =>
        { Assert.AreEqual(7, change.Entity.Value); return ValueTask.CompletedTask; }, _ => true);
        await enabled.Repository.SaveAsync(saved);
        await Idle(subscriber); Assert.AreEqual(1L, subscriber.DeliveredCount);
    }

    [TestMethod]
    public async Task FiltersRejectEnvelopeAccessAndInvokedCode()
    {
        await using var fixture = new Fixture(false);
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, entity => entity.Envelope != null));
        Func<IRecord, bool> arbitrary = _ => true;
        Assert.ThrowsExactly<ArgumentException>(() => fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, entity => arbitrary(entity)));
    }

    private static async Task Idle(params IRepositorySubscription[] subscriptions)
    {
        foreach (var subscription in subscriptions)
            await subscription.WaitForIdleAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task HandlerWritesDoNotEchoButOtherObserversReceiveThem(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "echo" });
        var seen = new ConcurrentQueue<int>();
        using var writer = fixture.Notifications.SubscribeChanged(async change =>
        {
            change.Entity.Value++;
            await fixture.Repository.SaveAsync(change.Entity);
        }, _ => true);
        using var observer = fixture.Notifications.SubscribeChanged(change =>
        { seen.Enqueue(change.Entity.Value); return ValueTask.CompletedTask; }, _ => true);
        saved.Value = 1;
        await fixture.Repository.SaveAsync(saved);
        await Idle(writer, observer);
        Assert.AreEqual(1L, writer.DeliveredCount);
        CollectionAssert.AreEqual(new[] { 1, 2 }, seen.ToArray());
        Assert.IsFalse(writer.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExplicitWriteScopeSuppressesItsOwnerAcrossAwaitAndThenRestores(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "scope" });
        using var owner = fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true);
        using var other = fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true);
        using (owner.BeginWriteScope())
        {
            await Task.Yield();
            saved.Value = 1;
            await fixture.Repository.SaveAsync(saved);
        }
        await Idle(owner, other);
        Assert.AreEqual(0L, owner.DeliveredCount); Assert.AreEqual(1L, other.DeliveredCount);
        saved.Value = 2;
        await fixture.Repository.SaveAsync(saved);
        await Idle(owner, other);
        Assert.AreEqual(1L, owner.DeliveredCount); Assert.AreEqual(2L, other.DeliveredCount);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task CausalChainStopsTwoSubscriberFeedback(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "cycle" });
        using var first = fixture.Notifications.SubscribeChanged(async change =>
        {
            change.Entity.Value++;
            await fixture.Repository.SaveAsync(change.Entity);
        }, _ => true);
        using var second = fixture.Notifications.SubscribeChanged(async change =>
        {
            change.Entity.Value++;
            await fixture.Repository.SaveAsync(change.Entity);
        }, _ => true);
        // Start with a write attributed to first: first -> second -> first must stop.
        using (first.BeginWriteScope())
        {
            saved.Value = 1;
            await fixture.Repository.SaveAsync(saved);
        }
        await Idle(second, first);
        Assert.AreEqual(0L, first.DeliveredCount); Assert.AreEqual(1L, second.DeliveredCount);
        Assert.IsFalse(first.IsDisposed); Assert.IsFalse(second.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ExplicitEchoOptInIsBoundedByCausalDepth(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { MaximumCausalDepth = 3 });
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "bounded-echo" });
        using var writer = fixture.Notifications.SubscribeChanged(async change =>
        {
            change.Entity.Value++;
            await fixture.Repository.SaveAsync(change.Entity);
        }, _ => true, receiveOwnWrites: true);
        saved.Value = 1;
        await fixture.Repository.SaveAsync(saved);
        Assert.AreEqual(RepositorySubscriptionEndReason.CausalDepthExceeded,
            await writer.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
        await Idle(writer);
        Assert.AreEqual(3L, writer.DeliveredCount);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task PhysicalCallbackReentryFailsExplicitlyInsteadOfDeadlocking(bool segment)
    {
        await using var fixture = new Fixture(segment, physical: true, durable: true);
        var physical = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        Exception? rejected = null;
        using var callback = physical.SubscribeToAdded(async _ =>
        {
            try { await fixture.Repository.SaveAsync(new Record { Id = "nested-physical" }); }
            catch (Exception error) { rejected = error; }
        });
        using var logical = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "outer-physical" }).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        await Idle(logical);
        Assert.IsInstanceOfType<InvalidOperationException>(rejected);
        Assert.IsNotNull(saved.Envelope); Assert.AreEqual(1L, logical.DeliveredCount);
    }

    [TestMethod]
    public async Task CallbackCannotDrainAnotherMailboxAndDeadlockSingleCallbackSlot()
    {
        await using var fixture = new Fixture(false, options: new() { MaximumConcurrentCallbacks = 1 });
        using var other = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        Exception? rejected = null;
        using var subscriber = fixture.Notifications.SubscribeCreated(async _ =>
        {
            try { await other.WaitForIdleAsync(); }
            catch (Exception error) { rejected = error; }
        });
        await fixture.Repository.SaveAsync(new Record { Id = "drain-cycle" });
        await Idle(subscriber, other);
        Assert.IsInstanceOfType<InvalidOperationException>(rejected);
        Assert.IsFalse(subscriber.IsDisposed);
    }

    [TestMethod]
    public async Task DurableFilePartialBatchNotifiesOnlySuccessfulObjectsAfterIndexRefresh()
    {
        await using var fixture = new Fixture(false, durable: true, serializer: new FailingSerializer());
        var observed = new ConcurrentQueue<string>();
        using var subscriber = fixture.Notifications.SubscribeCreated(async entity =>
        {
            Assert.IsNotNull(await fixture.Repository.SearchFirstAsync(item => item.Id == entity.Id));
            observed.Enqueue(entity.Id);
        });
        var error = await Assert.ThrowsExactlyAsync<StorageBulkOperationException>(() => fixture.Repository.SaveManyAsync(
            new IRecord[] { new Record { Id = "before" }, new Record { Id = "fail" }, new Record { Id = "after" } }).AsTask());
        await Idle(subscriber);
        CollectionAssert.AreEqual(new[] { 0, 2 }, error.SucceededIndexes.ToArray());
        CollectionAssert.AreEquivalent(new[] { "before", "after" }, observed.ToArray());
        Assert.IsFalse(subscriber.IsDisposed);
    }

    [TestMethod]
    public async Task SegmentAtomicBatchFailureDoesNotInventNotifications()
    {
        await using var fixture = new Fixture(true, serializer: new FailingSerializer());
        using var subscriber = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        await Assert.ThrowsAsync<Exception>(() => fixture.Repository.SaveManyAsync(
            new IRecord[] { new Record { Id = "before" }, new Record { Id = "fail" }, new Record { Id = "after" } }).AsTask());
        await Idle(subscriber);
        Assert.AreEqual(0L, subscriber.DeliveredCount);
        Assert.IsNull(await fixture.Repository.SearchFirstAsync(item => item.Id == "before"));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task EncryptedPayloadsKeepReadableValuesAndDeleteIsDetached(bool segment)
    {
        var root = Path.Combine(Path.GetTempPath(), "InterfaceDB-NotificationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddInterfaceDbAesGcmEncryption(new EphemeralKey());
            var config = new SecretConfig(root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
            RegisterProvider(services, config, segment);
            await using var provider = services.BuildServiceProvider();
            var repository = provider.GetRequiredService<IRepository<ISecret>>();
            var notifications = provider.GetRequiredService<IRepositoryNotifications<ISecret>>();
            var created = new List<ISecret>(); var changed = new List<RepositoryChange<ISecret>>(); var deleted = new List<ISecret>();
            using var a = notifications.SubscribeCreated(entity => { created.Add(entity); return ValueTask.CompletedTask; });
            using var b = notifications.SubscribeChanged(change => { changed.Add(change); return ValueTask.CompletedTask; }, _ => true);
            using var c = notifications.SubscribeDeleted(entity => { deleted.Add(entity); return ValueTask.CompletedTask; });
            var saved = await repository.SaveAsync(new Secret { Value = "first" });
            await Idle(a, b, c);
            Assert.AreEqual("first", created.Single().Value); Assert.IsNotNull(created.Single().Envelope);
            saved.Value = "second";
            saved = await repository.SaveAsync(saved);
            await Idle(a, b, c);
            CollectionAssert.AreEqual(new[] { "Value" }, changed.Single().ChangedProperties.ToArray());
            Assert.AreEqual("second", changed.Single().Entity.Value);
            await repository.DeleteAsync(saved);
            await Idle(a, b, c);
            Assert.AreEqual("second", deleted.Single().Value); Assert.IsNull(deleted.Single().Envelope);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class EphemeralKey : IKeyProvider
    {
        private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
    [StorageScope(VersionTag = "1.0.0")]
    public interface ISecret : IStoredContract<ISecret> { [EncryptedStorageField] string? Value { get; set; } }
    public sealed class Secret : ISecret
    {
        public string? Value { get; set; }
        public IObjectEnvelope<ISecret>? Envelope { get; private set; }
    }
    private sealed record SecretConfig(string DataRoot, ContractRepositoryType RepositoryType) : IDbContractConfigDto<ISecret>
    {
        public string EnvelopeDICacheKey => "secret";
        public string ContractName => "secret";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
        public StoragePolicy StoragePolicy => StoragePolicy.Default with
        { RepositoryNotifications = new(), WatchStorageChanges = false, DurabilityMode = StorageDurabilityMode.Durable };
    }
    private sealed class FailingSerializer : IRevisionObjectSerializer<IRecord>
    {
        private readonly BinaryObjectSerializer<IRecord> _inner = new();
        public long ReadRevision(ReadOnlyMemory<byte> payload) => _inner.ReadRevision(payload);
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => _inner.ReadRevisionAsync(source, cancellationToken);
        public byte[] Serialize(IObjectEnvelope<IRecord> envelope)
            => envelope.ObjectInstance!.Id == "fail" ? throw new IOException("Injected serialization failure") : _inner.Serialize(envelope);
        public IObjectEnvelope<IRecord> Deserialize(byte[] data) => _inner.Deserialize(data);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task NoBaselineRetentionStillDetectsChangesAndSubscriptionChurn(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { MaximumBaselineEntries = 0 });
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "churn", Value = 1 });
        using (var first = fixture.Notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true))
        {
            saved.Value = 2; saved = await fixture.Repository.SaveAsync(saved);
            await Idle(first); Assert.AreEqual(1L, first.DeliveredCount);
        }
        saved.Value = 3; saved = await fixture.Repository.SaveAsync(saved);
        var changes = new List<RepositoryChange<IRecord>>();
        using var second = fixture.Notifications.SubscribeChanged(change => { changes.Add(change); return ValueTask.CompletedTask; }, _ => true);
        saved.Value = 4; await fixture.Repository.SaveAsync(saved);
        await Idle(second); Assert.AreEqual(4, changes.Single().Entity.Value);
        CollectionAssert.AreEqual(new[] { "Value" }, changes.Single().ChangedProperties.ToArray());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task QueuedByteLimitFaultsWithoutFailingCommit(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { MaximumQueuedBytesPerSubscriber = 1 });
        using var subscription = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "byte-limit" });
        Assert.IsNotNull(saved.Envelope);
        Assert.AreEqual(RepositorySubscriptionEndReason.QueueOverflow, await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SnapshotLimitFaultsObservationWithoutReversingCommit(bool segment)
    {
        await using var fixture = new Fixture(segment, options: new() { MaximumSnapshotBytes = 1 });
        using var subscription = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "snapshot-limit" });
        Assert.IsNotNull(saved.Envelope);
        Assert.AreEqual(RepositorySubscriptionEndReason.ObservationFailed, await subscription.Completion);
        Assert.IsNotNull(await fixture.Repository.QueryFirstByStorageKeyAsync(saved.Envelope.EnvelopeCacheKey));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task CancellationBeforeCommitLeavesNoNotification(bool segment)
    {
        await using var fixture = new Fixture(segment);
        using var subscriber = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var candidate = new Record { Id = "cancel" };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Repository.SaveAsync(candidate, cancellation.Token).AsTask());
        Assert.IsNull(candidate.Envelope); Assert.AreEqual(0L, subscriber.DeliveredCount); Assert.IsFalse(subscriber.IsDisposed);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task PhysicalNotificationPayloadsRemainEnvelopes(bool segment)
    {
        await using var fixture = new Fixture(segment, physical: true);
        var physical = fixture.Services.GetRequiredKeyedService<IEnvelopeRepository<IRecord>>("notifications");
        var events = new List<(string Kind, IObjectEnvelope<IRecord> Envelope)>();
        using var add = physical.SubscribeToAdded(envelope => { events.Add(("added", envelope)); return ValueTask.CompletedTask; });
        using var store = physical.SubscribeToStored(envelope => { events.Add(("stored", envelope)); return ValueTask.CompletedTask; });
        using var delete = physical.SubscribeToDeleted(envelope => { events.Add(("deleted", envelope)); return ValueTask.CompletedTask; });
        using var logical = fixture.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "physical" });
        saved.Value = 1; saved = await fixture.Repository.SaveAsync(saved);
        await fixture.Repository.DeleteAsync(saved);
        await Idle(logical);
        CollectionAssert.AreEqual(new[] { "stored", "added", "stored", "deleted" }, events.Select(item => item.Kind).ToArray());
        Assert.AreEqual(StorageObjectState.Deleted, events.Last().Envelope.State);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReopeningDoesNotInventCreatedAndReadsPreviousCommittedValues(bool segment)
    {
        await using var fixture = new Fixture(segment);
        var saved = await fixture.Repository.SaveAsync(new Record { Id = "existing", Value = 4 });
        var key = saved.Envelope!.EnvelopeCacheKey;
        await fixture.Services.DisposeAsync();
        var policy = StoragePolicy.Default with { WatchStorageChanges = false, RepositoryNotifications = new(), HydrateOnInitialize = false };
        var config = new Config(fixture.Root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy);
        var services = new ServiceCollection();
        RegisterProvider(services, config, segment);
        await using var reopened = services.BuildServiceProvider();
        var repo = reopened.GetRequiredService<IRepository<IRecord>>();
        var notifications = reopened.GetRequiredService<IRepositoryNotifications<IRecord>>();
        using var created = notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        using var changed = notifications.SubscribeChanged(_ => ValueTask.CompletedTask, _ => true);
        await repo.InitializeAsync();
        var entity = await repo.QueryFirstByStorageKeyAsync(key); Assert.IsNotNull(entity);
        entity.Value = 5; await repo.SaveAsync(entity);
        await Idle(created, changed);
        Assert.AreEqual(0L, created.DeliveredCount); Assert.AreEqual(1L, changed.DeliveredCount);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SeparateDatabaseKeysDoNotShareSubscriptions(bool segment)
    {
        await using var first = new Fixture(segment);
        using var subscription = first.Notifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        var policy = StoragePolicy.Default with { WatchStorageChanges = false, RepositoryNotifications = new() };
        var services = new ServiceCollection();
        var config = new Config(Path.Combine(first.Root, "second"), segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy)
        { EnvelopeDICacheKey = "second" };
        RegisterProvider(services, config, segment);
        var thirdConfig = config with { EnvelopeDICacheKey = "third", DataRoot = Path.Combine(first.Root, "third") };
        RegisterProvider(services, thirdConfig, segment);
        await using var provider = services.BuildServiceProvider();
        var second = provider.GetRequiredKeyedService<IRepository<IRecord>>("second");
        var third = provider.GetRequiredKeyedService<IRepository<IRecord>>("third");
        var secondNotifications = provider.GetRequiredKeyedService<IRepositoryNotifications<IRecord>>("second");
        using var secondSub = secondNotifications.SubscribeCreated(_ => ValueTask.CompletedTask);
        await third.SaveAsync(new Record { Id = "third" });
        await second.SaveAsync(new Record { Id = "second" });
        await Idle(subscription, secondSub);
        Assert.AreEqual(0L, subscription.DeliveredCount); Assert.AreEqual(1L, secondSub.DeliveredCount);
        Assert.AreSame(secondNotifications, provider.GetRequiredService<IRepositoryNotifications<IRecord>>());
    }

    private static void RegisterProvider<T>(IServiceCollection services, IDbContractConfigDto<T> config, bool segment)
        where T : class, IStoredContract<T>
    {
        var handled = false;
        RegisterExternalProvider(services, config, segment, ref handled);
        if (handled) return;
        if (segment) services.AddSegmentCoreRepository(config); else services.AddFileRepository(config);
    }

    static partial void RegisterExternalProvider<T>(IServiceCollection services, IDbContractConfigDto<T> config,
        bool segment, ref bool handled) where T : class, IStoredContract<T>;

    public sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "InterfaceDB-NotificationTests", Guid.NewGuid().ToString("N"));
        public ServiceProvider Services { get; }
        public IRepository<IRecord> Repository { get; }
        public IRepositoryNotifications<IRecord> Notifications { get; }

        public Fixture(bool segment, StorageObjectCacheMode cache = StorageObjectCacheMode.StrongObject,
            bool physical = false, RepositoryNotificationOptions? options = null, bool enabled = true,
            bool durable = false, IObjectSerializer<IRecord>? serializer = null, int concurrency = 12,
            IWriteConflictStrategy<IRecord>? strategy = null, bool useSidecarWriteLocks = true, Idb.Diagnostics.IDatabaseTelemetry? telemetry = null)
        {
            var policy = StoragePolicy.Default with
            {
                Telemetry = telemetry, ObjectCacheMode = cache, RaiseNotifications = physical, WatchStorageChanges = false, MaxConcurrency = concurrency,
                HydrateOnInitialize = false, UseSingleWriteBuffer = true, UseSidecarWriteLocks = useSidecarWriteLocks,
                DurabilityMode = durable ? StorageDurabilityMode.Durable : StorageDurabilityMode.Performance,
                RepositoryNotifications = enabled ? options ?? new() : null
            };
            var config = new Config(Root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem, policy);
            var services = new ServiceCollection();
            if (serializer is not null) services.AddKeyedSingleton<IObjectSerializer<IRecord>>("notifications", serializer);
            if (strategy is not null) services.AddKeyedSingleton<IWriteConflictStrategy<IRecord>>("notifications", strategy);
            RegisterProvider(services, config, segment);
            Services = services.BuildServiceProvider();
            Repository = Services.GetRequiredService<IRepository<IRecord>>();
            Notifications = Services.GetRequiredService<IRepositoryNotifications<IRecord>>();
            Assert.AreSame<object>(Repository, Notifications);
            Assert.AreSame(Notifications, Services.GetRequiredKeyedService<IRepositoryNotifications<IRecord>>("notifications"));
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    public sealed record Config(string DataRoot, ContractRepositoryType RepositoryType, StoragePolicy StoragePolicy) : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey { get; init; } = "notifications";
        public string ContractName => "notification-record";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? ActiveVersion => "1.0.0";
    }

    public interface IBaseRecord { int Value { get; set; } }
    [StorageScope(VersionTag = "1.0.0")]
    [SearchIndex]
    public interface IRecord : IStoredContract<IRecord>, IBaseRecord
    {
        [SearchIndex(SearchIndexMode.Exact)] string Id { get; set; }
        string Language { get; set; }
        byte[] Bytes { get; set; }
        List<string> Names { get; set; }
    }
    public sealed class Record : IRecord
    {
        public string Id { get; set; } = "";
        public string Language { get; set; } = "en";
        public int Value { get; set; }
        public byte[] Bytes { get; set; } = [];
        public List<string> Names { get; set; } = [];
        public IObjectEnvelope<IRecord>? Envelope { get; private set; }
    }
}
