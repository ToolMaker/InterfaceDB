using System.Net;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Composition_ReadsRemoteOnMissAndPersistsIndependentCache(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("cache");
        await using var repository = Compose(cache, remote, mode);
        var read = await repository.QuerySingleAsync(_ => true);
        Assert.AreEqual(1, read.Score);
        Assert.AreEqual((await remote.QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds,
            read.Envelope!.RevisionTimestampUtcMicroseconds);
        read.Score = 99;
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score, "Application values must be detached.");
        await host.DisposeAsync();
        Assert.AreEqual(1, (await repository.QuerySingleAsync(_ => true)).Score);
        var saved = await repository.SaveAsync(new RemotePerson { Name = "offline", Score = 2 });
        Assert.IsTrue(saved.Envelope!.RevisionTimestampUtcMicroseconds > 0);
        Assert.AreEqual(2, (await Local(cache).QuerySingleAsync(person => person.Name == "offline")).Score);
    }

    [TestMethod]
    public async Task Composition_LocalFirstQueueSurvivesRestartAndSynchronizesUpdateAndDelete()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using (var cache = Cache("queue"))
        await using (var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst))
        {
            await repository.SaveAsync(new RemotePerson { Name = "offline", Score = 1 });
            Assert.IsNull(await remote.QueryFirstAsync(_ => true), "Local writes return before remote delivery.");
        }
        await using (var cache = Cache("queue"))
        await using (var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst))
        {
            var result = await repository.SynchronizeAsync();
            Assert.IsTrue(result.IsSuccessful, FailureText(result));
            Assert.AreEqual(1, result.PeerCommits);
            var saved = await repository.QuerySingleAsync(_ => true);
            saved.Score = 2;
            await repository.SaveAsync(saved);
            Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
            Assert.AreEqual(2, (await remote.QuerySingleAsync(_ => true)).Score);
            await repository.DeleteAsync(await repository.QuerySingleAsync(_ => true));
        }
        await using var reopened = Cache("queue");
        await using var final = Compose(reopened, remote, RepositoryReadPreference.LocalFirst);
        Assert.IsTrue((await final.SynchronizeAsync()).IsSuccessful);
        await using var subscriberCache = Cache("subscriber");
        await using var subscriber = Compose(subscriberCache, remote, RepositoryReadPreference.RemoteFirst);
        Assert.IsNull(await subscriber.QueryFirstAsync(_ => true), "A remote tombstone must not become a live entity.");
    }

    [TestMethod]
    public async Task Composition_OfflineMutationConflictsWithConcurrentRemoteEditWithoutOverwritingEither()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("conflict");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        var local = await repository.QuerySingleAsync(_ => true);
        local.Score = 2;
        await repository.SaveAsync(local);
        var other = await remote.QuerySingleAsync(_ => true);
        other.Score = 3;
        await remote.SaveAsync(other);
        var result = await repository.SynchronizeAsync();
        Assert.AreEqual(1, result.Conflicts);
        Assert.AreEqual(0, result.PeerCommits);
        Assert.AreEqual(3, (await remote.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(2, (await repository.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(1, (await repository.SynchronizeAsync()).Conflicts, "A conflict must retain the queued edit.");
    }

    [TestMethod]
    public async Task Composition_LostAcknowledgementReconcilesWithoutRepeatingRemoteMutation()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var dropped = new DropAcknowledgement(store);
        await using var host = await Host(dropped);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("ack");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 8 });
        Assert.AreEqual(1, (await repository.SynchronizeAsync()).Failures.Count);
        var reconciled = await repository.SynchronizeAsync();
        Assert.IsTrue(reconciled.IsSuccessful, FailureText(reconciled));
        Assert.AreEqual(0, reconciled.PeerCommits);
        Assert.AreEqual(1, reconciled.Acknowledgements);
        Assert.AreEqual(1, dropped.Mutations);
        Assert.AreEqual(8, (await repository.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Composition_RemoteFirstRejectsStaleWritesAndLocalFirstReceivesRemoteChanges()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var firstRemote = Client(host, "parent-a");
        await using var secondRemote = Client(host, "parent-a");
        await using var cacheA = Cache("a");
        await using var cacheB = Cache("b");
        await using var first = Compose(cacheA, firstRemote, RepositoryReadPreference.RemoteFirst);
        await using var second = Compose(cacheB, secondRemote, RepositoryReadPreference.LocalFirst);
        var initial = await first.SaveAsync(new RemotePerson { Name = "person", Score = 1 });
        var stale = await first.QuerySingleAsync(_ => true);
        Assert.AreEqual(1, (await second.QuerySingleAsync(_ => true)).Score);
        initial.Score = 2;
        await first.SaveAsync(initial);
        stale.Score = 3;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await first.SaveAsync(stale));
        Assert.AreEqual(1, (await second.QuerySingleAsync(_ => true)).Score, "LocalFirst uses its cache until reconciliation.");
        Assert.IsTrue((await second.SynchronizeAsync()).IsSuccessful);
        Assert.IsNull(await second.SearchFirstAsync(person => person.Score == 1));
        Assert.AreEqual(2, (await second.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Composition_AutomaticallyPushesLocalChangesAndRefreshesEverySubscriberIncludingDeletes()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remoteA = Client(host, "parent-a");
        await using var remoteB = Client(host, "parent-a");
        await using var cacheA = Cache("auto-a");
        await using var cacheB = Cache("auto-b");
        await using var first = Compose(cacheA, remoteA, RepositoryReadPreference.LocalFirst, background: true);
        await using var second = Compose(cacheB, remoteB, RepositoryReadPreference.RemoteFirst, background: true);
        var notifications = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = second.SubscribeChanges(() =>
        { Interlocked.Increment(ref notifications); notified.TrySetResult(); return ValueTask.CompletedTask; });
        await first.InitializeAsync();
        await second.InitializeAsync();
        await first.SaveAsync(new RemotePerson { Name = "person", Score = 1 });
        await Until(async () => (await Local(cacheB).QueryFirstAsync(_ => true))?.Score == 1);
        // The cache commit can be visible before the asynchronous notification worker runs.
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(Volatile.Read(ref notifications) > 0);
        var value = await second.QuerySingleAsync(_ => true);
        value.Score = 2;
        await second.SaveAsync(value);
        await Until(async () => (await Local(cacheA).QueryFirstAsync(_ => true))?.Score == 2);
        await second.DeleteAsync(await second.QuerySingleAsync(_ => true));
        await Until(async () => await first.QueryFirstAsync(_ => true) is null);
    }

    [TestMethod]
    public async Task Composition_RemotePhysicalDeletionDoesNotLeaveStaleCachedRecord()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("deletion");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.RemoteFirst);
        await repository.QuerySingleAsync(_ => true);
        await remote.DeleteAsync(await remote.QuerySingleAsync(_ => true));
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
        await host.DisposeAsync();
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task Composition_AuthenticationFailureCannotFallBackToCacheOrMixAccountScopes()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var tokens = new SwitchingTokenProvider(Token("parent-a"));
        await using var remote = new TcpRepository<IRemotePerson>(Options(host, tokens.Token) with
        { Connection = Options(host, tokens.Token).Connection with { TokenProvider = tokens } });
        await Seed(remote, "person", 1);
        await using var cache = Cache("auth");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.RemoteFirst);
        await repository.QuerySingleAsync(_ => true);
        tokens.Token = Token("parent-a", "expired");
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await repository.QueryFirstAsync(_ => true));
        await using var wrongScope = Compose(cache, remote, RepositoryReadPreference.LocalFirst, scope: "parent-b");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await wrongScope.InitializeAsync());
        tokens.Token = Token("parent-b");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await repository.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task Composition_AcknowledgementDoesNotInvalidateCurrentLocalEditAndCoalescesQueuedChanges()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("revision");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        var current = await repository.SaveAsync(new RemotePerson { Name = "person", Score = 1 });
        current.Score = 2;
        current = await repository.SaveAsync(current);
        var revision = current.Envelope!.RevisionTimestampUtcMicroseconds;
        var result = await repository.SynchronizeAsync();
        Assert.IsTrue(result.IsSuccessful, FailureText(result));
        Assert.AreEqual(1, result.PeerCommits);
        Assert.AreEqual(revision, (await repository.QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds);
        current.Score = 3;
        await repository.SaveAsync(current);
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual(3, (await remote.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    [DataRow(RepositoryConflictResolution.AcceptRemote)]
    [DataRow(RepositoryConflictResolution.KeepLocal)]
    public async Task Composition_ConflictResolutionIsExplicitAndRevisionGuarded(RepositoryConflictResolution resolution)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("resolution");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        var local = await repository.QuerySingleAsync(_ => true);
        local.Score = 2;
        var pending = await repository.SaveAsync(local);
        var other = await remote.QuerySingleAsync(_ => true);
        other.Score = 3;
        await remote.SaveAsync(other);
        Assert.AreEqual(1, (await repository.SynchronizeAsync()).Conflicts);
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () =>
            await repository.ResolveConflictAsync("person", local.Envelope!.RevisionTimestampUtcMicroseconds, resolution));
        await repository.ResolveConflictAsync("person", pending.Envelope!.RevisionTimestampUtcMicroseconds, resolution);
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        var expected = resolution == RepositoryConflictResolution.KeepLocal ? 2 : 3;
        Assert.AreEqual(expected, (await repository.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(expected, (await remote.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Composition_ReconnectAutomaticallyDeliversOfflineQueueAndRefreshesSubscribers()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var port = host.LocalEndPoint.Port;
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("reconnect");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst, background: true);
        await repository.InitializeAsync();
        await host.DisposeAsync();
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 4 });
        Assert.AreEqual(4, (await repository.QuerySingleAsync(_ => true)).Score);
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with { ListenEndPoint = new(IPAddress.Loopback, port) });
        await using var reader = Client(restarted, "parent-a");
        await Until(async () => (await reader.QueryFirstAsync(_ => true))?.Score == 4);
        await using var secondCache = Cache("reconnect-reader");
        await using var second = Compose(secondCache, reader, RepositoryReadPreference.LocalFirst, background: true);
        await second.InitializeAsync();
        await Until(async () => (await Local(secondCache).QueryFirstAsync(_ => true))?.Score == 4);
    }

    [TestMethod]
    public async Task Composition_KeyedRegistrationsKeepIndependentPolicySnapshots()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remoteA = Client(host, "parent-a");
        await using var remoteB = Client(host, "parent-a");
        await Seed(remoteA, "person", 1);
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IRemotePerson>(new CacheConfig(Path.Combine(_root, "di-a"), "local-a"),
            runtimeTypeAllowlist: new(typeof(RemotePerson)));
        services.AddSegmentCoreRepository<IRemotePerson>(new CacheConfig(Path.Combine(_root, "di-b"), "local-b"),
            runtimeTypeAllowlist: new(typeof(RemotePerson)));
        services.AddKeyedSingleton<IRepository<IRemotePerson>>("remote-a", remoteA);
        services.AddKeyedSingleton<IRepository<IRemotePerson>>("remote-b", remoteB);
        var options = new ResilientRepositoryOptions<IRemotePerson>
        {
            CacheScope = "parent-a", ReadPreference = RepositoryReadPreference.LocalFirst,
            EnableBackgroundSynchronization = false, IsUnavailableException = TcpRepositoryAvailability.IsUnavailable
        };
        services.AddLocalRemoteRepository("composed-a", "local-a", "remote-a", new PersonMapper(), options);
        options.ReadPreference = RepositoryReadPreference.RemoteFirst;
        services.AddLocalRemoteRepository("composed-b", "local-b", "remote-b", new PersonMapper(), options, makeDefault: false);
        options.ReadPreference = RepositoryReadPreference.LocalFirst;
        await using var provider = services.BuildServiceProvider();
        var a = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("composed-a");
        var b = provider.GetRequiredKeyedService<IRepository<IRemotePerson>>("composed-b");
        Assert.AreSame(a, provider.GetRequiredService<IRepository<IRemotePerson>>());
        await a.QuerySingleAsync(_ => true);
        await b.QuerySingleAsync(_ => true);
        var direct = await remoteA.QuerySingleAsync(_ => true);
        direct.Score = 2;
        await remoteA.SaveAsync(direct);
        Assert.AreEqual(1, (await a.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(2, (await b.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Composition_LocalCacheCannotBeReadAfterAccountSwitchEvenIfScopeStringIsReused()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var tokens = new SwitchingTokenProvider(Token("parent-a"));
        await using var remote = new TcpRepository<IRemotePerson>(Options(host, tokens.Token) with
        { Connection = Options(host, tokens.Token).Connection with { TokenProvider = tokens } });
        await Seed(remote, "person", 1);
        await using var cache = Cache("identity");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        await repository.QuerySingleAsync(_ => true);
        tokens.Token = Token("parent-b");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await repository.QueryFirstAsync(_ => true));
        await using var otherRemote = Client(host, "parent-b");
        await using var wrongAccount = Compose(cache, otherRemote, RepositoryReadPreference.LocalFirst);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await wrongAccount.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task Composition_BoundedFailedSnapshotAndCancellationCannotEraseOrServePartialCache()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "one", 1);
        await using var cache = Cache("bounded");
        await using (var populate = Compose(cache, remote, RepositoryReadPreference.RemoteFirst))
            await populate.QuerySingleAsync(_ => true);
        await Seed(remote, "two", 2);
        await using var limited = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a")) with { MaximumSnapshotRecords = 1 });
        await using var repository = Compose(cache, limited, RepositoryReadPreference.RemoteFirst);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await repository.QueryFirstAsync(_ => true));
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await repository.QueryFirstAsync(_ => true, cancelled.Token));
    }

    [TestMethod]
    public async Task Composition_ExplicitChangeHintRefreshesLocalFirstAndClearsOnlyAppliedInvalidation()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("hint");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        await repository.QuerySingleAsync(_ => true);
        var update = await remote.QuerySingleAsync(_ => true);
        update.Score = 2;
        await remote.SaveAsync(update);
        await repository.NotifyChangedAsync(new("people", "person", 0, 42));
        Assert.AreEqual(2, (await repository.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(0, (await repository.GetSynchronizationStateAsync()).DirtyStorageKeys.Count);
    }

    [TestMethod]
    public async Task Composition_RemoteCommitStillSucceedsWhenCacheUpdateFailsAndUnsafeFallbackIsBlocked()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("failed-cache");
        var proxy = System.Reflection.DispatchProxy.Create<IEnvelopeMetadataRepository<IRemotePerson>, FailingCache>();
        var control = (FailingCache)(object)proxy;
        control.Inner = (IEnvelopeMetadataRepository<IRemotePerson>)Local(cache);
        await using var repository = new ResilientRepository<IRemotePerson>(proxy, remote, new PersonMapper(),
            new ResilientRepositoryOptions<IRemotePerson>
            {
                ReadPreference = RepositoryReadPreference.RemoteFirst, CacheScope = "parent-a",
                EnableBackgroundSynchronization = false, IsUnavailableException = TcpRepositoryAvailability.IsUnavailable
            });
        var entity = await repository.QuerySingleAsync(_ => true);
        control.FailWrites = true;
        entity.Score = 5;
        var saved = await repository.SaveAsync(entity);
        Assert.AreEqual(5, saved.Score);
        Assert.AreEqual(5, (await remote.QuerySingleAsync(_ => true)).Score);
        var refresh = await repository.SynchronizeAsync();
        Assert.IsFalse(refresh.IsSuccessful);
        Assert.AreEqual(ResilientEndpoint.Preferred, refresh.Failures.Single().Endpoint,
            "The acknowledged remote command survives a failed local import.");
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score);
        await host.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(async () => await repository.QueryFirstAsync(_ => true));
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score);
    }

    public class FailingCache : System.Reflection.DispatchProxy
    {
        public IEnvelopeMetadataRepository<IRemotePerson> Inner { get; set; } = null!;
        public bool FailWrites { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (FailWrites && targetMethod!.Name == nameof(IEnvelopeMetadataRepository<IRemotePerson>.SaveWithExtensionMetadataAsync))
                throw new IOException("Injected local cache write failure.");
            try { return targetMethod!.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException exception)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
                throw;
            }
        }
    }
    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst)]
    [DataRow(RepositoryReadPreference.RemoteFirst)]
    public async Task Composition_DeletedLogicalKeyCanBeRecreatedWithoutResurrectingOldPayload(RepositoryReadPreference mode)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("recreate");
        await using var repository = Compose(cache, remote, mode);
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 1 });
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        await repository.DeleteAsync(await repository.QuerySingleAsync(_ => true));
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 2 });
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual(2, (await repository.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Composition_RejectsCacheOverflowBeforePersistingAnUnreadableQueue()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("queue-limit");
        await using var repository = new ResilientRepository<IRemotePerson>(Local(cache), remote, new PersonMapper(),
            new ResilientRepositoryOptions<IRemotePerson>
            {
                ReadPreference = RepositoryReadPreference.LocalFirst, CacheScope = "parent-a",
                EnableBackgroundSynchronization = false, MaximumSynchronizationScanItems = 1
            });
        await repository.SaveAsync(new RemotePerson { Name = "one", Score = 1 });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await repository.SaveAsync(new RemotePerson { Name = "two" }));
        Assert.AreEqual(1, (await repository.QuerySingleAsync(_ => true)).Score);
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual("one", (await remote.QuerySingleAsync(_ => true)).Name);
    }
    [TestMethod]
    public async Task Composition_LostAcknowledgementFollowedByOrdinaryRemoteEditImportsLatestPayload()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var dropped = new DropAcknowledgement(store);
        await using var host = await Host(dropped);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("ack-then-edit");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 1 });
        Assert.AreEqual(1, (await repository.SynchronizeAsync()).Failures.Count);
        var next = await remote.QuerySingleAsync(_ => true);
        next.Score = 2;
        await remote.SaveAsync(next); // An ordinary endpoint save preserves opaque extension metadata.
        var reconciled = await repository.SynchronizeAsync();
        Assert.IsTrue(reconciled.IsSuccessful, FailureText(reconciled));
        Assert.AreEqual(0, reconciled.PeerCommits);
        Assert.AreEqual(1, reconciled.Acknowledgements);
        Assert.AreEqual(2, (await repository.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(2, dropped.Mutations, "The old pending mutation must not be replayed.");
    }
    [TestMethod]
    public async Task Composition_ApplicationPredicateFailureDoesNotDisableSubsequentLocalWrites()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("predicate");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.LocalFirst);
        await Assert.ThrowsExactlyAsync<ApplicationException>(async () =>
            await repository.QueryFirstAsync(_ => throw new ApplicationException("Application predicate failed.")));
        var saved = await repository.SaveAsync(new RemotePerson { Name = "other", Score = 2 });
        Assert.AreEqual(2, saved.Score);
        Assert.IsNull(await remote.QueryFirstByStorageKeyAsync("other"));
    }
    private static string FailureText(ResilientSynchronizationResult result)
        => string.Join(Environment.NewLine, result.Failures.Select(failure => failure.Exception.ToString()));



    private ServiceProvider Cache(string name)
    {
        var services = new ServiceCollection();
        services.AddSegmentCoreRepository<IRemotePerson>(new CacheConfig(Path.Combine(_root, name)),
            runtimeTypeAllowlist: new(typeof(RemotePerson)));
        return services.BuildServiceProvider();
    }

    private static IRepository<IRemotePerson> Local(ServiceProvider cache)
        => cache.GetRequiredKeyedService<IRepository<IRemotePerson>>("cache");

    private static ResilientRepository<IRemotePerson> Compose(ServiceProvider cache, IRepository<IRemotePerson> remote,
        RepositoryReadPreference mode, bool background = false, string scope = "parent-a")
        => new(Local(cache), remote, new PersonMapper(), new ResilientRepositoryOptions<IRemotePerson>
        {
            DatasetId = "people", ReadPreference = mode, CacheScope = scope,
            EnableBackgroundSynchronization = background, BackgroundRetryDelay = TimeSpan.FromMilliseconds(50),
            IsUnavailableException = TcpRepositoryAvailability.IsUnavailable,
            IsEndpointWideFailureException = TcpRepositoryAvailability.IsUnavailable
        });

    private sealed class PersonMapper : IResilientEntityMapper<IRemotePerson>
    {
        public string GetStorageKey(IRemotePerson entity) => entity.Name;
        public IRemotePerson CreateDetachedCopy(IRemotePerson source) => new RemotePerson { Name = source.Name, Score = source.Score };
        public void CopyValues(IRemotePerson source, IRemotePerson target) { target.Name = source.Name; target.Score = source.Score; }
    }

}
