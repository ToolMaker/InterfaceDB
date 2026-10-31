using Idb.Transport;
using Idb.Transport.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private TcpRepositoryOptions<IRemotePerson> GroupOptions(TcpStoreHost host, string subject, string[] groups, string? selected)
    {
        var options = Options(host, Token(subject, additionalClaims: new() { ["groups"] = groups }));
        return options with { Connection = options.Connection with { RequestedScope = selected } };
    }

    [TestMethod]
    public async Task DataPolicy_GroupMembersShareDataWhileOtherGroupsKeepIdenticalKeysPrivate()
    {
        using var services = new ServiceCollection().AddSingleton<IDataAccessPolicy>(new ClaimDataAccessPolicy("groups")).BuildServiceProvider();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
        { DefaultAccessPolicy = services.GetRequiredService<IDataAccessPolicy>() });
        await using var a = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-a", ["a", "b"], "a"));
        await using var b = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-b", ["a"], "a"));
        await using var other = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-a", ["a", "b"], "b"));
        await Seed(a, "same-key", 1);
        await Seed(other, "same-key", 2);
        Assert.AreEqual(1, (await b.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(2, (await other.QuerySingleAsync(_ => true)).Score);
        Assert.AreNotEqual(await a.GetCacheIdentityAsync(), await other.GetCacheIdentityAsync());
        Assert.AreNotEqual(await a.GetCacheIdentityAsync(), await b.GetCacheIdentityAsync(), "Shared records still have actor-bound local caches.");
        var item = await b.QuerySingleAsync(_ => true); item.Score = 3; await b.SaveAsync(item);
        Assert.AreEqual(3, (await a.QuerySingleAsync(_ => true)).Score);
        await b.DeleteAsync(item);
        Assert.IsNull(await a.QueryFirstAsync(_ => true));
        Assert.AreEqual(2, (await other.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task DataPolicy_SelectorsRequireMembershipAndAmbiguousMembershipNeedsSelection()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = new ClaimDataAccessPolicy("groups") });
        foreach (var options in new[] {
            GroupOptions(host, "parent-a", ["a"], "b"),
            GroupOptions(host, "parent-a", ["a", "b"], null),
            GroupOptions(host, "parent-a", [], "a"),
            GroupOptions(host, "parent-a", ["a"], "a/child") })
        {
            Assert.AreEqual(RemoteErrorCode.Unauthorized,
                (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.DiscoverStoreAsync(options.Connection))).Code);
        }
        var one = await TcpEncodedEnvelopeStore.DiscoverStoreAsync(GroupOptions(host, "parent-a", ["a"], null).Connection);
        var selected = await TcpEncodedEnvelopeStore.DiscoverStoreAsync(GroupOptions(host, "parent-b", ["a"], "a").Connection);
        Assert.AreEqual(one.ScopeIdentity, selected.ScopeIdentity);
        var forged = GroupOptions(host, "parent-a", ["a"], "a").Connection with { ExpectedScopeIdentity = DataAccessScope.Unrestricted.Identity };
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.ConnectAsync(forged))).Code);
    }

    [TestMethod]
    public async Task DataPolicy_SubscriptionsAreGroupBoundAndDisposeIndependently()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = new ClaimDataAccessPolicy("groups") });
        await using var a = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-a", ["a"], "a"));
        await using var b = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-b", ["a"], "a"));
        await using var other = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-a", ["b"], "b"));
        int same = 0, different = 0;
        await using var watch = b.SubscribeChanges(() => { Interlocked.Increment(ref same); return ValueTask.CompletedTask; });
        await using var otherWatch = other.SubscribeChanges(() => { Interlocked.Increment(ref different); return ValueTask.CompletedTask; });
        await watch.Ready; await otherWatch.Ready;
        await Until(() => Task.FromResult(same == 1 && different == 1));
        await Seed(a, "new", 1);
        await Until(() => Task.FromResult(same >= 2));
        Assert.AreEqual(1, different, "Another group's change must not produce even a timing hint.");
        await watch.DisposeAsync(); await otherWatch.DisposeAsync();
        await Until(() => Task.FromResult(host.ActiveSubscriptions == 0));
    }

    [TestMethod]
    public async Task DataPolicy_RechecksEveryCommandAndNeverFallsBackAfterFailureOrScopeChange()
    {
        await using var inner = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var counting = new CountingPushStore(inner);
        int state = 0;
        var subject = new SubjectDataAccessPolicy();
        var policy = new DelegateDataAccessPolicy(context => Volatile.Read(ref state) switch
        { 0 => subject.Authorize(context), 1 => null, 2 => throw new InvalidOperationException("policy failure"), _ => DataAccessScope.Unrestricted });
        await using var host = await TcpStoreHost.StartAsync(HostOptions(counting) with { DefaultAccessPolicy = policy });
        await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(Options(host, Token("parent-a")).Connection);
        for (var failure = 1; failure <= 3; failure++)
        {
            Volatile.Write(ref state, failure);
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
                    (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await client.ExecuteAsync(request))).Code);
            }
        }
        Assert.AreEqual(0, counting.Operations);
    }

    [TestMethod]
    public async Task DataPolicy_ChangedServerScopeCannotReuseRepositoryCache()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var first = DataAccessScope.Partitioned("test", "a");
        var second = DataAccessScope.Partitioned("test", "b");
        DataAccessScope current = first;
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
        { DefaultAccessPolicy = new DelegateDataAccessPolicy(_ => Volatile.Read(ref current)) });
        await using var repository = Client(host, "parent-a");
        var cache = await repository.GetCacheIdentityAsync();
        await Seed(repository, "record", 1);
        Volatile.Write(ref current, second);
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await repository.QueryFirstAsync(_ => true))).Code);
        await using var fresh = Client(host, "parent-a");
        Assert.AreNotEqual(cache, await fresh.GetCacheIdentityAsync());
        Assert.IsNull(await fresh.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task DataPolicy_ReadOnlyCapabilitiesApplyInsideTheSelectedGroup()
    {
        var read = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.GetMany | EncodedStoreCapabilities.Enumerate | EncodedStoreCapabilities.DurableCommits;
        var writable = new ClaimDataAccessPolicy("groups");
        var readOnly = new ClaimDataAccessPolicy("groups", allowedCapabilities: read);
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
        { DefaultAccessPolicy = new DelegateDataAccessPolicy(context =>
            (context.Identity?.Principal.FindFirst("sub")?.Value == "parent-a" ? writable : readOnly).Authorize(context)) });
        await using var a = new TcpRepository<IRemotePerson>(GroupOptions(host, "parent-a", ["a"], "a"));
        await Seed(a, "record", 1);
        var options = GroupOptions(host, "parent-b", ["a"], "a");
        await using var b = new TcpRepository<IRemotePerson>(options with { Connection = options.Connection with { RequiredCapabilities = read } });
        Assert.AreEqual(1, (await b.QuerySingleAsync(_ => true)).Score);
        Assert.AreEqual(RemoteErrorCode.Unauthorized,
            (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TcpEncodedEnvelopeStore.ConnectAsync(options.Connection))).Code);
    }
}
