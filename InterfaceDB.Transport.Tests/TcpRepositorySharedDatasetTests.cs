using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private TcpRepositoryOptions<IRemotePerson> SharedOptions(TcpStoreHost host, string subject, string alias = "people")
    {
        var options = Options(host, Token(subject));
        return options with { Connection = options.Connection with
        { StoreAlias = alias, RequiredCapabilities = EncodedStoreCapabilities.All } };
    }

    private TcpStoreHostOptions SharedHostOptions(IEncodedEnvelopeStore store)
    {
        var options = HostOptions(store);
        return options with
        {
            Stores = [new("people", Manifest(), store) { AccessPolicy = UnrestrictedDataAccessPolicy.Instance }],
            AuthorizeIdentity = (identity, alias, capabilities) => options.AuthorizeIdentity!(identity, alias, capabilities)
                && identity.Principal.FindFirst("sub")?.Value is "parent-a" or "parent-b"
        };
    }

    [TestMethod]
    public async Task SharedDataset_MembersReadWriteReceivePushAndRejectStaleUpdates()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(SharedHostOptions(store));
        await using var a = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-a"));
        await using var b = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-b"));
        int hints = 0;
        await using var subscription = b.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        await subscription.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Task.FromResult(hints == 1));
        var saved = await Seed(a, "shared", 1);
        await Until(() => Task.FromResult(hints >= 2));
        var stale = await a.QuerySingleAsync(_ => true);
        var edit = await b.QuerySingleAsync(_ => true);
        Assert.AreEqual(saved.Envelope!.EnvelopeCacheKey, edit.Envelope!.EnvelopeCacheKey);
        edit.Score = 2; await b.SaveAsync(edit);
        Assert.AreEqual(2, (await a.QuerySingleAsync(_ => true)).Score);
        stale.Score = 3;
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await a.SaveAsync(stale));
        Assert.IsTrue(await b.DeleteAsync(await b.QuerySingleAsync(_ => true)));
        Assert.IsNull(await a.QueryFirstAsync(_ => true));
        await subscription.DisposeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
        await using var reconnect = b.SubscribeChanges(() => ValueTask.CompletedTask);
        await reconnect.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Seed(a, "after-reconnect", 4);
        Assert.AreEqual(4, (await b.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task SharedDataset_ServerApprovesScopeAndCachesRemainAccountBound()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(SharedHostOptions(store));
        var options = SharedOptions(host, "parent-a");
        var descriptor = await TcpEncodedEnvelopeStore.DiscoverStoreAsync(options.Connection);
        Assert.AreEqual(DataAccessScope.Unrestricted.Identity, descriptor.ScopeIdentity);
        Assert.AreEqual(Manifest(), descriptor.Manifest);
        var wrong = options.Connection with { ExpectedScopeIdentity = "wrong-scope" };
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.DiscoverAsync(wrong))).Code);
        await using var a = new TcpRepository<IRemotePerson>(options);
        await using var b = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-b"));
        Assert.AreNotEqual(await a.GetCacheIdentityAsync(), await b.GetCacheIdentityAsync());
    }

    [TestMethod]
    public async Task SharedDataset_ClientCannotOverrideServerScope()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store));
        var options = SharedOptions(host, "parent-a").Connection with { ExpectedScopeIdentity = DataAccessScope.Unrestricted.Identity };
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.DiscoverAsync(options))).Code);
    }

    [TestMethod]
    public async Task SharedDataset_OutsiderDeniedDiscoveryOperationsAndSubscriptions()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(SharedHostOptions(store));
        var options = SharedOptions(host, "outsider");
        await using var outsider = new TcpRepository<IRemotePerson>(options);
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await outsider.SaveAsync(new RemotePerson { Name = "forbidden" }))).Code);
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.DiscoverAsync(options.Connection))).Code);
        int hints = 0;
        var subscription = outsider.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => subscription.Ready)).Code);
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => subscription.Completion);
        subscription.Dispose();
        Assert.AreEqual(0, hints);
    }

    [TestMethod]
    public async Task SharedDataset_RevocationRechecksEveryCommandAndNotificationDelivery()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var counting = new CountingPushStore(inner);
        int allowed = 1;
        var options = SharedHostOptions(counting);
        await using var host = await TcpStoreHost.StartAsync(options with
        {
            AuthorizeIdentity = (identity, alias, caps) => options.AuthorizeIdentity!(identity, alias, caps)
                && (identity.Principal.FindFirst("sub")?.Value != "parent-b" || Volatile.Read(ref allowed) == 1)
        });
        await using var b = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-b"));
        int hints = 0;
        var subscription = b.SubscribeChanges(() => { Interlocked.Increment(ref hints); return ValueTask.CompletedTask; });
        await subscription.Ready;
        await Until(() => Task.FromResult(hints == 1));
        await using (var raw = await TcpEncodedEnvelopeStore.ConnectAsync(SharedOptions(host, "parent-b").Connection))
        {
            Interlocked.Exchange(ref allowed, 0);
            foreach (var command in Enum.GetValues<EncodedStoreCommand>())
            {
                var request = command switch
                {
                    EncodedStoreCommand.Get or EncodedStoreCommand.Delete => new EncodedStoreRequest { Command = command, Key = "x" },
                    EncodedStoreCommand.Put => new() { Command = command, Key = "x", Payload = [1] },
                    EncodedStoreCommand.GetMany => new() { Command = command, Keys = ["x"] },
                    EncodedStoreCommand.ApplyBatch => new() { Command = command, Mutations = [new("x", [1]), new("y", null)] },
                    EncodedStoreCommand.Enumerate or EncodedStoreCommand.Changes or EncodedStoreCommand.ReserveRevisions => new() { Command = command, PageSize = 1 },
                    _ => new() { Command = command }
                };
                Assert.AreEqual(RemoteErrorCode.Unauthorized,
                    (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await raw.ExecuteAsync(request))).Code, command.ToString());
            }
            Assert.AreEqual(0, counting.Operations);
        }
        await using var a = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-a"));
        await Seed(a, "permitted", 1);
        await Assert.ThrowsAsync<IOException>(() => subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, hints, "Revoked listener receives no change timing hint.");
        subscription.Dispose();
    }

    [TestMethod]
    public async Task SharedDataset_MixedAliasesKeepPrivateDataAndNotificationsIsolated()
    {
        await using var shared = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "shared"));
        await using var isolated = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "isolated"));
        await using var other = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "other"));
        await using var host = await TcpStoreHost.StartAsync(SharedHostOptions(shared) with
        {
            Stores = [new("people", Manifest(), shared) { AccessPolicy = UnrestrictedDataAccessPolicy.Instance },
                new("private", Manifest(), isolated) { AccessPolicy = new SubjectDataAccessPolicy() },
                new("other", Manifest(), other) { AccessPolicy = UnrestrictedDataAccessPolicy.Instance }],
            AuthorizeIdentity = (identity, alias, _) => identity.Principal.FindFirst("sub")?.Value is "parent-a" or "parent-b"
                && alias is "people" or "private" or "other"
        });
        TcpRepository<IRemotePerson> Private(string subject)
        {
            var o = Options(host, Token(subject));
            return new(o with { Connection = o.Connection with { StoreAlias = "private" } });
        }
        await using var privateA = Private("parent-a");
        await using var privateB = Private("parent-b");
        await using var member = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-a"));
        await using var otherMember = new TcpRepository<IRemotePerson>(SharedOptions(host, "parent-a", "other"));
        int privateHints = 0, otherHints = 0;
        await using var privateSub = privateB.SubscribeChanges(() => { Interlocked.Increment(ref privateHints); return ValueTask.CompletedTask; });
        await using var otherSub = otherMember.SubscribeChanges(() => { Interlocked.Increment(ref otherHints); return ValueTask.CompletedTask; });
        await Until(() => Task.FromResult(privateHints == 1 && otherHints == 1));
        await Seed(privateA, "secret", 1);
        await Seed(member, "shared", 2);
        Assert.IsNull(await privateB.QueryFirstAsync(_ => true));
        Assert.IsNull(await otherMember.QueryFirstAsync(_ => true));
        await Task.Delay(150);
        Assert.AreEqual(1, privateHints);
        Assert.AreEqual(1, otherHints);
    }
}
