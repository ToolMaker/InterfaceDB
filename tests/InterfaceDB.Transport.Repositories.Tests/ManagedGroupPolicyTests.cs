using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow("file")]
    [DataRow("segment-core")]
    [DataRow("atlas")]
    public async Task GroupSetup_BindsSignedApprovalAndOfflineCacheToAccountAndSelectedGroup(string provider)
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server"));
        var profile = Setup(provider: provider);
        var host = await SharedSetupHost(port, store, profile, new ClaimDataAccessPolicy("groups"));
        var models = new[] { ManagedRepositoryModel<IRemotePerson>.FromTcp(Options(host, Token("parent-a"))) };
        TcpManagedSetupAuthority Approval(string subject, string group, string[] memberships) => new(
            SetupConnection(port, subject) with
            {
                RequestedScope = group,
                TokenProvider = new FixedAccessTokenProvider(new AccessToken(Token(subject, additionalClaims: new() { ["groups"] = memberships })))
            }, "mobile", "RemotePerson", new RsaManagedSetupVerifier(_key.ExportSubjectPublicKeyInfo()),
            new FileManagedSetupReceiptStore(Path.Combine(_root, subject + "-" + group + ".json")));
        LocalRemoteRepository<IRemotePerson> Open(string subject, string group) => LocalRemoteRepositoryFactory.CreateFromServer(
            Approval(subject, group, ["a", "b"]), models, Providers(), Path.Combine(_root, "cache"), subject, new PersonMapper(),
            retryDelay: TimeSpan.FromMilliseconds(50));
        try
        {
            var approval = Approval("parent-a", "a", ["a", "b"]);
            await approval.DiscoverAsync();
            var shared = Approval("parent-b", "a", ["a"]); await shared.DiscoverAsync();
            var other = Approval("parent-a", "b", ["b"]); await other.DiscoverAsync();
            Assert.AreEqual(approval.DataConnection(profile.Manifest).ExpectedScopeIdentity, shared.DataConnection(profile.Manifest).ExpectedScopeIdentity);
            Assert.AreNotEqual(approval.DataConnection(profile.Manifest).ExpectedScopeIdentity, other.DataConnection(profile.Manifest).ExpectedScopeIdentity);
            Assert.AreEqual(RemoteErrorCode.Unauthorized,
                (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await Approval("parent-b", "a", ["b"]).DiscoverAsync())).Code);

            await using (var a = Open("parent-a", "a"))
            await using (var b = Open("parent-b", "a"))
            await using (var different = Open("parent-a", "b"))
            {
                // Setup approvals are leased per subject. The first client's version observer
                // can briefly own parent-a's approval lease while the other group initializes.
                // Busy is an explicit, retryable admission result, not an authorization failure.
                // Retry only that result within the fixture's bounded deadline.
                foreach (var repository in new[] { a, b, different })
                    await Until(async () =>
                    {
                        try { await repository.InitializeAsync(); return true; }
                        catch (RemoteStoreException exception) when (exception.Code == RemoteErrorCode.Busy) { return false; }
                    });
                await Until(() => Task.FromResult(a.Connection.State == RepositoryConnectionState.Online && b.Connection.State == RepositoryConnectionState.Online));
                await a.SaveAsync(new RemotePerson { Name = "shared", Score = 17 });
                await Until(async () => (await b.QueryFirstAsync(_ => true))?.Score == 17);
                Assert.IsNull(await different.QueryFirstAsync(_ => true));
                await host.DisposeAsync();
                await Until(() => Task.FromResult(b.Connection.IsDowntime));
            }
            await using var offline = Open("parent-b", "a");
            await offline.InitializeAsync();
            Assert.AreEqual(17, (await offline.QuerySingleAsync(_ => true)).Score);
        }
        finally { await host.DisposeAsync(); }
    }
}
