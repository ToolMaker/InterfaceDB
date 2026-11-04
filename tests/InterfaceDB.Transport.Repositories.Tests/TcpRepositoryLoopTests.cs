using Idb.Libraries.Repositories;
using System.Reflection;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Repository;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task Loop_RemoteFirstCommandCommitsOnlyRemoteAndNotificationImportsWithoutEcho()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        using var notifications = new RepositoryChangePublisher();
        var endpoint = ControlledEndpoint(remote, notifications);
        await using var cache = Cache("direction");
        await using var repository = Compose(cache, endpoint, RepositoryReadPreference.RemoteFirst, background: true);
        await repository.InitializeAsync();
        await Until(async () => (await Local(cache).QueryFirstAsync(_ => true))?.Score == 1);
        await Settle(store);
        var mutations = store.Mutations;
        var notificationsDelivered = 0;
        await using var changes = repository.SubscribeChanges(() =>
        { Interlocked.Increment(ref notificationsDelivered); return ValueTask.CompletedTask; });

        var command = await repository.QuerySingleAsync(_ => true);
        command.Score = 2;
        var saved = await repository.SaveAsync(command);
        Assert.AreEqual(2, saved.Score);
        Assert.AreEqual(mutations + 1, store.Mutations);
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score,
            "A remote-first command must not update the physical cache directly.");
        Assert.AreEqual(0, notificationsDelivered, "Publish only after cache refresh.");
        notifications.PublishChanged();
        await Until(async () => (await Local(cache).QuerySingleAsync(_ => true)).Score == 2);
        await Until(() => Task.FromResult(notificationsDelivered == 1));
        var revision = (await Local(cache).QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds;
        for (int index = 0; index < 12; index++) notifications.PublishChanged();
        await Settle(store);
        Assert.AreEqual(mutations + 1, store.Mutations, "Duplicate hints cannot originate remote writes.");
        Assert.AreEqual(revision, (await Local(cache).QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual(1, notificationsDelivered, "Unchanged remote revisions cannot republish local changes.");

        await repository.DeleteAsync(saved);
        Assert.AreEqual(revision, (await Local(cache).QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds,
            "A remote-first delete also waits for a refresh hint.");
        notifications.PublishChanged();
        await Until(async () => (await Local(cache).QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds > revision);
        await Until(() => Task.FromResult(notificationsDelivered == 2));
        await Settle(store);
        Assert.AreEqual(mutations + 2, store.Mutations, "Delete propagation cannot echo a deletion back.");
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task Loop_LocalFirstAcknowledgementDoesNotRepublishAnApplicationChange()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        using var notifications = new RepositoryChangePublisher();
        var endpoint = ControlledEndpoint(remote, notifications);
        await using var cache = Cache("ack-events");
        await using var repository = Compose(cache, endpoint, RepositoryReadPreference.LocalFirst, background: true);
        int changes = 0;
        await using var subscription = repository.SubscribeChanges(() => { Interlocked.Increment(ref changes); return ValueTask.CompletedTask; });
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 3 });
        await Until(async () => (await remote.QueryFirstAsync(_ => true))?.Score == 3);
        await Settle(store);
        Assert.AreEqual(0, (await repository.SynchronizeAsync()).PendingMutationsExamined,
            "Background delivery should already have cleared the durable pending marker.");
        await Until(() => Task.FromResult(changes == 1));
        for (int index = 0; index < 12; index++) notifications.PublishChanged();
        await Settle(store);
        Assert.AreEqual(1, store.Mutations, "The local command should produce exactly one remote commit.");
        Assert.AreEqual(1, changes, "Acknowledgement metadata must not look like a second application edit.");
    }

    [TestMethod]
    [DataRow(RepositoryReadPreference.LocalFirst, "save")]
    [DataRow(RepositoryReadPreference.RemoteFirst, "save")]
    [DataRow(RepositoryReadPreference.LocalFirst, "delete")]
    [DataRow(RepositoryReadPreference.RemoteFirst, "delete")]
    [DataRow(RepositoryReadPreference.LocalFirst, "resolve")]
    [DataRow(RepositoryReadPreference.RemoteFirst, "resolve")]
    public async Task Loop_CallbackCannotWriteBackToItsSource(RepositoryReadPreference mode, string operation)
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("callback");
        await using var repository = Compose(cache, remote, mode, background: true);
        await repository.InitializeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 1));
        await Settle(store);
        int calls = 0;
        await using var bad = repository.SubscribeChanges(async () =>
        {
            Interlocked.Increment(ref calls);
            var observed = await repository.QuerySingleAsync(_ => true);
            if (operation == "save") await repository.SaveAsync(observed);
            else if (operation == "delete") await repository.DeleteAsync(observed);
            else await repository.ResolveConflictAsync("person", observed.Envelope!.RevisionTimestampUtcMicroseconds,
                RepositoryConflictResolution.KeepLocal);
        });
        await repository.SaveAsync(new RemotePerson { Name = "person", Score = 4 });
        await Assert.ThrowsExactlyAsync<RepositoryFeedbackLoopException>(() => bad.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Until(async () => (await remote.QueryFirstAsync(_ => true))?.Score == 4);
        await Settle(store);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, store.Mutations);
        Assert.IsTrue(bad.IsDisposed);
        // A later explicit application command remains valid after detaching the faulty consumer.
        var next = await repository.QuerySingleAsync(_ => true);
        next.Score = 5;
        await repository.SaveAsync(next);
        await Until(async () => (await remote.QuerySingleAsync(_ => true)).Score == 5);
        await Settle(store);
        Assert.AreEqual(2, store.Mutations);
    }

    [TestMethod]
    public async Task Loop_RemoteFirstWithoutDeliveredHintBlocksStaleOfflineFallbackAndCanRecover()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var remote = Client(host, "parent-a");
        await Seed(remote, "person", 1);
        await using var cache = Cache("pending-refresh");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.RemoteFirst);
        var value = await repository.QuerySingleAsync(_ => true);
        value.Score = 2;
        await repository.SaveAsync(value);
        Assert.AreEqual(1, (await Local(cache).QuerySingleAsync(_ => true)).Score);
        var port = host.LocalEndPoint.Port;
        await host.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(async () => await repository.QueryFirstAsync(_ => true));
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(store) with
        { ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual(2, (await Local(cache).QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task Loop_RemoteFirstLostAcknowledgementCannotReplayOrServeStaleOfflineCache()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var dropped = new DropAcknowledgement(store);
        await using var host = await Host(dropped);
        await using var remote = Client(host, "parent-a");
        await using var cache = Cache("uncertain-refresh");
        await using var repository = Compose(cache, remote, RepositoryReadPreference.RemoteFirst);
        Assert.IsNull(await repository.QueryFirstAsync(_ => true));
        await Assert.ThrowsAsync<Exception>(async () =>
            await repository.SaveAsync(new RemotePerson { Name = "person", Score = 6 }));
        Assert.AreEqual(1, dropped.Mutations);
        Assert.IsNull(await Local(cache).QueryFirstAsync(_ => true));
        var port = host.LocalEndPoint.Port;
        await host.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(async () => await repository.QueryFirstAsync(_ => true));
        await using var restarted = await TcpStoreHost.StartAsync(HostOptions(dropped) with
        { ListenEndPoint = new(System.Net.IPAddress.Loopback, port) });
        Assert.IsTrue((await repository.SynchronizeAsync()).IsSuccessful);
        Assert.AreEqual(6, (await Local(cache).QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(1, dropped.Mutations, "Reconciliation must observe the uncertain commit, never replay it.");
    }

    public interface IControlledPushEndpoint : IEnvelopeMetadataRepository<IRemotePerson>, IRepositoryChangeNotifications, IRepositoryCacheIdentity { }

    private static IControlledPushEndpoint ControlledEndpoint(TcpRepository<IRemotePerson> inner, RepositoryChangePublisher notifications)
    {
        var endpoint = DispatchProxy.Create<IControlledPushEndpoint, ControlledPushProxy>();
        var control = (ControlledPushProxy)(object)endpoint;
        control.Inner = inner; control.Notifications = notifications;
        return endpoint;
    }

    private sealed class DelayedReadySubscription(IRepositoryChangeSubscription inner, Task ready) : IRepositoryChangeSubscription
    {
        public bool IsDisposed => inner.IsDisposed;
        public Task Ready => Task.WhenAll(inner.Ready, ready);
        public Task Completion => inner.Completion;
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    public class ControlledPushProxy : DispatchProxy
    {
        public TcpRepository<IRemotePerson> Inner { get; set; } = null!;
        public RepositoryChangePublisher Notifications { get; set; } = null!;
        public Task? ReadyGate { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRepositoryChangeNotifications.SubscribeChanges))
                {
                var subscription = Notifications.SubscribeChanges((Func<ValueTask>)args![0]!, (CancellationToken)args[1]!);
                return ReadyGate is null ? subscription : new DelayedReadySubscription(subscription, ReadyGate);
            }
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
                throw;
            }
        }
    }
}
