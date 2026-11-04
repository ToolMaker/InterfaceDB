using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Commercial.Repositories;
using Idb.Commercial.Repositories.Peers;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore;
using Idb.Storage.AtlasCore;
using Idb.Transport;
using Idb.Transport.Peers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed partial class PeerReplicatedRepositoryTests
{
    private const string Issuer = "https://peer.example.test/realm";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "InterfaceDB.Peer.Tests", Guid.NewGuid().ToString("N"));
    private TestCertificates _certificates = null!;
    private RSA _key = null!;
    private JwtAccessTokenValidator _validator = null!;
    private static readonly PeerLimits Limits = new() { RequestTimeout = TimeSpan.FromSeconds(4), PageSize = 8, MaximumRecords = 1000 };
    [TestInitialize]
    public void Setup()
    {
        _certificates = new(); _key = RSA.Create(2048);
        _validator = new(new() { Authorities = [new() { Id = "peer", Issuer = Issuer, Audiences = ["peer-storage"],
            ClockSkew = TimeSpan.Zero, SigningKeys = [new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "key" }] }] });
    }
    [TestCleanup]
    public void Cleanup()
    {
        _validator.Dispose(); _key.Dispose(); _certificates.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    [DataRow("file", false)] [DataRow("segment", false)] [DataRow("atlas", false)]
    [DataRow("file", true)] [DataRow("segment", true)] [DataRow("atlas", true)]
    public async Task Peer_RealProvidersAndRegisteredSerializersConvergeWithoutEchoes(string engine, bool custom)
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger, custom: custom);
        await using var a = Client("a", host.LocalEndPoint.Port, engine, custom);
        await using var b = Client("b", host.LocalEndPoint.Port, engine, custom);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
        await Until(() => Task.FromResult(a.Repository.Connection.State == RepositoryConnectionState.Online && b.Repository.Connection.State == RepositoryConnectionState.Online));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = b.Repository.SubscribeCreated(_ => { received.TrySetResult(); return ValueTask.CompletedTask; });
        await subscription.Ready;
        var local = await a.Repository.SaveAsync(new PeerItem { Key = "one", Text = "hello" });
        Assert.IsNull(local.Sequence);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(async () => (await b.Repository.QueryFirstAsync(x => x.Key == "one"))?.Sequence > 0);
        var first = await a.Repository.QuerySingleAsync(x => x.Key == "one");
        await Until(async () => (await a.Repository.QuerySingleAsync(x => x.Key == "one")).Sequence > 0);
        first = await a.Repository.QuerySingleAsync(x => x.Key == "one");
        var initialSequence = first.Sequence;
        first.Text = "edited"; await a.Repository.SaveAsync(first);
        await Until(async () => (await b.Repository.QuerySingleAsync(x => x.Key == "one")).Text == "edited");
        Assert.IsTrue((await b.Repository.SearchSingleOrDefault(x => x.Sequence > initialSequence))?.Sequence > initialSequence);
        await a.Repository.DeleteAsync(await a.Repository.QuerySingleAsync(x => x.Key == "one"));
        await Until(async () => await b.Repository.QueryFirstAsync(_ => true) is null);
        var head = (await ledger.SnapshotAsync()).Single();
        Assert.AreEqual(3L, head.Sequence); Assert.IsTrue(head.Deleted);
        await a.Repository.SynchronizeAsync(); await b.Repository.SynchronizeAsync();
        Assert.AreEqual(3L, (await ledger.SnapshotAsync()).Single().Sequence);
        Assert.IsTrue(a.Serializer.Writes > 0 && b.Serializer.Reads > 0);
        // The receiving device stores the original wire bytes and serves them without re-encoding.
        var writes = b.Serializer.Writes;
        await using var c = Client("c", host.LocalEndPoint.Port, engine, custom);
        await c.Repository.InitializeAsync();
        await Until(() => Task.FromResult(c.Repository.Synchronization.ThroughSequence == 3));
        Assert.AreEqual(writes, b.Serializer.Writes);
    }

    [TestMethod]
    public async Task Peer_OfflineWritesSurviveClientAndRelayRestartAndLostAcknowledgement()
    {
        var port = FreePort();
        await using (var a = Client("a", port, "file", false))
        {
            await a.Repository.SaveAsync(new PeerItem { Key = "offline", Text = "durable" });
            Assert.IsNull((await a.Repository.QuerySingleAsync(_ => true)).Sequence);
        }
        long sequence;
        await using (var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger")))
        await using (var host = await Host(ledger, port))
        await using (var a = Client("a", port, "file", false, loseAcknowledgement: true))
        {
            await a.Repository.InitializeAsync();
            await Until(async () => (await a.Repository.QuerySingleAsync(_ => true)).Sequence > 0);
            Assert.AreEqual(1L, sequence = (await ledger.SnapshotAsync()).Single().Sequence);
        }
        await using (var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger")))
        await using (var host = await Host(ledger, port))
        await using (var a = Client("a", port, "file", false))
        await using (var b = Client("b", port, "segment", false))
        {
            await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
            await Until(async () => (await b.Repository.QueryFirstAsync(_ => true))?.Sequence == sequence);
            await a.Repository.SaveAsync(new PeerItem { Key = "next", Text = "second" });
            await Until(async () => (await b.Repository.QueryFirstAsync(x => x.Key == "next"))?.Sequence == 2);
        }
    }

    [TestMethod]
    public async Task Peer_StartupUnionsUniqueRecordsAndRepairsEveryExistingDevice()
    {
        var port = FreePort();
        await using var a = Client("a", port, "file", false);
        await using var b = Client("b", port, "segment", false);
        await using var c = Client("c", port, "atlas", false);
        await a.Repository.SaveAsync(new PeerItem { Key = "only-a", Text = "A" });
        await b.Repository.SaveAsync(new PeerItem { Key = "only-b", Text = "B" });
        await c.Repository.SaveAsync(new PeerItem { Key = "only-c", Text = "C" });
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger, port);
        await Until(async () => await Count(a.Repository) == 3 && await Count(b.Repository) == 3 && await Count(c.Repository) == 3
            && a.Repository.Synchronization.PendingRecords == 0 && b.Repository.Synchronization.PendingRecords == 0 && c.Repository.Synchronization.PendingRecords == 0);
        await using var newcomer = Client("new", port, "file", false);
        await newcomer.Repository.InitializeAsync();
        await Until(async () => await Count(newcomer.Repository) == 3);
        Assert.AreEqual(3, (await ledger.SnapshotAsync()).Count);
    }

    [TestMethod]
    public async Task Peer_NotificationCallbackCannotRepublishAndConcurrentLocalSaveStillReplicates()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        await using var a = Client("a", host.LocalEndPoint.Port, "file", false);
        await using var b = Client("b", host.LocalEndPoint.Port, "file", false);
        await a.Repository.InitializeAsync(); await b.Repository.InitializeAsync();
        var blocked = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = b.Repository.SubscribeCreated(async entity =>
        {
            try { await b.Repository.SaveAsync(entity); }
            catch (Exception exception) { blocked.TrySetResult(exception); }
        }, x => x.Key == "remote");
        await subscription.Ready;
        await Task.WhenAll(a.Repository.SaveAsync(new PeerItem { Key = "remote" }).AsTask(), b.Repository.SaveAsync(new PeerItem { Key = "local" }).AsTask());
        Assert.IsInstanceOfType<RepositoryFeedbackLoopException>(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Until(async () => await Count(a.Repository) == 2 && await Count(b.Repository) == 2);
        Assert.AreEqual(2, (await ledger.SnapshotAsync()).Count);
    }

    [TestMethod]
    public async Task Peer_RejectsUnauthorizedAccountAndManifestMismatch()
    {
        await using var ledger = new FilePeerSequenceLedger(Path.Combine(_root, "ledger"));
        await using var host = await Host(ledger);
        var options = Options(host.LocalEndPoint.Port, false);
        var wrong = new TcpPeerConnectionFactory(options, Issuer, "wrong-account", Limits);
        await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await wrong.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage())));
        var mismatch = new TcpPeerConnectionFactory(options with { Manifest = Manifest(false) with { ContractSchemaHash = "different" } }, Issuer, "reader", Limits);
        var error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await mismatch.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage())));
        Assert.AreEqual(PeerError.Incompatible, error.Error);
        var rejected = new TcpPeerConnectionFactory(options with { StoreAlias = "private" }, Issuer, "reader", Limits);
        error = await Assert.ThrowsExactlyAsync<PeerProtocolException>(async () => await rejected.ConnectAsync((_, _) => ValueTask.FromResult(new PeerMessage())));
        Assert.AreEqual(PeerError.Unauthorized, error.Error);
    }

    [TestMethod]
    public async Task Peer_AllocationRejectsConflictsAndPreservesIdempotencyAfterRestart()
    {
        var path = Path.Combine(_root, "ledger");
        var mutation = Header("record", 0);
        await using (var ledger = new FilePeerSequenceLedger(path))
        {
            await ledger.InitializeAsync("scope");
            Assert.AreEqual(1L, (await ledger.AllocateAsync(mutation, "owner")).Sequence);
            Assert.AreEqual(1L, (await ledger.AllocateAsync(mutation, "owner")).Sequence);
            await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => ledger.AllocateAsync(mutation with { PayloadHash = new string('A', 64) }, "owner").AsTask());
            await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => ledger.AllocateAsync(mutation, "other").AsTask());
            await Assert.ThrowsExactlyAsync<PeerProtocolException>(() => ledger.AllocateAsync(Header("record", 0), "owner").AsTask());
        }
        await using var reopened = new FilePeerSequenceLedger(path);
        await reopened.InitializeAsync("scope");
        Assert.AreEqual(1L, (await reopened.AllocateAsync(mutation, "owner")).Sequence);
        Assert.AreEqual(2L, (await reopened.AllocateAsync(Header("record", 1) with { Deleted = true }, "owner")).Sequence);
    }

    [TestMethod]
    public void Peer_RangePlannerDetectsEqualCountGapsAndSharesSourceLoad()
    {
        var heads = Enumerable.Range(1, 10).Select(i => Header(i.ToString(), 0) with { Sequence = i }).ToArray();
        var inventories = new Dictionary<string, IReadOnlyList<PeerRecordDescriptor>>
        { ["A"] = heads, ["B"] = heads, ["C"] = heads, ["new"] = [] };
        var plans = new PeerRangePlanner().Plan(heads, inventories);
        Assert.AreEqual(10, plans.Count);
        Assert.AreEqual(3, plans.Select(p => p.Source).Distinct().Count());
        Assert.IsTrue(plans.All(p => p.Targets.SequenceEqual(["new"])));
        inventories = new() { ["A"] = [heads[0], heads[1], heads[3], heads[4]], ["B"] = [heads[0], heads[2], heads[3], heads[4]] };
        plans = new PeerRangePlanner().Plan(heads.Take(5).ToArray(), inventories);
        Assert.AreEqual(2, plans.Count);
        CollectionAssert.AreEquivalent(new long[] { 2, 3 }, plans.Select(p => p.Record.Sequence).ToArray());
        CollectionAssert.AreEqual(new[] { new PeerRange(1, 2), new PeerRange(4, 5), new PeerRange(long.MaxValue, long.MaxValue) },
            PeerRangePlanner.Compact([1, 2, 2, 4, 5, long.MaxValue]).ToArray());
    }

    [TestMethod]
    public void Peer_SequenceDiscoverySupportsInheritedPropertiesAndRejectsAmbiguity()
    {
        Assert.AreEqual("Sequence", new AttributedSequenceAccessor<IPeerItem>().Selector.Body.ToString().Split('.').Last());
        _ = new AttributedSequenceAccessor<IInheritedSequence>();
        Assert.ThrowsExactly<ArgumentException>(() => new AttributedSequenceAccessor<IMissingSequence>());
        Assert.ThrowsExactly<ArgumentException>(() => new AttributedSequenceAccessor<IAmbiguousSequence>());
    }
    private interface IBaseSequence { [SequenceNumber] long? Order { get; set; } }
    private interface IInheritedSequence : IBaseSequence;
    private interface IMissingSequence { long? Sequence { get; set; } }
    private interface IAmbiguousSequence : IBaseSequence { [SequenceNumber] long? Second { get; set; } }

    private static PeerRecordDescriptor Header(string key, long parent) => new(key, Guid.NewGuid(), parent, 0, false, Convert.ToHexString(SHA256.HashData([1])));
    private static async Task<int> Count(PeerReplicatedRepository<IPeerItem> repository)
    { var count = 0; await foreach (var item in repository.QueryManyAsync(_ => true)) count++; return count; }
    private static async Task Until(Func<Task<bool>> test)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await test()) await Task.Delay(25, timeout.Token);
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private ValueTask<TcpPeerRelayHost> Host(IPeerSequenceLedger ledger, int port = 0, bool custom = false)
        => TcpPeerRelayHost.StartAsync(new()
        {
            ListenEndPoint = new(IPAddress.Loopback, port), ServerCertificate = _certificates.Server, TokenValidator = _validator,
            Datasets = [new("entries", Manifest(custom), ledger)], Limits = Limits,
            AuthorizeIdentity = (identity, alias, _) => alias == "entries" && identity.Principal.FindFirst("scope")?.Value == "peer",
            HandshakeTimeout = TimeSpan.FromSeconds(3)
        });
    private TcpStoreClientOptions Options(int port, bool custom) => new()
    {
        Host = "127.0.0.1", Port = port, TargetHost = "localhost", StoreAlias = "entries", Manifest = Manifest(custom),
        AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenProvider = new FixedAccessTokenProvider(new AccessToken(Token())),
        ServerCertificateValidation = _certificates.ValidateServer, ConnectTimeout = TimeSpan.FromSeconds(2)
    };
    private static CompatibilityManifest Manifest(bool custom) => new()
    {
        ApiVersion = "idb-peer-v1", StorageEngine = "peer", StorageFormat = "peer-journal-v1", EnvelopeFormat = custom ? "marker-v1" : "IDB6",
        ContractIdentity = "PeerItem", ContractVersion = "1.0.0", ContractSchemaHash = "item-sequence-v1",
        ImplementationManifestHash = "peer-item-v1", IndexDefinitionHash = "key-sequence-v1"
    };
    private ClientHandle Client(string name, int port, string engine, bool custom, bool loseAcknowledgement = false,
        bool offline = false, bool journalFailure = false, string dataset = "entries", PeerRegistrationOptions? registration = null)
    {
        var services = new ServiceCollection();
        var serializer = new CountingSerializer(custom);
        services.AddKeyedSingleton<IObjectSerializer<IPeerItem>>("local", serializer);
        var config = new Config(Path.Combine(_root, name, "data"), engine);
        var types = new StoredRuntimeTypeAllowlist<IPeerItem>(typeof(PeerItem));
        if (engine == "file") services.AddFileRepository(config, durabilityMode: StorageDurabilityMode.Durable, runtimeTypeAllowlist: types);
        else if (engine == "atlas") services.AddAtlasCoreRepository(config, runtimeTypeAllowlist: types);
        else services.AddSegmentCoreRepository(config, runtimeTypeAllowlist: types);
        IPeerConnectionFactory transport = new TcpPeerConnectionFactory(Options(port, custom) with { StoreAlias = dataset }, Issuer, "reader", Limits, registration);
        if (loseAcknowledgement) transport = new LoseAcknowledgementFactory(transport);
        var controlled = new ControlledFactory(transport) { Offline = offline };
        var journalPath = Path.Combine(_root, name, "journal");
        services.AddPeerReplicatedRepository("peer", "local", journalPath, dataset, "1.0.0", new Mapper(), controlled, Limits,
            retryDelay: TimeSpan.FromMilliseconds(50), journalFactory: journalFailure ? _ => new FailAfterJournal(new FilePeerJournal(journalPath, Limits)) : null,
            historyDate: registration?.DateProperty is null ? null : new PeerHistoryDateAccessor<IPeerItem>(x => x.CreatedAt));
        var provider = services.BuildServiceProvider();
        return new(provider, provider.GetRequiredKeyedService<PeerReplicatedRepository<IPeerItem>>("peer"), serializer, controlled);
    }
    private sealed record ClientHandle(ServiceProvider Provider, PeerReplicatedRepository<IPeerItem> Repository, CountingSerializer Serializer,
        ControlledFactory Transport) : IAsyncDisposable
    { public ValueTask DisposeAsync() => Provider.DisposeAsync(); }
    private sealed class LoseAcknowledgementFactory(IPeerConnectionFactory inner) : IPeerConnectionFactory
    {
        private int _lost;
        public string Scope => inner.Scope;
        public string? HistoryDateProperty => inner.HistoryDateProperty;
        public async ValueTask<IPeerConnection> ConnectAsync(Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> handler, CancellationToken cancellationToken = default)
            => new Drop(await inner.ConnectAsync(handler, cancellationToken), this);
        private sealed class Drop(IPeerConnection inner, LoseAcknowledgementFactory owner) : IPeerConnection
        {
            public Task Completion => inner.Completion;
            public async ValueTask<PeerMessage> RequestAsync(PeerMessage request, CancellationToken cancellationToken = default)
            {
                var reply = await inner.RequestAsync(request, cancellationToken);
                if (request.Command == PeerCommand.Allocate && Interlocked.Exchange(ref owner._lost, 1) == 0) throw new EndOfStreamException("Lost allocation acknowledgement.");
                return reply;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class Config(string root, string engine) : IDbContractConfigDto<IPeerItem>
    {
        public string EnvelopeDICacheKey => "local";
        public string ContractName => "PeerItem";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => engine == "file" ? ContractRepositoryType.FileSystem : engine == "atlas" ? ContractRepositoryType.AtlasCore : ContractRepositoryType.SegmentCore;
        public StoragePolicy? StoragePolicy => new() { DurabilityMode = StorageDurabilityMode.Durable, RepositoryNotifications = new() };
    }
    private sealed class Mapper : IResilientEntityMapper<IPeerItem>
    {
        public string GetStorageKey(IPeerItem entity) => entity.Key;
        public IPeerItem CreateDetachedCopy(IPeerItem entity) => new PeerItem { Key = entity.Key, Text = entity.Text, Sequence = entity.Sequence, CreatedAt = entity.CreatedAt };
        public void CopyValues(IPeerItem source, IPeerItem target) { target.Key = source.Key; target.Text = source.Text; target.Sequence = source.Sequence; target.CreatedAt = source.CreatedAt; }
    }
    private sealed class CountingSerializer(bool custom) : IRevisionObjectSerializer<IPeerItem>
    {
        private readonly BinaryObjectSerializer<IPeerItem> _inner = new(runtimeTypeAllowlist: new(typeof(PeerItem)));
        public int Writes;
        public int Reads;
        public byte[] Serialize(IObjectEnvelope<IPeerItem> envelope)
        { Interlocked.Increment(ref Writes); var bytes = _inner.Serialize(envelope); return custom ? [7, 9, .. bytes] : bytes; }
        public IObjectEnvelope<IPeerItem> Deserialize(byte[] data)
        { Interlocked.Increment(ref Reads); return _inner.Deserialize(Strip(data)); }
        public long ReadRevision(ReadOnlyMemory<byte> data) => _inner.ReadRevision(Strip(data.ToArray()));
        public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return ReadRevision(buffer.ToArray());
        }
        private byte[] Strip(byte[] data)
        {
            if (!custom) return data;
            if (data.Length < 2 || data[0] != 7 || data[1] != 9) throw new InvalidDataException("Wrong serializer.");
            return data[2..];
        }
    }
    private string Token()
    {
        static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt", kid = "key" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = Issuer, sub = "reader", aud = "peer-storage", scope = "peer",
            nbf = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() }));
        return header + "." + payload + "." + Encode(_key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}

[StorageScope(VersionTag = "1.0.0")]
public interface IPeerItem : IStoredContract<IPeerItem>
{
    DateTimeOffset CreatedAt { get; set; }
    [SearchIndex(SearchIndexMode.Exact)] string Key { get; set; }
    [SequenceNumber, SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)] long? Sequence { get; set; }
    string Text { get; set; }
}
public sealed class PeerItem : IPeerItem
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";
    public long? Sequence { get; set; }
    public IObjectEnvelope<IPeerItem>? Envelope { get; private set; }
}

internal static class PeerTestQueryExtensions
{
    internal static ValueTask<IPeerItem?> SearchSingleOrDefault(this PeerReplicatedRepository<IPeerItem> repository,
        System.Linq.Expressions.Expression<Func<IPeerItem, bool>> predicate) => repository.SearchFirstAsync(predicate);
}
