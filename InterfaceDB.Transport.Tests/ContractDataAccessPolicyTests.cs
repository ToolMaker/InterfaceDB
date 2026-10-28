using System.Security.Claims;
using Idb.Authentication;
using Idb.Transport;
using Idb.Transport.Repositories;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Storage.SegmentCore;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace InterfaceDB.Transport.Tests;

public sealed partial class TcpRepositoryTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ContractPolicy_ValidatesEveryWriteOnceAndSelectsTenantWithoutReadDecoding(bool encrypted)
    {
        var config = new TenantConfig();
        IEncryptor encryptor = encrypted ? new AesGcmEncryptor(new SerializerKey()) : new EncryptionNone();
        var binary = new BinaryObjectSerializer<ITenantRecord>(runtimeTypeAllowlist: new(typeof(TenantRecord)));
        IObjectSerializer<ITenantRecord> serializer = encrypted ? new EncryptingObjectSerializer<ITenantRecord>(binary, encryptor) : binary;
        var reader = new CountingTenantReader(new AccessPolicyEnvelopeReader<ITenantRecord>(serializer, config));
        var collection = new ServiceCollection();
        collection.AddKeyedSingleton<IObjectSerializer<ITenantRecord>>("tenants", serializer);
        collection.AddKeyedSingleton<IAccessPolicyEnvelopeReader<ITenantRecord>>("tenants", reader);
        collection.AddContractDataAccessPolicy(config);
        using var services = collection.BuildServiceProvider();
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_root,
            keyIndexFactory: services.GetRequiredService<IPartitionKeyIndexFactory>());
        var manifest = Manifest() with { ContractIdentity = "TenantRecord", ContractSchemaHash = "tenant-v1", IndexDefinitionHash = "tenant-auto-exact-v1" };
        await using var host = await TcpStoreHost.StartAsync(HostOptions(store) with
        { Stores = [new("people", manifest, store) { AccessPolicy = services.GetRequiredKeyedService<IDataAccessPolicy>("tenants") }] });
        TcpRepositoryOptions<ITenantRecord> OptionsFor(string actor, string tenant) => new()
        {
            Contract = config, RuntimeTypes = new(typeof(TenantRecord)), Serializer = serializer, Encryptor = encryptor,
            Connection = Options(host, Token(actor, additionalClaims: new() { ["tenant_id"] = tenant })).Connection with { Manifest = manifest }
        };
        await using var a = new TcpRepository<ITenantRecord>(OptionsFor("a", "tenant-a"));
        await using var shared = new TcpRepository<ITenantRecord>(OptionsFor("other-member", "tenant-a"));
        await using var b = new TcpRepository<ITenantRecord>(OptionsFor("b", "tenant-b"));
        var saved = await a.SaveAsync(new TenantRecord { TenantId = "tenant-a", Text = "private" });
        Assert.AreEqual(1, reader.Reads, "The server decodes each record once for write authorization.");
        Assert.AreEqual("private", (await shared.QuerySingleAsync(_ => true)).Text);
        Assert.IsNull(await b.QueryFirstAsync(_ => true));
        Assert.AreEqual(1, reader.Reads, "Selection must not re-decode stored rows on the server.");
        var before = (await store.EnumerateAsync(20)).Records!.ToDictionary(r => r.Key, r => r.Payload);
        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await a.SaveManyAsync([
            new TenantRecord { TenantId = "tenant-a", Text = "must-not-commit" },
            new TenantRecord { TenantId = "tenant-b", Text = "forged-owner" }]));
        Assert.AreEqual(RemoteErrorCode.Unauthorized, rejected.Code);
        var after = (await store.EnumerateAsync(20)).Records!;
        Assert.AreEqual(before.Count, after.Length);
        foreach (var record in after) CollectionAssert.AreEqual(before[record.Key], record.Payload, "Rejected batches cannot advance data or clock.");
        saved.TenantId = "tenant-b";
        Assert.AreEqual(RemoteErrorCode.Unauthorized, (await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await a.SaveAsync(saved))).Code);
        Assert.AreEqual("tenant-a", (await shared.QuerySingleAsync(_ => true)).TenantId);
        Assert.IsTrue(store.PartitionIndexStatistics.Hits > 0);
        Assert.IsTrue(await shared.DeleteAsync(await shared.QuerySingleAsync(_ => true)));
        Assert.IsNull(await a.QueryFirstAsync(_ => true));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ContractPolicy_CriteriaAutomaticallyIndexInLocalFileAndSegmentRepositories(bool segment)
    {
        var config = new TenantConfig(_root, segment ? ContractRepositoryType.SegmentCore : ContractRepositoryType.FileSystem);
        var collection = new ServiceCollection();
        if (segment) collection.AddSegmentCoreRepository(config); else collection.AddFileRepository(config);
        await using var services = collection.BuildServiceProvider();
        var repository = services.GetRequiredKeyedService<IRepository<ITenantRecord>>("tenants");
        await repository.InitializeAsync();
        await repository.SaveAsync(new TenantRecord { TenantId = "a", Text = "one" });
        await repository.SaveAsync(new TenantRecord { TenantId = "b", Text = "two" });
        var found = await repository.SearchFirstAsync(r => r.TenantId == "a");
        var all = new List<string>();
        await foreach (var item in repository.QueryManyAsync(_ => true)) all.Add(item.TenantId + ":" + item.Text);
        Assert.IsNotNull(found, string.Join(",", all));
        Assert.AreEqual("one", found.Text);
        Assert.IsTrue(services.GetRequiredKeyedService<IIndexManager<ITenantRecord>>("tenants").ExplainSearch(r => r.TenantId == "a").UsesIndexes);
        // No authentication/policy is required by these local repositories.
        Assert.AreEqual("two", (await repository.SearchFirstAsync(r => r.TenantId == "b"))!.Text);
    }

    [TestMethod]
    public void ContractPolicy_CompositeCriteriaSelectExactClaimMembershipAndRejectAmbiguity()
    {
        var policy = new ContractDataAccessPolicy<ICompositeOwner>(new CompositeReader());
        var identity = new AuthenticatedIdentity(new ClaimsPrincipal(new ClaimsIdentity([
            new("iss", Issuer), new("sub", "member"), new("tenant_id", "a"),
            new("departments", "[\"d1\",\"d2\"]", "JSON_ARRAY")], "jwt")), DateTimeOffset.UtcNow.AddMinutes(5), "test");
        var context = new DataAccessContext(identity, "people", "{\"Department\":\"d1\"}", EncodedStoreCapabilities.Read);
        var selected = policy.Authorize(context);
        Assert.IsNotNull(selected);
        Assert.IsNull(policy.Authorize(context with { RequestedScope = null }));
        Assert.IsNull(policy.Authorize(context with { RequestedScope = "{\"TenantId\":\"other\",\"Department\":\"d1\"}" }));
        Assert.IsNull(policy.Authorize(context with { RequestedScope = "{\"Department\":\"d1\",\"Department\":\"d2\"}" }));
        Assert.AreNotEqual(selected, policy.Authorize(context with { RequestedScope = "{\"Department\":\"d2\"}" }));
        policy.ValidateWrite(context, new() { Command = EncodedStoreCommand.Put, Key = "key", Payload = [1] });
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => policy.ValidateWrite(context, new() { Command = EncodedStoreCommand.Put, Key = "key", Payload = [2] }));
    }

    [TestMethod]
    public void ContractPolicy_RejectsAmbiguousOrUnindexableDeclarations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ContractDataAccessPolicy<IShadowOwner>(new UnusedReader<IShadowOwner>()));
        Assert.ThrowsExactly<ArgumentException>(() => new ContractDataAccessPolicy<INumericOwner>(new UnusedReader<INumericOwner>()));
        Assert.ThrowsExactly<ArgumentException>(() => new ContractDataAccessPolicy<IUnindexedOwner>(new UnusedReader<IUnindexedOwner>()));
        Assert.ThrowsExactly<ArgumentException>(() => new ContractDataAccessPolicy<IEncryptedOwner>(new UnusedReader<IEncryptedOwner>()));
    }

    [TestMethod]
    public void ContractPolicy_KeyedRegistrationsKeepSelectionsIndependent()
    {
        var collection = new ServiceCollection();
        collection.AddKeyedSingleton<IObjectSerializer<ITenantRecord>>("shared-serializer",
            new BinaryObjectSerializer<ITenantRecord>(runtimeTypeAllowlist: new(typeof(TenantRecord))));
        collection.AddContractDataAccessPolicy(new TenantConfig(), "first", "shared-serializer", new Dictionary<string, string> { ["TenantId"] = "a" });
        collection.AddContractDataAccessPolicy(new TenantConfig(), "second", "shared-serializer", new Dictionary<string, string> { ["TenantId"] = "b" });
        using var services = collection.BuildServiceProvider();
        var identity = new AuthenticatedIdentity(new ClaimsPrincipal(new ClaimsIdentity([
            new("iss", Issuer), new("sub", "member"), new("tenant_id", "a"), new("tenant_id", "b")], "jwt")), DateTimeOffset.UtcNow.AddMinutes(5), "test");
        var context = new DataAccessContext(identity, "people", null, EncodedStoreCapabilities.Read);
        var first = services.GetRequiredKeyedService<IDataAccessPolicy>("first");
        var second = services.GetRequiredKeyedService<IDataAccessPolicy>("second");
        Assert.IsNotNull(first.Authorize(context)); Assert.IsNotNull(second.Authorize(context));
        Assert.AreNotEqual(first.Authorize(context), second.Authorize(context));
        Assert.IsNull(first.Authorize(context with { RequestedScope = "b" }));
    }

    private interface IShadowOwner : IHasTenant { new string TenantId { get; } }
    private interface INumericOwner { [StorageAccessClaim("tenant_id")] int TenantId { get; } }
    private interface IUnindexedOwner { [StorageAccessClaim("tenant_id"), NotSearchIndexed] string TenantId { get; } }
    private interface IEncryptedOwner { [StorageAccessClaim("tenant_id"), EncryptedStorageField] string TenantId { get; } }
    private sealed class UnusedReader<T> : IAccessPolicyEnvelopeReader<T> where T : class
    { public T Read(string alias, string key, byte[] payload) => throw new AssertFailedException("Invalid policy must fail at construction."); }

    private sealed class CountingTenantReader(IAccessPolicyEnvelopeReader<ITenantRecord> inner) : IAccessPolicyEnvelopeReader<ITenantRecord>
    {
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public ITenantRecord Read(string alias, string key, byte[] payload)
        { Interlocked.Increment(ref _reads); return inner.Read(alias, key, payload); }
    }
    private sealed class CompositeReader : IAccessPolicyEnvelopeReader<ICompositeOwner>
    {
        public ICompositeOwner Read(string alias, string key, byte[] payload) => new CompositeOwner { TenantId = "a", Department = payload[0] == 1 ? "d1" : "d2" };
    }
    private interface IHasTenant { [StorageAccessClaim("tenant_id")] string TenantId { get; } }
    private interface ICompositeOwner : IHasTenant { [StorageAccessClaim("departments")] string Department { get; } }
    private sealed class CompositeOwner : ICompositeOwner { public string TenantId { get; init; } = ""; public string Department { get; init; } = ""; }
}

[StorageScope(VersionTag = "1.0.0")]
public interface ITenantRecord : IStoredContract<ITenantRecord>
{
    [StorageAccessClaim("tenant_id")] string TenantId { get; set; }
    string Text { get; set; }
}
[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed class TenantRecord : ITenantRecord
{
    public string TenantId { get; set; } = "";
    public string Text { get; set; } = "";
    [IgnoreMember] public IObjectEnvelope<ITenantRecord>? Envelope { get; private set; }
}
public sealed class TenantConfig(string? root = null, ContractRepositoryType repository = ContractRepositoryType.Remote) : IDbContractConfigDto<ITenantRecord>
{
    public string EnvelopeDICacheKey => "tenants";
    public string ContractName => "TenantRecord";
    public string FileExtension => ".mpk";
    public string? ImplementationsPath => null;
    public string? ContractPath => null;
    public string? MigrationsPath => null;
    public string? DataRoot => root;
    public string? ActiveVersion => "1.0.0";
    public ContractRepositoryType RepositoryType => repository;
    public StoragePolicy? StoragePolicy => null;
}
