using System.Net;
using System.Text.Json;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private ValueTask<TcpStoreHost> SharedSetupHost(int port, IEncodedEnvelopeStore store, ManagedClientSetup profile, IDataAccessPolicy? policy = null)
    {
        var registry = new ManagedSetupRegistry([new(profile, store,
            identity => identity.Principal.FindFirst("sub")?.Value is "parent-a" or "parent-b"
                && identity.Principal.FindAll("scope").Any(c => c.Value.Split(' ').Contains("interfacedb.storage"))) { AccessPolicy = policy ?? UnrestrictedDataAccessPolicy.Instance }],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1));
        return TcpStoreHost.StartAsync(registry.Configure(HostOptions(store) with
        { Stores = [], AuthorizeIdentity = null, ListenEndPoint = new(IPAddress.Loopback, port), MaximumConnections = 32 }));
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("segment-core")]
    [DataRow("atlas")]
    public async Task SharedSetup_RemoteFirstMembersRefreshCachesAndRecoverOnReconnect(string provider)
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        var profile = Setup(provider: provider);
        var host = await SharedSetupHost(port, store, profile);
        var modelOptions = Options(host, Token("parent-a"));
        var models = new[] { ManagedRepositoryModel<IRemotePerson>.FromTcp(modelOptions) };
        LocalRemoteRepository<IRemotePerson> Open(string subject) => LocalRemoteRepositoryFactory.CreateFromServer(
            Authority(port, subject: subject), models, Providers(), Path.Combine(_root, "cache"), subject, new PersonMapper(),
            retryDelay: TimeSpan.FromMilliseconds(50));
        try
        {
            var approval = Authority(port); await approval.DiscoverAsync();
            Assert.AreEqual("unrestricted-v1", approval.DataConnection(profile.Manifest).ExpectedScopeIdentity);
            await using var writer = new TcpRepository<IRemotePerson>(modelOptions with { Connection = approval.DataConnection(profile.Manifest) });
            await Seed(writer, "message", 1);
            await using var a = Open("parent-a");
            await using var b = Open("parent-b");
            await a.InitializeAsync(); await b.InitializeAsync();
            await Until(() => Task.FromResult(a.Connection.State == RepositoryConnectionState.Online && b.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(RepositoryReadPreference.RemoteFirst, a.EffectiveReadPreference);
            Assert.AreEqual(1, (await b.QuerySingleAsync(_ => true)).Score);
            var update = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var watch = b.SubscribeChanges(async () =>
            {
                if ((await b.QueryFirstAsync(_ => true))?.Score == 2) update.TrySetResult();
            });
            await watch.Ready;
            var item = await a.QuerySingleAsync(_ => true); item.Score = 2; await a.SaveAsync(item);
            await update.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Finish the writer's own catch-up before testing offline use of a confirmed cache.
            Assert.AreEqual(2, (await a.QuerySingleAsync(_ => true)).Score);
            await watch.DisposeAsync();
            await host.DisposeAsync();
            await Until(() => Task.FromResult(a.Connection.IsDowntime && b.Connection.IsDowntime));
            Assert.AreEqual(2, (await b.QuerySingleAsync(_ => true)).Score, "B retains the remote member's change locally.");
            var pending = await a.QuerySingleAsync(_ => true); pending.Score = 3; await a.SaveAsync(pending);
            host = await SharedSetupHost(port, store, profile);
            await Until(async () => a.Connection.State == RepositoryConnectionState.Online && b.Connection.State == RepositoryConnectionState.Online
                && (await b.QuerySingleAsync(_ => true)).Score == 3);
            Assert.AreEqual(RepositoryReadPreference.RemoteFirst, a.EffectiveReadPreference);
            Assert.AreEqual(3, (await writer.QuerySingleAsync(_ => true)).Score);
            var denied = Authority(port, subject: "outsider");
            Assert.AreEqual(RemoteErrorCode.Unauthorized,
                (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await denied.DiscoverAsync())).Code);
        }
        finally { await host.DisposeAsync(); }
    }

    [TestMethod]
    public async Task SharedSetup_ScopeChangeRequiresExplicitCacheMigration()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var original = Setup();
        Assert.IsFalse(JsonSerializer.Serialize(original).Contains("AccessMode"), "Policy implementations must not be serialized to clients.");
        var host = await SharedSetupHost(port, store, original, new SubjectDataAccessPolicy());
        var authority = Authority(port);
        try
        {
            await authority.DiscoverAsync();
            await host.DisposeAsync();
            host = await SharedSetupHost(port, store, original);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await authority.DiscoverAsync());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await Authority(port).DiscoverAsync());
            Assert.AreNotEqual("unrestricted-v1", authority.DataConnection(original.Manifest).ExpectedScopeIdentity);
        }
        finally { await host.DisposeAsync(); }
    }

    [TestMethod]
    public async Task SharedSetup_RejectsConflictingScopesForSameDataset()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        Assert.ThrowsExactly<ArgumentException>(() => new ManagedSetupRegistry(
            [new(Setup(), store, _ => true), new(Setup("other"), store, _ => true) { AccessPolicy = UnrestrictedDataAccessPolicy.Instance }],
            "test-audience", new RsaManagedSetupSigner(_key), TimeSpan.FromHours(1)));
    }
}
