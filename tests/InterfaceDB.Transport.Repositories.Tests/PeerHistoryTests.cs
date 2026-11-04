using Idb.Commercial.Repositories;
using Idb.Commercial.Repositories.Peers;
using Idb.Transport.Peers;

namespace InterfaceDB.Transport.Tests;

public sealed partial class PeerReplicatedRepositoryTests
{
    private static PeerRegistrationOptions Registration(PeerJoinMode join = PeerJoinMode.LoadAllHistory,
        PeerResumeMode resume = PeerResumeMode.ResumeAll) => new()
        { RegistrationId = Guid.NewGuid(), Join = join, Resume = resume, DateProperty = nameof(IPeerItem.CreatedAt) };

    private ValueTask<TcpPeerRelayHost> HistoryHost(IPeerSequenceLedger ledger, IPeerRegistrationLedger registrations,
        int port = 0, Func<PeerHistoryPolicy?>? policy = null, bool custom = false)
        => TcpPeerRelayHost.StartAsync(new()
        {
            ListenEndPoint = new(System.Net.IPAddress.Loopback, port), ServerCertificate = _certificates.Server,
            TokenValidator = _validator, AuthorizeIdentity = (_, alias, _) => alias == "entries", Limits = Limits,
            HistoryPolicy = (_, _) => policy is null ? new() : policy(),
            Datasets = [new("entries", Manifest(custom), ledger)
                { Registrations = registrations, HistoryDateProperty = nameof(IPeerItem.CreatedAt) }]
        });

    [TestMethod]
    [DataRow("file", false)] [DataRow("segment", false)] [DataRow("atlas", false)]
    [DataRow("file", true)] [DataRow("segment", true)] [DataRow("atlas", true)]
    public async Task Peer_HistoryRangeUsesInterfaceDatesAcrossProvidersAndSerializers(string engine, bool custom)
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations, custom: custom);
        await using var a = Client("a", host.LocalEndPoint.Port, engine, custom, registration: Registration());
        var start = DateTimeOffset.UtcNow.AddDays(-5); var end = start.AddDays(2);
        foreach (var (key, date) in new[] { ("old", start.AddTicks(-1)), ("start", start), ("inside", start.AddDays(1)), ("end", end) })
            await a.Repository.SaveAsync(new PeerItem { Key = key, CreatedAt = date });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        var choices = Registration(PeerJoinMode.LoadHistoryRange) with { HistoryRange = new(start, end) };
        await using var b = Client("b", host.LocalEndPoint.Port, engine, custom, registration: choices);
        await b.Repository.InitializeAsync();
        await Until(async () => b.Repository.Synchronization.IsComplete && await Count(b.Repository) == 2);
        Assert.IsNotNull(await b.Repository.QueryFirstAsync(x => x.Key == "start"));
        Assert.IsNotNull(await b.Repository.QueryFirstAsync(x => x.Key == "inside"));
        Assert.IsNull(await b.Repository.QueryFirstAsync(x => x.Key == "end"));
        // A full-history newcomer must not expand the existing range client's selection.
        await using var c = Client("c", host.LocalEndPoint.Port, engine, custom, registration: Registration());
        await c.Repository.InitializeAsync();
        await Until(async () => c.Repository.Synchronization.IsComplete && await Count(c.Repository) == 4);
        await b.Repository.SynchronizeAsync(); Assert.AreEqual(2, await Count(b.Repository));
        // New live activity still arrives outside the original backfill interval.
        await a.Repository.SaveAsync(new PeerItem { Key = "live", CreatedAt = end.AddDays(1) });
        await Until(async () => await b.Repository.QueryFirstAsync(x => x.Key == "live") is not null);
    }

    [TestMethod]
    [DataRow(PeerResumeMode.None, 0)]
    [DataRow(PeerResumeMode.ResumeAll, 2)]
    [DataRow(PeerResumeMode.ResumeRange, 1)]
    public async Task Peer_FromNowReconnectUsesIndependentResumeMode(PeerResumeMode resume, int recovered)
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await a.Repository.SaveAsync(new PeerItem { Key = "before" });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        var choices = Registration(PeerJoinMode.FromNow, resume) with
            { ResumeWindow = resume == PeerResumeMode.ResumeRange ? TimeSpan.FromDays(7) : null };
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false, registration: choices);
        await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online));
        Assert.AreEqual(0, await Count(b.Repository));
        await b.Transport.DisconnectAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "missed-recent", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await a.Repository.SaveAsync(new PeerItem { Key = "missed-old", CreatedAt = DateTimeOffset.UtcNow.AddDays(-10) });
        // Updating a pre-join record must not expose it through its newer mutation sequence.
        var before = await a.Repository.QuerySingleAsync(x => x.Key == "before"); before.Text = "edited";
        await a.Repository.SaveAsync(before);
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        b.Transport.Offline = false;
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online
            && b.Repository.Synchronization.ThroughSequence == 4));
        Assert.AreEqual(recovered, await Count(b.Repository));
        Assert.IsNull(await b.Repository.QueryFirstAsync(x => x.Key == "before"));
        await a.Repository.SaveAsync(new PeerItem { Key = "live-old-date", CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) });
        await Until(async () => await b.Repository.QueryFirstAsync(x => x.Key == "live-old-date") is not null);
    }

    [TestMethod]
    public async Task Peer_JoiningBoundarySurvivesClientAndServerRestart()
    {
        var port = FreePort(); var aOptions = Registration(); var bOptions = Registration(PeerJoinMode.FromNow);
        var ledgerPath = Path.Combine(_root, "ledger"); var registrationsPath = Path.Combine(_root, "registrations");
        await using (var ledger = new FilePeerSequenceLedger(ledgerPath))
        await using (var registrations = new FilePeerRegistrationLedger(registrationsPath))
        await using (var host = await HistoryHost(ledger, registrations, port))
        await using (var a = Client("a", port, "file", false, registration: aOptions))
        {
            await a.Repository.SaveAsync(new PeerItem { Key = "before" });
            await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
            await using (var b = Client("b", port, "file", false, registration: bOptions))
            {
                await b.Repository.InitializeAsync();
                await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online));
            }
            await a.Repository.SaveAsync(new PeerItem { Key = "after" });
            await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        }
        await using var reopenedLedger = new FilePeerSequenceLedger(ledgerPath);
        await using var reopenedRegistrations = new FilePeerRegistrationLedger(registrationsPath);
        await using var restarted = await HistoryHost(reopenedLedger, reopenedRegistrations, port);
        await using var source = Client("a", port, "file", false, registration: aOptions);
        await using var resumed = Client("b", port, "file", false, registration: bOptions);
        await source.Repository.InitializeAsync(); await resumed.Repository.InitializeAsync();
        await Until(async () => resumed.Repository.Synchronization.IsComplete && await Count(resumed.Repository) == 1);
        Assert.IsNotNull(await resumed.Repository.QueryFirstAsync(x => x.Key == "after"));
        Assert.IsNull(await resumed.Repository.QueryFirstAsync(x => x.Key == "before"));
    }

    [TestMethod]
    public async Task Peer_HistoryPolicyRejectsRequestsAndRechecksExistingSessions()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        PeerHistoryPolicy policy = new() { AllowHistory = false };
        await using var host = await HistoryHost(ledger, registrations, policy: () => Volatile.Read(ref policy));
        var invalid = new TcpPeerConnectionFactory(Options(host.LocalEndPoint.Port, false), Issuer, "reader", Limits, Registration());
        var error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => invalid.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage())).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, error.Error);
        var allowed = new TcpPeerConnectionFactory(Options(host.LocalEndPoint.Port, false), Issuer, "reader", Limits, Registration(PeerJoinMode.FromNow));
        await using var connection = await allowed.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage { Records = [] }));
        Volatile.Write(ref policy, policy with { AllowResume = false });
        error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => connection.RequestAsync(new() { Command = PeerCommand.Reconcile }).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, error.Error);
    }

    [TestMethod]
    public async Task Peer_RegistrationBindingRejectsReusedIdentityAndChangedPreferences()
    {
        var options = Registration(PeerJoinMode.FromNow);
        var path = Path.Combine(_root, "registrations");
        await using (var registrations = new FilePeerRegistrationLedger(path))
        {
            await registrations.InitializeAsync("scope");
            Assert.AreEqual(3L, (await registrations.GetOrCreateAsync(options, "a", 3)).JoinedThrough);
        }
        await using var reopened = new FilePeerRegistrationLedger(path);
        await reopened.InitializeAsync("scope");
        Assert.AreEqual(3L, (await reopened.GetOrCreateAsync(options, "a", 100)).JoinedThrough);
        var error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => reopened.GetOrCreateAsync(options, "b", 0).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, error.Error);
        error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => reopened.GetOrCreateAsync(options with { Join = PeerJoinMode.LoadAllHistory }, "a", 0).AsTask());
        Assert.AreEqual(PeerError.InvalidRequest, error.Error);
    }

    [TestMethod]
    public async Task Peer_DatesCannotBeChangedAfterFirstSaveOrAllocation()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await a.Repository.SaveAsync(new PeerItem { Key = "original" });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        var entity = await a.Repository.QuerySingleAsync(_ => true); entity.CreatedAt = entity.CreatedAt.AddDays(1);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => a.Repository.SaveAsync(entity).AsTask());
        var head = (await ledger.SnapshotAsync()).Single();
        var mutation = head with { MutationId = Guid.NewGuid(), BaseSequence = head.Sequence, Sequence = 0,
            CreatedSequence = 0, HistoryDateUtc = head.HistoryDateUtc!.Value.AddDays(1) };
        var error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => ledger.AllocateAsync(mutation, "someone").AsTask());
        Assert.AreEqual(PeerError.Conflict, error.Error);
    }

    [TestMethod]
    public void Peer_ResumeWindowsMoveWithoutPollingAndPreserveDeletionRepair()
    {
        var now = DateTimeOffset.UtcNow;
        var request = Registration(PeerJoinMode.FromNow, PeerResumeMode.ResumeRange) with { ResumeWindow = TimeSpan.FromDays(7) };
        var state = new PeerRegistrationState(request, "a", 1, true);
        var row = Header("later", 0) with { Sequence = 2, CreatedSequence = 2, HistoryDateUtc = now.AddDays(-6) };
        var selector = new PeerHistorySelector();
        Assert.IsTrue(selector.Includes(state, 3, row, false, now));
        Assert.IsFalse(selector.Includes(state, 3, row, false, now.AddDays(2)));
        Assert.IsTrue(selector.Includes(state, 3, row with { Deleted = true }, true, now.AddDays(2)));
        var fixedRange = state with { Options = request with { ResumeWindow = null, ResumeRange = new(now.AddDays(-7), now) } };
        Assert.IsTrue(selector.Includes(fixedRange, 3, row, false, now.AddDays(20)));
        Assert.IsFalse(selector.Includes(state, 3, row with { CreatedSequence = 1 }, true, now));
    }

    [TestMethod]
    public void Peer_InvalidRangesAndDateSelectorsFailBeforeConnecting()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.ThrowsExactly<ArgumentException>(() => Registration(PeerJoinMode.LoadHistoryRange).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Registration() with { HistoryRange = new(now.AddDays(-1), now) }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Registration(PeerJoinMode.LoadHistoryRange) with { HistoryRange = new(now, now) }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => Registration(resume: PeerResumeMode.ResumeRange).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Registration(resume: PeerResumeMode.ResumeRange) with
            { ResumeWindow = TimeSpan.FromDays(7), ResumeRange = new(now.AddDays(-1), now) }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new PeerHistoryDateAccessor<IPeerItem>(x => x.CreatedAt.AddDays(1)));
        var selector = new PeerHistoryDateAccessor<IPeerItem>(x => x.CreatedAt);
        Assert.AreEqual(nameof(IPeerItem.CreatedAt), selector.PropertyName);
    }

    [TestMethod]
    public async Task Peer_ConflictingEditsOutsideResumeWindowCanStillBeResolved()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        var choices = Registration(PeerJoinMode.FromNow, PeerResumeMode.ResumeRange) with { ResumeWindow = TimeSpan.FromDays(7) };
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false, registration: choices);
        await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online));
        await a.Repository.SaveAsync(new PeerItem { Key = "conflict", Text = "original", CreatedAt = DateTimeOffset.UtcNow.AddDays(-20) });
        await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1);
        await b.Transport.DisconnectAsync();
        var local = await b.Repository.QuerySingleAsync(_ => true); local.Text = "local"; await b.Repository.SaveAsync(local);
        var remote = await a.Repository.QuerySingleAsync(_ => true); remote.Text = "remote"; await a.Repository.SaveAsync(remote);
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        b.Transport.Offline = false;
        await Until(() => Task.FromResult(b.Repository.Connection.LastFailure is PeerProtocolException { Error: PeerError.Conflict }
            && b.Repository.Synchronization.ThroughSequence == 2));
        await b.Repository.ResolveConflictAsync("conflict", PeerConflictResolution.UseRemote);
        Assert.AreEqual("remote", (await b.Repository.QuerySingleAsync(_ => true)).Text);
        await b.Transport.DisconnectAsync();
        await a.Repository.DeleteAsync(await a.Repository.QuerySingleAsync(_ => true));
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        b.Transport.Offline = false;
        await Until(async () => await b.Repository.QueryFirstAsync(_ => true) is null);
    }

    [TestMethod]
    public async Task Peer_NewRecordCollisionWithinJoiningScopeCanBeResolvedWithoutResume()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false,
            registration: Registration(PeerJoinMode.FromNow, PeerResumeMode.None));
        await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online));
        await b.Transport.DisconnectAsync();
        var date = DateTimeOffset.UtcNow.AddDays(-20);
        await a.Repository.SaveAsync(new PeerItem { Key = "collision", Text = "remote", CreatedAt = date });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        await b.Repository.SaveAsync(new PeerItem { Key = "collision", Text = "local", CreatedAt = date });
        b.Transport.Offline = false;
        await Until(() => Task.FromResult(b.Repository.Connection.LastFailure is PeerProtocolException { Error: PeerError.Conflict }
            && b.Repository.Synchronization.ThroughSequence == 1));
        await b.Repository.ResolveConflictAsync("collision", PeerConflictResolution.UseRemote);
        Assert.AreEqual("remote", (await b.Repository.QuerySingleAsync(_ => true)).Text);
    }

    [TestMethod]
    public void Peer_PendingRepairDoesNotWidenJoiningHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var selector = new PeerHistorySelector();
        var row = Header("pre-join", 0) with { Sequence = 4, CreatedSequence = 1, HistoryDateUtc = now.AddDays(-20) };
        var state = new PeerRegistrationState(Registration(PeerJoinMode.FromNow), "a", 2, true);
        Assert.IsFalse(selector.Includes(state, 4, row, true, now, requiredForPendingWrite: true));
        state = state with { Options = Registration(PeerJoinMode.LoadHistoryRange) with { HistoryRange = new(now.AddDays(-7), now) } };
        Assert.IsFalse(selector.Includes(state, 4, row, true, now, requiredForPendingWrite: true));
        state = state with { Options = state.Options with { Resume = PeerResumeMode.None } };
        Assert.IsTrue(selector.Includes(state, 4, row with { CreatedSequence = 3 }, false, now, requiredForPendingWrite: true));
    }

    [TestMethod]
    public async Task Peer_ServerSequenceRestrictionCannotBeOverriddenByClientHistoryRequest()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        PeerHistoryPolicy policy = new();
        await using var host = await HistoryHost(ledger, registrations, policy: () => Volatile.Read(ref policy));
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await a.Repository.SaveAsync(new PeerItem { Key = "restricted" });
        await a.Repository.SaveAsync(new PeerItem { Key = "permitted" });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        // Changing the old record's latest mutation must not change its authorization boundary.
        var old = await a.Repository.QuerySingleAsync(x => x.Key == "restricted"); old.Text = "edited"; await a.Repository.SaveAsync(old);
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        Volatile.Write(ref policy, new() { MinimumCreatedSequence = 2 });
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online));
        Assert.AreEqual(1, await Count(b.Repository));
        Assert.IsNotNull(await b.Repository.QueryFirstAsync(x => x.Key == "permitted"));
        Assert.IsNull(await b.Repository.QueryFirstAsync(x => x.Key == "restricted"));
        Assert.IsTrue(b.Repository.Synchronization.IsComplete);
        var factory = new TcpPeerConnectionFactory(Options(host.LocalEndPoint.Port, false), Issuer, "reader", Limits, Registration());
        await using var connection = await factory.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage { Records = [] }));
        var mutation = (await ledger.SnapshotAsync()).Single(x => x.RecordId == "restricted") with
            { BaseSequence = 3, Sequence = 0, CreatedSequence = 0, MutationId = Guid.NewGuid() };
        var error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => connection.RequestAsync(new() { Command = PeerCommand.Allocate, Record = mutation }).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, error.Error);
    }

    [TestMethod]
    public async Task Peer_LoadHistoryWithoutResumeDoesNotReloadOnReconnect()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var registrations = new FilePeerRegistrationLedger(Path.Combine(_root, "registrations"));
        await using var host = await HistoryHost(ledger, registrations);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, registration: Registration());
        await a.Repository.SaveAsync(new PeerItem { Key = "initial" });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false, registration: Registration(resume: PeerResumeMode.None));
        await b.Repository.InitializeAsync();
        await Until(async () => b.Repository.Synchronization.IsComplete && await Count(b.Repository) == 1);
        await b.Transport.DisconnectAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "missed" });
        await Until(() => Task.FromResult(a.Repository.Synchronization.PendingRecords == 0));
        b.Transport.Offline = false;
        await Until(() => Task.FromResult(b.Repository.Connection.State == RepositoryConnectionState.Online
            && b.Repository.Synchronization.ThroughSequence == 2));
        Assert.AreEqual(1, await Count(b.Repository));
    }

    [TestMethod]
    public void Peer_FilteredCollectionBalancesOnlyRequestedTransfersAmongAuthorizedHolders()
    {
        var rows = Enumerable.Range(1, 30).Select(i => Header(i.ToString(), 0) with { Sequence = i, CreatedSequence = i }).ToArray();
        var inventories = new Dictionary<string, IReadOnlyList<PeerRecordDescriptor>>
            { ["a"] = rows, ["b"] = rows, ["c"] = rows, ["new"] = [] };
        var transfers = new PeerRangePlanner().Plan(rows, inventories, (_, row) => row.Sequence % 3 == 0);
        Assert.AreEqual(10, transfers.Count);
        var loads = transfers.GroupBy(t => t.Source).Select(g => g.Count()).ToArray();
        Assert.AreEqual(3, loads.Length); Assert.IsTrue(loads.Max() - loads.Min() <= 1);
        transfers = new PeerRangePlanner().Plan(rows, inventories, (_, row) => row.Sequence % 3 == 0, (source, _) => source != "a");
        Assert.IsFalse(transfers.Any(t => t.Source == "a"));
        Assert.AreEqual(2, transfers.Select(t => t.Source).Distinct().Count());
        Assert.IsFalse(new PeerHistoryPolicy { MinimumCreatedSequence = 1 }.Allows(rows[0] with { CreatedSequence = 0 }));
    }
}
