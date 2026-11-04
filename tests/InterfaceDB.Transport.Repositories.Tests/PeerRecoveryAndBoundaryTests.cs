using System.Buffers.Binary;
using Idb.Commercial.Repositories;
using Idb.Commercial.Repositories.Peers;
using Idb.Transport.Peers;

namespace InterfaceDB.Transport.Tests;

public sealed partial class PeerReplicatedRepositoryTests
{
    [TestMethod]
    public async Task Peer_CrashWithNewerLocalEditAndUnacknowledgedAssignmentRecoversBothOperations()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using (var a = Client("a", host.LocalEndPoint.Port, "file", false))
        {
            a.Transport.HoldAllocationReply = true;
            await a.Repository.SaveAsync(new PeerItem { Key = "changing", Text = "first" });
            await a.Transport.Allocated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var next = await a.Repository.QuerySingleAsync(_ => true); next.Text = "newer";
            await a.Repository.SaveAsync(next);
        }
        await using var restarted = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var receiver = Client("b", host.LocalEndPoint.Port, "file", false);
        await restarted.Repository.InitializeAsync(); await receiver.Repository.InitializeAsync();
        Assert.AreEqual("newer", (await restarted.Repository.QuerySingleAsync(_ => true)).Text);
        await Until(async () => (await receiver.Repository.QueryFirstAsync(_ => true)) is { Sequence: 2, Text: "newer" }
            && restarted.Repository.Synchronization.PendingRecords == 0);
        Assert.AreEqual(2L, (await ledger.SnapshotAsync()).Single().Sequence);
    }

    [TestMethod]
    public async Task Peer_MultipleDatasetsShareOnePortWithoutSharingRecordsOrSequences()
    {
        await using var alphaLedger = new FilePeerSequenceLedger(Path.Combine(_root, "alpha-ledger"));
        await using var betaLedger = new FilePeerSequenceLedger(Path.Combine(_root, "beta-ledger"));
        await using var host = await TcpPeerRelayHost.StartAsync(new()
        {
            ListenEndPoint = new(System.Net.IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
            TokenValidator = _validator, AuthorizeIdentity = (_, alias, _) => alias is "alpha" or "beta", Limits = Limits,
            Datasets = [new("alpha", Manifest(false), alphaLedger), new("beta", Manifest(false), betaLedger)]
        });
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, dataset: "alpha");
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false, dataset: "beta");
        await using var c = Client("c", host.LocalEndPoint.Port, "file", false, dataset: "alpha");
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync(); await c.Repository.InitializeAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "same-id", Text = "alpha" });
        await b.Repository.SaveAsync(new PeerItem { Key = "same-id", Text = "beta" });
        await Until(async () => (await c.Repository.QueryFirstAsync(_ => true))?.Text == "alpha"
            && (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1);
        Assert.AreEqual("beta", (await b.Repository.QuerySingleAsync(_ => true)).Text);
        Assert.AreEqual(1L, (await alphaLedger.SnapshotAsync()).Single().Sequence);
        Assert.AreEqual(1L, (await betaLedger.SnapshotAsync()).Single().Sequence);
    }

    [TestMethod]
    public async Task Peer_RejectsAlteredPayloadAndUsesAnotherHolder()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "untampered", Text = "valid" });
        await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1);
        a.Transport.CorruptReads = true;
        await using var c = Client("c", host.LocalEndPoint.Port, "file", false);
        await c.Repository.InitializeAsync();
        await Until(async () => (await c.Repository.QueryFirstAsync(_ => true))?.Text == "valid");
        Assert.IsTrue(a.Transport.Reads > 0 && b.Transport.Reads > 0);
    }

    [TestMethod]
    public async Task Peer_RevokedSessionCannotAllocateOrRequestPeerData()
    {
        var allowed = 1;
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await TcpPeerRelayHost.StartAsync(new()
        {
            ListenEndPoint = new(System.Net.IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
            TokenValidator = _validator, AuthorizeIdentity = (_, _, _) => Volatile.Read(ref allowed) != 0, Limits = Limits,
            Datasets = [new("entries", Manifest(false), ledger)]
        });
        var factory = new TcpPeerConnectionFactory(Options(host.LocalEndPoint.Port, false), Issuer, "reader", Limits);
        await using var connection = await factory.ConnectAsync((request, _) => ValueTask.FromResult(new PeerMessage { Records = [] }));
        Volatile.Write(ref allowed, 0);
        var exception = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => connection.RequestAsync(new()
            { Command = PeerCommand.Allocate, Record = Header("forbidden", 0) }).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, exception.Error);
        exception = await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => connection.RequestAsync(new() { Command = PeerCommand.Reconcile }).AsTask());
        Assert.AreEqual(PeerError.Unauthorized, exception.Error);
        Assert.AreEqual(0, (await ledger.SnapshotAsync()).Count);
    }

    [TestMethod]
    public async Task Peer_InterruptedProjectionReplaysJournalBeforePublishing()
    {
        var port = FreePort();
        await using (var interrupted = Client("a", port, "file", false, journalFailure: true))
            await Assert.ThrowsExactlyAsync<IOException>(() => interrupted.Repository.SaveAsync(new PeerItem { Key = "survives", Text = "journal first" }).AsTask());
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger, port);
        await using var recovered = Client("a", port, "file", false);
        await using var other = Client("b", port, "file", false);
        await recovered.Repository.InitializeAsync(); await other.Repository.InitializeAsync();
        Assert.AreEqual("journal first", (await recovered.Repository.QuerySingleAsync(_ => true)).Text);
        await Until(async () => (await other.Repository.QueryFirstAsync(_ => true))?.Text == "journal first");
        Assert.AreEqual(1L, (await ledger.SnapshotAsync()).Single().Sequence);
    }

    [TestMethod]
    public async Task Peer_OfflineDeletionPreventsStaleDeviceResurrectingRecord()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "gone", Text = "old" });
        await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1);
        await b.Transport.DisconnectAsync();
        await a.Repository.DeleteAsync(await a.Repository.QuerySingleAsync(_ => true));
        await Until(async () => (await ledger.SnapshotAsync()).Single().Deleted);
        b.Transport.Offline = false;
        await Until(async () => await b.Repository.QueryFirstAsync(_ => true) is null && b.Repository.Synchronization.ThroughSequence == 2);
        await a.Repository.SynchronizeAsync();
        Assert.AreEqual(2L, (await ledger.SnapshotAsync()).Single().Sequence);
    }

    [TestMethod]
    [DataRow(PeerConflictResolution.KeepLocal)]
    [DataRow(PeerConflictResolution.UseRemote)]
    public async Task Peer_ConflictingOfflineEditsStayPendingUntilExplicitResolution(PeerConflictResolution choice)
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
        await a.Repository.SaveAsync(new PeerItem { Key = "shared", Text = "original" });
        await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1);
        await b.Transport.DisconnectAsync();
        var local = await b.Repository.QuerySingleAsync(_ => true); local.Text = "local edit";
        await b.Repository.SaveAsync(local);
        var remote = await a.Repository.QuerySingleAsync(_ => true); remote.Text = "remote edit";
        await a.Repository.SaveAsync(remote);
        await Until(async () => (await ledger.SnapshotAsync()).Single().Sequence == 2);
        b.Transport.Offline = false;
        await Until(() => Task.FromResult(b.Repository.Connection.LastFailure is PeerProtocolException { Error: PeerError.Conflict }
            && b.Repository.Synchronization.ThroughSequence == 2));
        Assert.AreEqual("local edit", (await b.Repository.QuerySingleAsync(_ => true)).Text);
        Assert.AreEqual(1, b.Repository.Synchronization.PendingRecords);
        await b.Repository.ResolveConflictAsync("shared", choice);
        var expected = choice == PeerConflictResolution.KeepLocal ? "local edit" : "remote edit";
        await Until(async () => (await a.Repository.QuerySingleAsync(_ => true)).Text == expected
            && (await b.Repository.QuerySingleAsync(_ => true)).Text == expected && b.Repository.Synchronization.PendingRecords == 0);
        Assert.AreEqual(choice == PeerConflictResolution.KeepLocal ? 3L : 2L, (await ledger.SnapshotAsync()).Single().Sequence);
    }

    [TestMethod]
    public async Task Peer_StartupSharesReadsAndReassignsAnUnavailableSource()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var b = Client("b", host.LocalEndPoint.Port, "segment", false);
        await using var c = Client("c", host.LocalEndPoint.Port, "atlas", false);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync(); await c.Repository.InitializeAsync();
        for (var i = 0; i < 12; i++) await a.Repository.SaveAsync(new PeerItem { Key = "r" + i });
        await Until(async () => await Count(b.Repository) == 12 && await Count(c.Repository) == 12 && a.Repository.Synchronization.PendingRecords == 0);
        await a.Repository.SynchronizeAsync();
        a.Transport.Reads = b.Transport.Reads = c.Transport.Reads = 0;
        a.Transport.FailReads = true;
        await using var newcomer = Client("new", host.LocalEndPoint.Port, "file", false);
        await newcomer.Repository.InitializeAsync();
        await Until(async () => await Count(newcomer.Repository) == 12 && newcomer.Repository.Synchronization.IsComplete);
        Assert.IsTrue(a.Transport.Reads > 0 && b.Transport.Reads > 0 && c.Transport.Reads > 0);
        Assert.AreEqual(0, newcomer.Transport.Reads);
        Assert.AreEqual(12, (await ledger.SnapshotAsync()).Count);
    }

    [TestMethod]
    public async Task Peer_RecordUnavailableUntilItsHolderReconnects()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await a.Repository.SaveAsync(new PeerItem { Key = "only-copy" });
        await Until(async () => (await a.Repository.QuerySingleAsync(_ => true)).Sequence == 1);
        await a.Transport.DisconnectAsync();
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false);
        await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(b.Repository.Synchronization.Reconciled));
        Assert.AreEqual(1, b.Repository.Synchronization.MissingRecords);
        Assert.IsFalse(b.Repository.Synchronization.IsComplete);
        a.Transport.Offline = false;
        await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == 1 && b.Repository.Synchronization.IsComplete);
    }

    [TestMethod]
    public async Task Peer_SequenceAcknowledgementDoesNotInvalidateOutstandingLocalEdit()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false, offline: true);
        var saved = await a.Repository.SaveAsync(new PeerItem { Key = "editing", Text = "first" });
        var revision = saved.Envelope!.RevisionTimestampUtcMicroseconds;
        a.Transport.Offline = false;
        await Until(async () => (await a.Repository.QuerySingleAsync(_ => true)).Sequence == 1);
        Assert.AreEqual(revision, (await a.Repository.QuerySingleAsync(_ => true)).Envelope!.RevisionTimestampUtcMicroseconds);
        saved.Text = "second";
        await a.Repository.SaveAsync(saved);
        await Until(async () => (await a.Repository.QuerySingleAsync(_ => true)).Sequence == 2);
    }

    [TestMethod]
    public async Task Peer_FrameRejectsOversizeTruncationUnknownCommandsAndMalformedJson()
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(prefix, 65537);
        await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => PeerFrameCodec.ReadAsync(new MemoryStream(prefix), Limits, default).AsTask());
        BinaryPrimitives.WriteInt32BigEndian(prefix, 2);
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(4), Limits.MaximumPayloadBytes + 1);
        await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => PeerFrameCodec.ReadAsync(new MemoryStream(prefix), Limits, default).AsTask());
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => PeerFrameCodec.ReadAsync(new MemoryStream([1, 2]), Limits, default).AsTask());
        using var stream = new MemoryStream();
        await PeerFrameCodec.WriteAsync(stream, new() { Command = (PeerCommand)999 }, Limits, default);
        stream.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PeerFrameCodec.ReadAsync(stream, Limits, default).AsTask());
        var malformed = System.Text.Encoding.UTF8.GetBytes("{\"unrecognized\":true}");
        BinaryPrimitives.WriteInt32BigEndian(prefix, malformed.Length); BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(4), 0);
        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(() => PeerFrameCodec.ReadAsync(new MemoryStream([.. prefix, .. malformed]), Limits, default).AsTask());
    }

    [TestMethod]
    public async Task Peer_JournalRefusesCrossAccountReuseAndConcurrentOwnership()
    {
        var path = Path.Combine(_root, "journal");
        await using (var first = new FilePeerJournal(path))
        {
            await first.InitializeAsync("account-a");
            await using var second = new FilePeerJournal(path);
            await Assert.ThrowsExactlyAsync<IOException>(() => second.InitializeAsync("account-a").AsTask());
        }
        await using var wrongAccount = new FilePeerJournal(path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrongAccount.InitializeAsync("account-b").AsTask());
    }

    private sealed class ControlledFactory(IPeerConnectionFactory inner) : IPeerConnectionFactory
    {
        private IPeerConnection? _active;
        internal volatile bool Offline;
        internal volatile bool FailReads;
        internal volatile bool CorruptReads;
        internal volatile bool HoldAllocationReply;
        internal TaskCompletionSource<PeerRecordDescriptor> Allocated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        public string Scope => inner.Scope;
        public string? HistoryDateProperty => inner.HistoryDateProperty;
        public async ValueTask<IPeerConnection> ConnectAsync(Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> handler, CancellationToken cancellationToken = default)
        {
            if (Offline) throw new EndOfStreamException("Device offline.");
            var connection = await inner.ConnectAsync(async (request, token) =>
            {
                if (request.Command == PeerCommand.Read)
                {
                    Interlocked.Increment(ref Reads);
                    if (FailReads) throw new EndOfStreamException("Source disappeared during retrieval.");
                }
                var response = await handler(request, token);
                if (request.Command == PeerCommand.Read && CorruptReads && response.Payload is { Length: > 0 } bytes)
                {
                    var corrupted = bytes.ToArray(); corrupted[^1] ^= 1;
                    return response with { Payload = corrupted };
                }
                return response;
            }, cancellationToken);
            var wrapped = new ControlledConnection(connection, this);
            Volatile.Write(ref _active, wrapped);
            return wrapped;
        }
        internal async ValueTask DisconnectAsync()
        {
            Offline = true;
            if (Volatile.Read(ref _active) is { } active) await active.DisposeAsync();
        }
        private sealed class ControlledConnection(IPeerConnection inner, ControlledFactory owner) : IPeerConnection
        {
            public Task Completion => inner.Completion;
            public async ValueTask<PeerMessage> RequestAsync(PeerMessage request, CancellationToken cancellationToken = default)
            {
                var result = await inner.RequestAsync(request, cancellationToken);
                if (request.Command == PeerCommand.Allocate && owner.HoldAllocationReply)
                {
                    owner.Allocated.TrySetResult(result.Record!);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                return result;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class FailAfterJournal(IPeerJournal inner) : IPeerJournal
    {
        public ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default) => inner.InitializeAsync(scope, cancellationToken);
        public ValueTask<IReadOnlyList<PeerJournalEntry>> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public async ValueTask PutAsync(PeerJournalEntry entry, CancellationToken cancellationToken = default)
        { await inner.PutAsync(entry, cancellationToken); throw new IOException("Simulated interruption after durable journal commit."); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
