using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using Microsoft.IdentityModel.Tokens;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed partial class TcpRepositoryTests
{
    private const string Issuer = "https://identity.example.test/realms/repository";
    private TestCertificates _certificates = null!;
    private RSA _key = null!;
    private JwtAccessTokenValidator _validator = null!;
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "InterfaceDB.TypedTcp.Tests", Guid.NewGuid().ToString("N"));
        _certificates = new();
        _key = RSA.Create(2048);
        _validator = new(new()
        {
            Authorities = [new()
            {
                Id = "test", Issuer = Issuer, Audiences = ["interfacedb-storage"],
                ClockSkew = TimeSpan.Zero,
                SigningKeys = [new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "test-key" }]
            }]
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _validator.Dispose(); _key.Dispose(); _certificates.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task IndependentClientsRefreshIndexesAndRejectStaleWrites()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var first = Client(host, "parent-a");
        await using var second = Client(host, "parent-a");
        var saved = await first.SaveAsync(new RemotePerson { Name = "old", Score = 1 });
        var stale = await second.QuerySingleAsync(_ => true);
        saved.Name = "new"; saved.Score = 2;
        await first.SaveAsync(saved);
        Assert.IsNull(await second.SearchFirstAsync(person => person.Name == "old"));
        Assert.AreEqual(2, (await second.SearchFirstAsync(person => person.Name == "new"))!.Score);
        stale.Score = 99;
        var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(
            async () => await second.SaveAsync(stale));
        Assert.AreEqual(1, conflict.ExpectedRevision < conflict.CurrentRevision ? 1 : 0);
        Assert.AreEqual(2, (await first.QuerySingleAsync(_ => true)).Score);
        Assert.IsTrue(await first.DeleteAsync(await first.QuerySingleAsync(_ => true)));
        Assert.IsNull(await second.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    public async Task ConcurrentClientsSerializeOperationsAndOnlyOneSameRevisionUpdateCommits()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var first = Client(host, "parent-a");
        await using var second = Client(host, "parent-a");
        await first.SaveAsync(new RemotePerson { Name = "shared" });
        var a = await first.QuerySingleAsync(_ => true);
        var b = await second.QuerySingleAsync(_ => true);
        a.Score = 1; b.Score = 2;
        async Task<bool> TrySave(TcpRepository<IRemotePerson> repository, IRemotePerson entity)
        {
            try { await repository.SaveAsync(entity); return true; }
            catch (WriteConflictException<IRemotePerson>) { return false; }
        }
        var results = await Task.WhenAll(TrySave(first, a), TrySave(second, b));
        Assert.AreEqual(1, results.Count(result => result));
        Assert.IsTrue((await first.QuerySingleAsync(_ => true)).Score is 1 or 2);
    }

    [TestMethod]
    public async Task DifferentSubjectsCannotReadEnumerateOverwriteOrDeleteEachOthersKeys()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var a = Client(host, "parent-a");
        await using var b = Client(host, "parent-b");
        var owned = await a.SaveAsync(new RemotePerson { Name = "private-a" });
        Assert.IsNull(await b.QueryFirstByStorageKeyAsync(owned.Envelope!.EnvelopeCacheKey));
        Assert.IsNull(await b.QueryFirstAsync(_ => true));
        // A known key and even a copied envelope cannot select another principal's partition.
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await b.SaveAsync(owned));
        await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await b.DeleteAsync(owned));
        await b.SaveAsync(new RemotePerson { Name = "private-b" });
        Assert.AreEqual("private-a", (await a.QuerySingleAsync(_ => true)).Name);
        Assert.AreEqual("private-b", (await b.QuerySingleAsync(_ => true)).Name);
    }

    [TestMethod]
    public async Task RestartPreservesPayloadAndRevisionClock()
    {
        long revision;
        await using (var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root))
        await using (var host = await Host(store))
        await using (var repository = Client(host, "parent-a"))
        {
            var saved = await repository.SaveAsync(new RemotePerson { Name = "persisted" });
            revision = saved.Envelope!.RevisionTimestampUtcMicroseconds;
        }
        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var restarted = await Host(reopened);
        await using var client = Client(restarted, "parent-a");
        var restored = await client.QuerySingleAsync(_ => true);
        Assert.AreEqual("persisted", restored.Name);
        Assert.AreEqual(revision, restored.Envelope!.RevisionTimestampUtcMicroseconds);
        await client.DeleteAsync(restored);
        var replacement = await client.SaveAsync(new RemotePerson { Name = "next" });
        Assert.IsTrue(replacement.Envelope!.RevisionTimestampUtcMicroseconds > revision);
    }

    [TestMethod]
    public async Task CommittedButUnacknowledgedBatchIsNotReplayedAndCanBeReconciled()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        var dropped = new DropAcknowledgement(store);
        await using var host = await Host(dropped);
        await using var repository = Client(host, "parent-a");
        var input = new RemotePerson { Name = "committed-once" };
        var failure = await Assert.ThrowsExactlyAsync<RemoteRepositoryCommitOutcomeUnknownException>(
            async () => await repository.SaveAsync(input));
        Assert.AreEqual(1, dropped.Mutations);
        Assert.IsNull(input.Envelope, "An uncertain write must not publish a successful caller revision.");
        var restored = await repository.QuerySingleAsync(_ => true);
        Assert.AreEqual("committed-once", restored.Name);
        Assert.AreEqual(restored.Envelope!.RevisionTimestampUtcMicroseconds,
            failure.AttemptedRevisions[restored.Envelope.EnvelopeCacheKey]);
        Assert.AreEqual(1, dropped.Mutations, "Reading to reconcile must not replay the mutation.");
    }

    [TestMethod]
    [DataRow("audience")]
    [DataRow("missing-audience")]
    [DataRow("scope")]
    [DataRow("missing-scope")]
    [DataRow("expired")]
    public async Task InvalidTokenIsRejectedBeforeRepositoryAccess(string defect)
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var rejected = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a", defect)));
        var error = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await rejected.InitializeAsync());
        Assert.AreEqual(RemoteErrorCode.Unauthorized, error.Code);
        Assert.AreEqual(0, (await store.EnumerateAsync(20)).Records!.Length);
    }

    [TestMethod]
    public async Task TypedRepositoryUsesUnrestrictedDataWhenNoPolicyIsConfigured()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with { DefaultAccessPolicy = null });
        await using var repository = Client(host, "parent-a");
        await repository.InitializeAsync();
        await Seed(repository, "unrestricted", 1);
        await using var second = Client(host, "parent-b");
        Assert.AreEqual(1, (await second.QuerySingleAsync(_ => true)).Score);
    }

    [TestMethod]
    public async Task BatchValidationRejectsDuplicateEntityWithoutPartialCommit()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var repository = Client(host, "parent-a");
        var saved = await repository.SaveAsync(new RemotePerson { Name = "original" });
        saved.Name = "candidate";
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await repository.SaveManyAsync([saved, saved]));
        Assert.AreEqual("original", (await repository.QuerySingleAsync(_ => true)).Name);
    }

    [TestMethod]
    public async Task ConflictReadCurrentReopensAfterSubsequentWriteAndDelete()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var a = Client(host, "parent-a");
        await using var b = Client(host, "parent-a");
        var original = await a.SaveAsync(new RemotePerson { Name = "original" });
        var stale = await b.QuerySingleAsync(_ => true);
        original.Score = 1;
        original = await a.SaveAsync(original);
        var conflict = await Assert.ThrowsExactlyAsync<WriteConflictException<IRemotePerson>>(async () => await b.SaveAsync(stale));
        original.Score = 2;
        original = await a.SaveAsync(original);
        Assert.AreEqual(2, (await conflict.ReadCurrentAsync())!.Score);
        await a.DeleteAsync(original);
        Assert.IsNull(await conflict.ReadCurrentAsync());
    }

    [TestMethod]
    public async Task WritesCannotGrowPastSnapshotBoundsOrPartiallyCommit()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        await using var repository = new TcpRepository<IRemotePerson>(Options(host, Token("parent-a"))
            with { MaximumSnapshotRecords = 1, MaximumSnapshotBytes = 4096 });
        var first = await repository.SaveAsync(new RemotePerson { Name = "original" });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await repository.SaveAsync(new RemotePerson { Name = "overflow" }));
        first.Name = new string('x', 8192);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await repository.SaveAsync(first));
        Assert.AreEqual("original", (await repository.QuerySingleAsync(_ => true)).Name);
        await repository.DeleteAsync(first);
        await repository.SaveAsync(new RemotePerson { Name = "replacement" });
        Assert.AreEqual("replacement", (await repository.QuerySingleAsync(_ => true)).Name);
    }

    [TestMethod]
    public async Task NegotiatedSmallPagesAndBatchesRemainReadableAndIsolated()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
            { Limits = new() { MaximumPageItems = 1, MaximumBatchItems = 3 } });
        await using var a = Client(host, "parent-a");
        await using var b = Client(host, "parent-b");
        await a.SaveManyAsync([new RemotePerson { Name = "a1" }, new RemotePerson { Name = "a2" }]);
        await b.SaveManyAsync([new RemotePerson { Name = "b1" }, new RemotePerson { Name = "b2" }]);
        var names = new List<string>();
        await foreach (var item in a.QueryManyAsync(_ => true)) names.Add(item.Name);
        CollectionAssert.AreEquivalent(new[] { "a1", "a2" }, names);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await a.SaveManyAsync([new RemotePerson(), new RemotePerson(), new RemotePerson()]));
        Assert.AreEqual("b1", (await b.SearchFirstAsync(p => p.Name == "b1"))!.Name);
    }

    [TestMethod]
    public async Task RefreshedUserTokenIsAcceptedButAccountSwitchRequiresNewRepository()
    {
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root);
        await using var host = await Host(store);
        var provider = new SwitchingTokenProvider(Token("parent-a"));
        var options = Options(host, provider.Token);
        await using var repository = new TcpRepository<IRemotePerson>(options with
            { Connection = options.Connection with { TokenProvider = provider } });
        await repository.SaveAsync(new RemotePerson { Name = "a" });
        provider.Token = Token("parent-a");
        Assert.AreEqual("a", (await repository.QuerySingleAsync(_ => true)).Name);
        provider.Token = Token("parent-b");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await repository.InitializeAsync());
        await using var b = Client(host, "parent-b");
        Assert.IsNull(await b.QueryFirstAsync(_ => true));
    }

    private sealed class SwitchingTokenProvider(string token) : IAccessTokenProvider
    {
        internal string Token { get; set; } = token;
        public ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AccessToken(Token));
    }

    private ValueTask<TcpStoreHost> Host(IEncodedEnvelopeStore store) => TcpStoreHost.StartAsync(HostOptions(store));
    private TcpStoreHostOptions HostOptions(IEncodedEnvelopeStore store) => new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
        Stores = [new("people", Manifest(), store)], AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
        TokenValidator = _validator, DefaultAccessPolicy = new SubjectDataAccessPolicy(),
        AuthorizeIdentity = (identity, alias, _) => alias == "people"
            && identity.Principal.FindAll("scope").SelectMany(claim => claim.Value.Split(' ')).Contains("interfacedb.storage"),
        HandshakeTimeout = TimeSpan.FromSeconds(5), MaximumOperationTimeout = TimeSpan.FromSeconds(5)
    };
    private TcpRepository<IRemotePerson> Client(TcpStoreHost host, string subject) => new(Options(host, Token(subject)));
    private TcpRepositoryOptions<IRemotePerson> Options(TcpStoreHost host, string token) => new()
    {
        Contract = new PersonConfig(), RuntimeTypes = new(typeof(RemotePerson)),
        Connection = new()
        {
            Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "people",
            Manifest = Manifest(), AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
            TokenProvider = new FixedAccessTokenProvider(new AccessToken(token)), ServerCertificateValidation = _certificates.ValidateServer,
            ConnectTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5)
        },
        AdmissionTimeout = TimeSpan.FromSeconds(10), BusyRetryDelay = TimeSpan.FromMilliseconds(10)
    };
    internal static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "InterfaceDB-remote-v1", StorageEngine = "SegmentCore", StorageFormat = "test",
        EnvelopeFormat = TcpRepositoryOptions<IRemotePerson>.EnvelopeFormat, ContractIdentity = "RemotePerson",
        ContractVersion = "1.0.0", ContractSchemaHash = "person-v1", ImplementationManifestHash = "person-types-v1",
        IndexDefinitionHash = "name-score-v1"
    };
    private string Token(string subject, string? defect = null, Dictionary<string, object>? additionalClaims = null)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer, ["sub"] = subject, ["aud"] = defect == "audience" ? "wrong" : "interfacedb-storage",
            ["scope"] = defect == "scope" ? "wrong" : "interfacedb.storage",
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = (defect == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5)).ToUnixTimeSeconds()
        };
        if (additionalClaims is not null) foreach (var claim in additionalClaims) claims[claim.Key] = claim.Value;
        if (defect == "missing-audience") claims.Remove("aud");
        if (defect == "missing-scope") claims.Remove("scope");
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt", kid = "test-key" }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return header + "." + payload + "." + Encode(_key.SignData(Encoding.ASCII.GetBytes(header + "." + payload),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    internal sealed class PersonConfig(string? dataRoot = null) : IDbContractConfigDto<IRemotePerson>
    {
        public string EnvelopeDICacheKey => "people";
        public string ContractName => "RemotePerson";
        public string FileExtension => ".mpk";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => dataRoot;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => ContractRepositoryType.Remote;
        public StoragePolicy? StoragePolicy => null;
    }
    private sealed class DropAcknowledgement(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        public int Mutations { get; private set; }
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            var response = await inner.ExecuteAsync(request, cancellationToken);
            if (request.IsMutation && ++Mutations == 1) throw new IOException("Injected lost acknowledgement after durable commit.");
            return response;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IRemotePerson : IStoredContract<IRemotePerson>
{
    [SearchIndex(SearchIndexMode.Exact)]
    string Name { get; set; }
    [SearchIndex]
    int Score { get; set; }
}
[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class RemotePerson : IRemotePerson
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
    [IgnoreMember]
    public IObjectEnvelope<IRemotePerson>? Envelope { get; private set; }
}
