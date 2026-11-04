using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Commercial.Transport.Atlas;
using Idb.Libraries.Repositories;
using Idb.Transport;
using Idb.Transport.Repositories;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    private static int FreeSetupPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private TcpStoreClientOptions SetupConnection(int port, string? subject = "parent-a") => new()
    {
        Host = "127.0.0.1", Port = port, TargetHost = "localhost", StoreAlias = "people", Manifest = Manifest(),
        AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenProvider = new FixedAccessTokenProvider(new AccessToken(Token(subject!))),
        ServerCertificateValidation = _certificates.ValidateServer, ConnectTimeout = TimeSpan.FromSeconds(2), OperationTimeout = TimeSpan.FromSeconds(2)
    };
    private ManagedClientSetup Setup(string name = "mobile", string provider = "file", RepositoryReadPreference mode = RepositoryReadPreference.RemoteFirst,
        CompatibilityManifest? manifest = null) => new(name, provider, mode, ManagedSetupProtocol.ClientManifest(manifest ?? Manifest()), "people", "policy-1");
    private ValueTask<TcpStoreHost> SetupHost(int port, IEncodedEnvelopeStore store, params ManagedClientSetup[] profiles)
    {
        var registry = new ManagedSetupRegistry(profiles.Select(profile => new ManagedSetupRegistration(profile, store,
            identity => identity.Principal.FindFirst("sub")?.Value == "parent-a"
                && identity.Principal.FindAll("scope").Any(claim => claim.Value.Split(' ').Contains("interfacedb.storage")))),
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1));
        return TcpStoreHost.StartAsync(registry.Configure(HostOptions(store) with
        { Stores = [], AuthorizeIdentity = null, ListenEndPoint = new(IPAddress.Loopback, port), MaximumConnections = 32 }));
    }
    private TcpManagedSetupAuthority Authority(int port, string name = "mobile", string contract = "RemotePerson", string? provider = null,
        string subject = "parent-a", IManagedSetupReceiptStore? receipts = null, TimeProvider? clock = null) => new(
        SetupConnection(port, subject), name, contract, new RsaManagedSetupVerifier(_key.ExportSubjectPublicKeyInfo()),
        receipts ?? new FileManagedSetupReceiptStore(Path.Combine(_root, name + "-" + subject + "-approval.json")), provider, clock);
    private static IManagedLocalRepositoryProvider<IRemotePerson>[] Providers() =>
        [new FileManagedProvider<IRemotePerson>(), new SegmentCoreManagedProvider<IRemotePerson>(), new AtlasManagedProvider<IRemotePerson>()];

    [TestMethod]
    [DataRow("file", RepositoryReadPreference.LocalFirst)]
    [DataRow("file", RepositoryReadPreference.RemoteFirst)]
    [DataRow("file-without-dot", RepositoryReadPreference.RemoteFirst)]
    [DataRow("segment-core", RepositoryReadPreference.LocalFirst)]
    [DataRow("segment-core", RepositoryReadPreference.RemoteFirst)]
    [DataRow("atlas", RepositoryReadPreference.LocalFirst)]
    [DataRow("atlas", RepositoryReadPreference.RemoteFirst)]
    public async Task Setup_AllProvidersSupportPubSubOfflineReplayAndServerVersionMigration(string provider, RepositoryReadPreference mode)
    {
        bool extensionWithoutDot = provider == "file-without-dot";
        if (extensionWithoutDot) provider = "file";
        var port = FreeSetupPort();
        var masterPath = Path.Combine(_root, "master");
        var master = await VersionedTcpStore.OpenAsync(masterPath, "people", [StoreV1()]);
        var host = await SetupHost(port, master.Registration.Store, Setup(provider: provider, mode: mode));
        var v1 = Options(host, Token("parent-a")) with { RuntimeTypes = new(typeof(RemotePerson), typeof(PersonMigration)) };
        var v2 = v1 with { Contract = new VersionPersonConfig("2.0.0"), Connection = v1.Connection with { Manifest = Manifest2() } };
        if (extensionWithoutDot)
        {
            v1 = v1 with { Contract = new SetupContract("1.0.0") };
            v2 = v2 with { Contract = new SetupContract("2.0.0") };
        }
        var models = new[] { ManagedRepositoryModel<IRemotePerson>.FromTcp(v1), ManagedRepositoryModel<IRemotePerson>.FromTcp(v2) };
        LocalRemoteRepository<IRemotePerson> Open() => LocalRemoteRepositoryFactory.CreateFromServer(Authority(port), models, Providers(),
            Path.Combine(_root, "cache"), "parent-a", new PersonMapper(), [new PersonMigration()], TimeSpan.FromMilliseconds(50));
        TcpRepository<IRemotePerson> Remote(TcpRepositoryOptions<IRemotePerson> options) => new(options with
        { Connection = options.Connection with { Manifest = ManagedSetupProtocol.ClientManifest(options.Connection.Manifest) } });
        try
        {
            await using (var remote = Remote(v1)) await Seed(remote, "person", 1);
            await using (var managed = Open())
            {
                await managed.InitializeAsync();
                await Until(async () => managed.Connection.State == RepositoryConnectionState.Online && (await managed.QueryFirstAsync(_ => true)) is not null);
                Assert.AreEqual("1.0.0", managed.RunningVersion);
                Assert.AreEqual(mode, managed.EffectiveReadPreference);
                await host.DisposeAsync();
                await Until(() => Task.FromResult(managed.Connection.IsDowntime));
                var pending = await managed.QuerySingleAsync(_ => true); pending.Score = 2; await managed.SaveAsync(pending);
            }
            await using var reopened = Open();
            await reopened.InitializeAsync();
            Assert.AreEqual(2, (await reopened.QuerySingleAsync(_ => true)).Score);
            var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = reopened.SubscribeChanges(() =>
            { if (reopened.RunningVersion == "2.0.0") notification.TrySetResult(); return ValueTask.CompletedTask; });
            await subscription.Ready;
            await master.DisposeAsync();
            master = await VersionedTcpStore.OpenAsync(masterPath, "people", [StoreV1(), StoreV2()]);
            host = await SetupHost(port, master.Registration.Store, Setup(provider: provider, mode: mode, manifest: Manifest2()));
            try { await notification.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { Assert.Fail("Activation failed: " + reopened.Connection.State + " " + reopened.Connection.LastFailure); }
            await Until(() => Task.FromResult(reopened.RunningVersion == "2.0.0" && reopened.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(12, (await reopened.QuerySingleAsync(_ => true)).Score);
            await using var finalRemote = Remote(v2);
            Assert.AreEqual(12, (await finalRemote.QuerySingleAsync(_ => true)).Score);
            Assert.AreEqual(mode, reopened.EffectiveReadPreference);
            var feedback = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var loopCheck = reopened.SubscribeChanges(async () =>
            {
                try { await reopened.SaveAsync(await reopened.QuerySingleAsync(_ => true)); }
                catch (Exception exception) { feedback.TrySetResult(exception); }
            });
            await loopCheck.Ready;
            var changed = await finalRemote.QuerySingleAsync(_ => true); changed.Score = 20; await finalRemote.SaveAsync(changed);
            try { Assert.IsInstanceOfType<RepositoryFeedbackLoopException>(await feedback.Task.WaitAsync(TimeSpan.FromSeconds(15))); }
            catch (TimeoutException) { Assert.Fail("Remote notification missing: " + reopened.Connection.State + " " + reopened.Connection.LastFailure
                + "; observed score=" + (await reopened.QuerySingleAsync(_ => true)).Score + "; subscription=" + loopCheck.Completion.Status); }
            await loopCheck.DisposeAsync();
            await reopened.SaveAsync(new RemotePerson { Name = "created-on-client", Score = 7 });
            await Until(async () => (await finalRemote.QueryFirstAsync(person => person.Name == "created-on-client"))?.Score == 7);
            await Until(async () => (await reopened.QueryFirstAsync(person => person.Name == "created-on-client"))?.Score == 7);
            Assert.IsTrue(await reopened.DeleteAsync(await reopened.QuerySingleAsync(person => person.Name == "created-on-client")));
            await Until(async () => (await reopened.QueryFirstAsync(person => person.Name == "created-on-client")) is null);
        }
        finally { await host.DisposeAsync(); await master.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Setup_OnlyRequestedApprovedProjectionIsReturnedAndNoCatalogueIsAvailable()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "server-secret-path"));
        await using var host = await SetupHost(port, store, Setup(), Setup("desktop-private-name", "atlas", RepositoryReadPreference.LocalFirst));
        var first = Authority(port, provider: "file");
        Assert.AreEqual(ManagedSetupProtocol.ClientManifest(Manifest()), await first.DiscoverAsync());
        Assert.AreEqual("file", first.LocalProviderId);
        Assert.AreEqual(RepositoryReadPreference.RemoteFirst, first.ReadPreference);
        var json = JsonSerializer.Serialize(first.Setup);
        Assert.IsFalse(json.Contains("server-secret-path") || json.Contains("SegmentCore") || json.Contains("desktop-private-name"));
        var second = Authority(port, "desktop-private-name"); await second.DiscoverAsync();
        Assert.AreEqual("atlas", second.LocalProviderId);
        foreach (var candidate in new[] { Authority(port, "missing"), Authority(port, subject: "parent-b"),
            Authority(port, contract: "DifferentContract"), Authority(port, provider: "atlas") })
        {
            var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await candidate.DiscoverAsync());
            Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        }
        var raw = SetupConnection(port) with { StoreAlias = ManagedSetupProtocol.Alias("RemotePerson", "mobile"), Manifest = ManagedSetupProtocol.Manifest,
            RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits };
        await using var connection = await TcpEncodedEnvelopeStore.ConnectAsync(raw);
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await connection.EnumerateAsync(10));
        await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await connection.PutAsync("config", [1]));
    }

    [TestMethod]
    public async Task Setup_RejectionOpensNoLocalProviderAndDoesNotFallBackToCachedApproval()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup());
        var authority = Authority(port); await authority.DiscoverAsync();
        var options = Options(host, Token("parent-a"));
        await host.DisposeAsync();
        host = await SetupHost(port, store, Setup("replacement"));
        try
        {
            var tracker = new RejectOpeningProvider();
            await using var managed = LocalRemoteRepositoryFactory.CreateFromServer(Authority(port),
                [ManagedRepositoryModel<IRemotePerson>.FromTcp(options)], [tracker], Path.Combine(_root, "cache"), "parent-a", new PersonMapper());
            var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await managed.InitializeAsync());
            Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code); Assert.IsFalse(tracker.Opened);
        }
        finally { await host.DisposeAsync(); }
    }

    private sealed class RejectOpeningProvider : IManagedLocalRepositoryProvider<IRemotePerson>
    {
        public bool Opened;
        public string Id => "file";
        public string GetVersionDirectory(ManagedProviderContext<IRemotePerson> context) => throw new AssertFailedException("Provider accessed before approval.");
        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services, ManagedProviderContext<IRemotePerson> context)
        { Opened = true; throw new AssertFailedException("Provider opened before approval."); }
        public ValueTask MigrateAsync(ManagedProviderContext<IRemotePerson> previous, ManagedProviderContext<IRemotePerson> selected,
            IServiceProvider target, CancellationToken cancellationToken) => throw new AssertFailedException("Migration before approval.");
    }

    private sealed class SetupContract(string version) : Idb.Libraries.Abstractions.Dtos.IDbContractConfigDto<IRemotePerson>
    {
        public string EnvelopeDICacheKey => "people";
        public string ContractName => "RemotePerson";
        public string FileExtension => "mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => null;
        public string? ActiveVersion => version;
        public Idb.Libraries.Abstractions.Options.StoragePolicy? StoragePolicy => null;
        public Idb.Libraries.Abstractions.Enums.ContractRepositoryType RepositoryType => Idb.Libraries.Abstractions.Enums.ContractRepositoryType.Remote;
    }

    private sealed class SetupReceipts : IManagedSetupReceiptStore
    {
        public string? Value;
        // Deliberately ignores scope to simulate copying an approval into another account/endpoint's cache.
        public ValueTask<string?> ReadAsync(string scope, CancellationToken cancellationToken = default) => ValueTask.FromResult(Value);
        public ValueTask WriteAsync(string scope, string signedReceipt, CancellationToken cancellationToken = default)
        { Value = signedReceipt; return ValueTask.CompletedTask; }
    }

    [TestMethod]
    public async Task Setup_ApprovalRenewsWhileIdleAndRestoresOnlineState()
    {
        var port = FreeSetupPort(); var receipts = new SetupReceipts();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var registry = new ManagedSetupRegistry([new(Setup(), store, _ => true)],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromSeconds(2));
        await using var host = await TcpStoreHost.StartAsync(registry.Configure(HostOptions(store) with
            { Stores = [], AuthorizeIdentity = null, ListenEndPoint = new(IPAddress.Loopback, port) }));
        var authority = Authority(port, receipts: receipts);
        await using var managed = LocalRemoteRepositoryFactory.CreateFromServer(authority,
            [ManagedRepositoryModel<IRemotePerson>.FromTcp(Options(host, Token("parent-a")))], Providers(),
            Path.Combine(_root, "cache"), "parent-a", new PersonMapper(), retryDelay: TimeSpan.FromMilliseconds(50));
        await managed.InitializeAsync();
        await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
        var first = receipts.Value;
        await Until(() => Task.FromResult(receipts.Value != first));
        var second = receipts.Value;
        await Until(() => Task.FromResult(receipts.Value != second));
        await authority.ValidateAccessAsync(true);
        await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
    }
    private sealed class SetupClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Setup_ExpiredApprovalBlocksAccessButRenewsWithoutRestart(bool restartWhileExpired)
    {
        var port = FreeSetupPort(); var receipts = new SetupReceipts(); var clock = new SetupClock();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup());
        var model = ManagedRepositoryModel<IRemotePerson>.FromTcp(Options(host, Token("parent-a")));
        LocalRemoteRepository<IRemotePerson> Open() => LocalRemoteRepositoryFactory.CreateFromServer(
            Authority(port, receipts: receipts, clock: clock), [model], Providers(), Path.Combine(_root, "cache"),
            "parent-a", new PersonMapper(), retryDelay: TimeSpan.FromMilliseconds(30));
        var managed = Open();
        try
        {
            await managed.InitializeAsync();
            await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
            await host.DisposeAsync();
            await Until(() => Task.FromResult(managed.Connection.IsDowntime));
            await managed.SaveAsync(new RemotePerson { Name = "pending", Score = 17 });
            if (restartWhileExpired) { await managed.DisposeAsync(); managed = Open(); }
            clock.Now += TimeSpan.FromDays(2);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => managed.QueryFirstAsync(_ => true).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => managed.SaveAsync(new RemotePerson { Name = "denied", Score = 99 }).AsTask());
            await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Faulted));
            clock.Now = DateTimeOffset.UtcNow;
            host = await SetupHost(port, store, Setup());
            await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
            Assert.AreEqual(17, (await managed.QuerySingleAsync(_ => true)).Score);
        }
        finally { await managed.DisposeAsync(); await host.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("tampered")]
    [DataRow("wrong-key")]
    [DataRow("expired")]
    [DataRow("other-account")]
    [DataRow("other-endpoint")]
    [DataRow("other-setup")]
    public async Task Setup_OfflineApprovalRejectsForgeryAndReplayOutsideItsScope(string defect)
    {
        var port = FreeSetupPort(); var receipts = new SetupReceipts(); var clock = new SetupClock();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup());
        var authority = Authority(port, receipts: receipts, clock: clock);
        await authority.DiscoverAsync(); await authority.ValidateAccessAsync(false);
        await host.DisposeAsync();
        var options = SetupConnection(port, defect == "other-account" ? "parent-b" : "parent-a");
        if (defect == "other-endpoint") options = options with { TargetHost = "another-service.example" };
        if (defect == "expired") clock.Now += TimeSpan.FromDays(2);
        if (defect == "tampered")
        {
            var receipt = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(receipts.Value!))!;
            var payload = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(receipt["Payload"]!.GetValue<string>()))!;
            payload["Setup"]!["LocalProviderId"] = "atlas";
            receipt["Payload"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
            receipts.Value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(receipt));
        }
        using var otherKey = RSA.Create(2048);
        var verifier = new RsaManagedSetupVerifier((defect == "wrong-key" ? otherKey : _key).ExportSubjectPublicKeyInfo());
        var reopened = new TcpManagedSetupAuthority(options, defect == "other-setup" ? "desktop" : "mobile", "RemotePerson", verifier, receipts, clock: clock);
        if (defect is "tampered" or "wrong-key")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reopened.DiscoverAsync());
        else if (defect == "expired") await Assert.ThrowsExactlyAsync<RepositoryAccessRenewalRequiredException>(async () => await reopened.DiscoverAsync());
        else await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(async () => await reopened.DiscoverAsync());
        if (defect == "expired") await Assert.ThrowsExactlyAsync<RepositoryAccessRenewalRequiredException>(async () => await authority.ValidateAccessAsync(true));
    }

    [TestMethod]
    public async Task Setup_ObservationRequestsRediscoveryWhenPolicyChangesBeforeSubscription()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup(mode: RepositoryReadPreference.LocalFirst));
        var authority = Authority(port);
        CompatibilityManifest manifest;
        try { manifest = await authority.DiscoverAsync(); }
        finally { await host.DisposeAsync(); }

        await using var restarted = await SetupHost(port, store,
            Setup(mode: RepositoryReadPreference.RemoteFirst) with { Revision = "policy-2" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await authority.ObserveAsync(manifest, timeout.Token);
        Assert.AreEqual(manifest, await authority.DiscoverAsync(timeout.Token));
        Assert.AreEqual(RepositoryReadPreference.RemoteFirst, authority.ReadPreference);
    }

    [TestMethod]
    public async Task Setup_ReconnectAppliesServerReadModeWithoutChangingVersionOrLosingData()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup(mode: RepositoryReadPreference.LocalFirst));
        var options = Options(host, Token("parent-a"));
        await using var managed = LocalRemoteRepositoryFactory.CreateFromServer(Authority(port),
            [ManagedRepositoryModel<IRemotePerson>.FromTcp(options)], Providers(), Path.Combine(_root, "cache"), "parent-a", new PersonMapper(),
            retryDelay: TimeSpan.FromMilliseconds(50));
        try
        {
            await managed.InitializeAsync();
            await managed.SaveAsync(new RemotePerson { Name = "saved", Score = 3 });
            await Until(() => Task.FromResult(managed.Connection.State == RepositoryConnectionState.Online));
            await host.DisposeAsync();
            await Until(() => Task.FromResult(managed.Connection.IsDowntime));
            host = await SetupHost(port, store, Setup(mode: RepositoryReadPreference.RemoteFirst) with { Revision = "policy-2" });
            try
            {
                await Until(() => Task.FromResult(managed.EffectiveReadPreference == RepositoryReadPreference.RemoteFirst
                    && managed.Connection.State == RepositoryConnectionState.Online));
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Read mode after reconnect: {managed.EffectiveReadPreference}; connection: {managed.Connection.State}; failure: {managed.Connection.LastFailure}");
            }
            Assert.AreEqual("1.0.0", managed.RunningVersion);
            Assert.AreEqual(3, (await managed.QuerySingleAsync(_ => true)).Score);
        }
        finally { await host.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Setup_ProviderSwitchRequiresMigrationAndCannotSilentlyReplaceAnExistingCache()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup());
        await Authority(port).DiscoverAsync();
        await host.DisposeAsync();
        host = await SetupHost(port, store, Setup(provider: "atlas"));
        try { await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await Authority(port).DiscoverAsync()); }
        finally { await host.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("provider")]
    [DataRow("model")]
    public async Task Setup_UnavailableClientCapabilitiesFailBeforeLocalStorageOpens(string defect)
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var setup = defect == "provider" ? Setup(provider: "uninstalled") : Setup(manifest: Manifest() with { ContractSchemaHash = "unsupported-schema" });
        await using var host = await SetupHost(port, store, setup);
        var tracker = new RejectOpeningProvider();
        await using var managed = LocalRemoteRepositoryFactory.CreateFromServer(Authority(port),
            [ManagedRepositoryModel<IRemotePerson>.FromTcp(Options(host, Token("parent-a")))], [tracker], Path.Combine(_root, "cache"), "parent-a", new PersonMapper());
        if (defect == "provider") await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await managed.InitializeAsync());
        else await Assert.ThrowsExactlyAsync<RepositoryUpgradeRequiredException>(async () => await managed.InitializeAsync());
        Assert.IsFalse(tracker.Opened);
    }

    [TestMethod]
    public async Task Setup_DirectDataAccessStillRequiresJwtPermissionAndOneAliasPerDataset()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        await using var host = await SetupHost(port, store, Setup());
        var options = Options(host, Token("parent-b"));
        await using var denied = new TcpRepository<IRemotePerson>(options with
        { Connection = options.Connection with { Manifest = ManagedSetupProtocol.ClientManifest(Manifest()) } });
        var rejection = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await denied.QueryFirstAsync(_ => true));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejection.Code);
        Assert.ThrowsExactly<ArgumentException>(() => new ManagedSetupRegistry(
            [new(Setup(), store, _ => true), new(Setup("other") with { RemoteAlias = "duplicate-data-alias" }, store, _ => true)],
            TcpManagedSetupAuthority.Audience(SetupConnection(port)), new RsaManagedSetupSigner(_key), TimeSpan.FromDays(1)));
    }

    [TestMethod]
    public async Task Setup_UnsupportedApprovedUpgradeCannotBeBypassedByRestartingOffline()
    {
        var port = FreeSetupPort();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(_root, "master"));
        var host = await SetupHost(port, store, Setup());
        var model = ManagedRepositoryModel<IRemotePerson>.FromTcp(Options(host, Token("parent-a")));
        LocalRemoteRepository<IRemotePerson> Open() => LocalRemoteRepositoryFactory.CreateFromServer(Authority(port), [model], Providers(),
            Path.Combine(_root, "cache"), "parent-a", new PersonMapper(), retryDelay: TimeSpan.FromMilliseconds(50));
        await using (var first = Open()) await first.InitializeAsync();
        await host.DisposeAsync();
        host = await SetupHost(port, store, Setup(manifest: Manifest2()));
        try
        {
            await using (var outdated = Open())
                await Assert.ThrowsExactlyAsync<RepositoryUpgradeRequiredException>(async () => await outdated.InitializeAsync());
        }
        finally { await host.DisposeAsync(); }
        await using var offline = Open();
        await Assert.ThrowsExactlyAsync<RepositoryUpgradeRequiredException>(async () => await offline.InitializeAsync());
    }
}
