using System.Net;
using Idb.Authentication;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    public async Task Push_SubscriptionBudgetReservesCapacityForWriters()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { MaximumConnections = 3, MaximumSubscriptions = 1 });
        await using var client = Client(host, "parent-a");
        await using var first = client.SubscribeChanges(() => ValueTask.CompletedTask);
        await first.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = client.SubscribeChanges(() => ValueTask.CompletedTask);
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => rejected.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(RemoteErrorCode.Busy, error.Code);
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => rejected.Ready);
        await Seed(client, "still-writable", 1);
        await first.DisposeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        await using var replacement = client.SubscribeChanges(() => ValueTask.CompletedTask);
        await replacement.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, host.ActiveSubscriptions);
    }

    [TestMethod]
    public async Task Push_SubscriptionConnectionCannotExecuteStorageCommands()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var store = new CountingPushStore(inner);
        await using var host = await Host(store);
        var options = Options(host, Token("parent-a")).Connection with
        {
            RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate
                | EncodedStoreCapabilities.DurableCommits | EncodedStoreCapabilities.Write
        };
        await using var connection = await TcpEncodedEnvelopeStore.ConnectCoreAsync(options, true, default);
        await Assert.ThrowsAsync<IOException>(async () => await connection.PutAsync("forbidden", [1]));
        Assert.AreEqual(0, store.Operations, "Subscription sessions must never dispatch storage requests.");
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
    }

    [TestMethod]
    public async Task Push_IsAccountScopedAndDisposableWithoutHoldingWriteLease()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var a = Client(host, "parent-a");
        await using var b = Client(host, "parent-b");
        int aCount = 0, bCount = 0;
        await using var aSub = a.SubscribeChanges(() => { Interlocked.Increment(ref aCount); return ValueTask.CompletedTask; });
        await using var bSub = b.SubscribeChanges(() => { Interlocked.Increment(ref bCount); return ValueTask.CompletedTask; });
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 2 && aCount == 1 && bCount == 1));
        await Seed(a, "person", 1);
        await Until(() => Task.FromResult(aCount >= 2));
        await Task.Delay(150);
        Assert.AreEqual(1, bCount, "Another account must not receive even a timing hint.");
        await aSub.DisposeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 1));
        var disposedCount = aCount;
        await Seed(a, "second", 2);
        await Task.Delay(150);
        Assert.AreEqual(disposedCount, aCount);
        await b.DisposeAsync();
        await bSub.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
    }



    [TestMethod]
    public async Task Push_SlowCallbackCoalescesWithoutBlockingWriterOrOtherSubscribers()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var writer = Client(host, "parent-a");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowCount = 0, fastCount = 0;
        await using var slow = writer.SubscribeChanges(async () =>
        {
            Interlocked.Increment(ref slowCount);
            entered.TrySetResult();
            await release.Task;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var fast = writer.SubscribeChanges(() => { Interlocked.Increment(ref fastCount); return ValueTask.CompletedTask; });
            await Until(() => Task.FromResult(fastCount >= 1));
            for (int i = 0; i < 8; i++) await Seed(writer, "person-" + i, i);
            await Until(() => Task.FromResult(fastCount >= 2));
            Assert.AreEqual(1, slowCount);
            await Task.Delay(150);
            release.TrySetResult();
            await Until(() => Task.FromResult(slowCount == 2));
            await Task.Delay(150);
            Assert.AreEqual(2, slowCount, "Slow consumers retain one coalesced hint, not an unbounded event queue.");
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task Push_CancellationAndCallbackFailureDeregisterOnServer()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var client = Client(host, "parent-a");
        using var lifetime = new CancellationTokenSource();
        var first = client.SubscribeChanges(() => ValueTask.CompletedTask, lifetime.Token);
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 1));
        lifetime.Cancel();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        var failed = client.SubscribeChanges(() => ValueTask.FromException(new ApplicationException("consumer failure")));
        await Assert.ThrowsExactlyAsync<ApplicationException>(() => failed.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(failed.IsDisposed);
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        failed.Dispose();
    }

    [TestMethod]
    public async Task Push_RejectsUnauthorizedSubscriptionsWithoutPublishingHints()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var client = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a", "scope")));
        int hints = 0;
        var subscription = client.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
        Assert.AreEqual(0, hints);
        Assert.AreEqual(0, host.ActiveSubscriptions);
        subscription.Dispose();
    }

    [TestMethod]
    public async Task Push_TokenExpiryTerminatesIdleSubscription()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { TokenValidator = new ShortLivedValidator(_validator) });
        await using var client = Client(host, "parent-a");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = client.SubscribeChanges(() => { ready.TrySetResult(); return ValueTask.CompletedTask; });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        subscription.Dispose();
    }

    [TestMethod]
    public async Task Push_RechecksAuthorizationBeforeDelivery()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        int allowed = 1;
        var options = HostOptions(store);
        await using var host = await TcpStoreHost.StartAsync(options with
        {
            AuthorizeIdentity = (identity, alias, capabilities) => options.AuthorizeIdentity!(identity, alias, capabilities)
                && (capabilities != (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate
                    | EncodedStoreCapabilities.DurableCommits) || Volatile.Read(ref allowed) == 1)
        });
        await using var client = Client(host, "parent-a");
        int hints = 0;
        var subscription = client.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        await Until(() => Task.FromResult(hints == 1));
        Interlocked.Exchange(ref allowed, 0);
        await Seed(client, "person", 1);
        await Assert.ThrowsAsync<IOException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, hints);
        Assert.AreEqual(0, host.ActiveSubscriptions);
        subscription.Dispose();
    }

    private sealed class ShortLivedValidator(IAccessTokenValidator inner) : IAccessTokenValidator
    {
        public async ValueTask<AuthenticatedIdentity> ValidateAsync(string token, CancellationToken cancellationToken = default)
            => (await inner.ValidateAsync(token, cancellationToken)) with { ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(500) };
    }
    private sealed class CountingPushStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        private int _operations, _mutations;
        internal int Operations => Volatile.Read(ref _operations);
        internal int Mutations => Volatile.Read(ref _mutations);
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _operations);
            if (request.IsMutation) Interlocked.Increment(ref _mutations);
            return await inner.ExecuteAsync(request, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static async Task Settle(CountingPushStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var count = store.Operations;
            await Task.Delay(200, timeout.Token);
            if (count == store.Operations) return;
        }
    }
}
