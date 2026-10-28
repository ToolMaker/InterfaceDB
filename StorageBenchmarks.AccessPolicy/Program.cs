using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using InterfaceDB.Transport.Tests;
using MessagePack;
using Microsoft.IdentityModel.Tokens;

if (args.Contains("--mixed-paging"))
{
    await MixedPagingBenchmark.RunAsync(args);
    return;
}

var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".tmp/access-policy-production/benchmark");
Directory.CreateDirectory(output);
bool smoke = args.Contains("--smoke");
using var certificates = new TestCertificates();
using var signingKey = RSA.Create(2048);
using var validator = new JwtAccessTokenValidator(new()
{
    Authorities = [new() { Id = "benchmark", Issuer = "https://benchmark.invalid", Audiences = ["storage"],
        SigningKeys = [new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = "benchmark" }] }]
});
var serializer = new BinaryObjectSerializer<IBenchRecord>(runtimeTypeAllowlist: new(typeof(BenchRecord)));
var codec = new EncodedEnvelopeSerializer<IBenchRecord>(serializer);
var policy = new ContractDataAccessPolicy<IBenchRecord>(new AccessPolicyEnvelopeReader<IBenchRecord>(serializer, new BenchConfig()));
var limits = new TransportLimits { MaximumBatchItems = 512, MaximumPageItems = 1024 };
var manifest = new CompatibilityManifest { ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0",
    EnvelopeFormat = "canonical", ContractIdentity = "BenchRecord", ContractVersion = "1.0.0",
    ContractSchemaHash = "tenant-text-v1", ImplementationManifestHash = "bench-v1", IndexDefinitionHash = "tenant-exact-v1" };
var scenarios = smoke ? new[] { new Scenario("smoke", 200, 2, true) } : new[] {
    new Scenario("unrestricted-10k", 10_000, 1, false), new Scenario("criteria-10k-one", 10_000, 1, true),
    new Scenario("criteria-10k-100", 10_000, 100, true), new Scenario("criteria-100k-100", 100_000, 100, true) };
var reports = new List<object>();
byte[][] expected = [];
bool complete = false;
foreach (var scenario in scenarios)
{
    string root = Path.Combine(output, "data-" + scenario.Name + "-" + Guid.NewGuid().ToString("N"));
    int tenantRows = scenario.Rows / scenario.Tenants;
    expected = Enumerable.Range(0, tenantRows).Select(n => Payload(0, n)).ToArray();
    var tokens = Enumerable.Range(0, scenario.Tenants).Select(Token).ToArray();
    Console.WriteLine($"Seeding {scenario.Name}: {scenario.Rows} records through TLS/JWT TCP.");
    await using (var store = await Open(root, scenario, PartitionIndexMode.Disabled))
    await using (var host = await Host(store, scenario))
    {
        for (int tenant = 0; tenant < scenario.Tenants; tenant++)
        {
            await using var client = await Connect(host, tokens[tenant]);
            for (int offset = 0; offset < tenantRows; offset += limits.MaximumBatchItems)
                await client.ApplyBatchAsync(Enumerable.Range(offset, Math.Min(limits.MaximumBatchItems, tenantRows - offset))
                    .Select(n => new EncodedMutation(Key(n), Payload(tenant, n))).ToArray());
            await CheckAll(client, tenant, tenantRows);
        }
    }
    for (int round = 0; round < (smoke ? 1 : 3); round++)
    foreach (var mode in round % 2 == 0 ? new[] { PartitionIndexMode.Disabled, PartitionIndexMode.Lazy }
                                      : new[] { PartitionIndexMode.Lazy, PartitionIndexMode.Disabled })
    {
        var started = Stopwatch.GetTimestamp();
        await using var store = await Open(root, scenario, mode);
        double openMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await using var host = await Host(store, scenario);
        started = Stopwatch.GetTimestamp();
        await using var client = await Connect(host, tokens[0]);
        double connectMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var cold = await client.EnumerateAsync(128);
        double coldPageMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        CheckPage(cold, 0, tenantRows);
        for (int n = 0; n < 10; n++) { await client.GetAsync(Key(0)); await client.EnumerateAsync(128); }
        var get = await Measure(async () =>
        {
            var payload = await client.GetAsync(Key(0));
            if (payload is null || !payload.AsSpan().SequenceEqual(expected[0])) throw new InvalidDataException("Get mismatch.");
        }, smoke ? 3 : 100);
        var page = await Measure(async () => CheckPage(await client.EnumerateAsync(128), 0, tenantRows), smoke ? 3 : 30);
        var full = await Measure(async () =>
        {
            string? after = null; int count = 0;
            do
            {
                var result = await client.EnumerateAsync(128, after);
                CheckPage(result, count, tenantRows); count += result.Records!.Length; after = result.NextAfterKey;
            } while (after is not null);
            if (count != tenantRows) throw new InvalidDataException("Enumeration count mismatch.");
        }, smoke ? 2 : 5);
        var update = await Measure(async () => await client.PutAsync(Key(0), expected[0]), smoke ? 3 : 64);
        var mutations = Enumerable.Range(0, Math.Min(128, tenantRows)).Select(n => new EncodedMutation(Key(n), expected[n])).ToArray();
        var batch = await Measure(async () => { await client.ApplyBatchAsync(mutations); }, smoke ? 2 : 10);
        await CheckAll(client, 0, tenantRows);
        reports.Add(new { scenario.Name, scenario.Rows, scenario.Tenants, scenario.Criteria, Round = round, Mode = mode.ToString(),
            OpenMs = openMs, ConnectTlsJwtMs = connectMs, ColdPageMs = coldPageMs, Get = get, Page128 = page, FullTenant = full,
            DurableUpdate = update, DurableBatch = batch, BatchItems = mutations.Length, Index = store.PartitionIndexStatistics });
        await Save();
        Console.WriteLine($"{scenario.Name} {mode} round {round}: page {page.MedianMs:F3} ms, full {full.MedianMs:F3} ms, update {update.MedianMs:F3} ms");
    }
    // Independent final reopen verifies every tenant and every decoded field, not just the timed sample.
    await using (var store = await Open(root, scenario, PartitionIndexMode.Lazy))
    await using (var host = await Host(store, scenario))
        for (int tenant = 0; tenant < scenario.Tenants; tenant++)
        {
            await using var client = await Connect(host, tokens[tenant]);
            await CheckAll(client, tenant, tenantRows);
        }
    // Keep data for inspection. A fresh run always uses a new directory.
}
complete = true;
await Save();
Console.WriteLine("All full-object, partition, ordering and reopen checks passed.");

ValueTask<SegmentCoreEncodedEnvelopeStore> Open(string path, Scenario scenario, PartitionIndexMode mode) =>
    SegmentCoreEncodedEnvelopeStore.OpenAsync(path, limits, maximumEnumerationKeys: scenario.Rows + 1024,
        maximumPartitionKeys: scenario.Rows / scenario.Tenants + 1024, keyIndexFactory: new BoundedPartitionKeyIndexFactory(new() { Mode = mode }));
ValueTask<TcpStoreHost> Host(IEncodedEnvelopeStore store, Scenario scenario) => TcpStoreHost.StartAsync(new()
{
    ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = certificates.Server, Limits = limits,
    Stores = [new("bench", manifest, store) { AccessPolicy = scenario.Criteria ? policy : UnrestrictedDataAccessPolicy.Instance }],
    AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenValidator = validator,
    AuthorizeIdentity = (identity, alias, _) => alias == "bench" && identity.Principal.HasClaim("scope", "storage"),
    MaximumOperationTimeout = TimeSpan.FromMinutes(2)
});
ValueTask<TcpEncodedEnvelopeStore> Connect(TcpStoreHost host, string token) => TcpEncodedEnvelopeStore.ConnectAsync(new()
{
    Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "bench", Manifest = manifest,
    Limits = limits, AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
    TokenProvider = new FixedAccessTokenProvider(new AccessToken(token)), ServerCertificateValidation = certificates.ValidateServer,
    OperationTimeout = TimeSpan.FromMinutes(2)
});
byte[] Payload(int tenant, int number) => codec.Serialize(Key(number), new FileObjectEnvelope<IBenchRecord>
{
    EnvelopeCacheKey = Key(number), VersionTag = "1.0.0", RevisionTimestampUtcMicroseconds = 1,
    ObjectInstance = new BenchRecord { TenantId = "tenant-" + tenant, Number = number, Text = new string('x', 256) }
}, []);
static string Key(int number) => "r-" + number.ToString("D9");
void CheckPage(EncodedStoreResponse page, int offset, int count)
{
    if (page.Records is null || page.Records.Length != Math.Min(128, count - offset)) throw new InvalidDataException("Page count mismatch.");
    for (int n = 0; n < page.Records.Length; n++)
        if (page.Records[n].Key != Key(offset + n) || !page.Records[n].Payload.AsSpan().SequenceEqual(expected[offset + n]))
            throw new InvalidDataException("Page content mismatch.");
}
async Task CheckAll(IEncodedEnvelopeStore store, int tenant, int expectedCount)
{
    string? after = null; int seen = 0;
    do
    {
        var page = await store.EnumerateAsync(1024, after);
        foreach (var record in page.Records!)
        {
            if (record.Key != Key(seen) || !record.Payload.AsSpan().SequenceEqual(Payload(tenant, seen))) throw new InvalidDataException("Full byte check failed.");
            var item = codec.Deserialize(record.Key, record.Payload, []).ObjectInstance!;
            if (item.TenantId != "tenant-" + tenant || item.Number != seen || item.Text != new string('x', 256)) throw new InvalidDataException("Full object check failed.");
            seen++;
        }
        after = page.NextAfterKey;
    } while (after is not null);
    if (seen != expectedCount) throw new InvalidDataException("Full count check failed.");
}
string Token(int tenant)
{
    static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt", kid = "benchmark" }));
    var body = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> {
        ["iss"] = "https://benchmark.invalid", ["sub"] = "member-" + tenant, ["aud"] = "storage", ["scope"] = "storage",
        ["tenant_id"] = "tenant-" + tenant, ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() }));
    return header + "." + body + "." + Encode(signingKey.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
}
async Task<Measurement> Measure(Func<Task> operation, int count)
{
    var samples = new double[count]; long before = GC.GetTotalAllocatedBytes(true);
    for (int n = 0; n < count; n++) { var start = Stopwatch.GetTimestamp(); await operation(); samples[n] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    long bytes = GC.GetTotalAllocatedBytes(true) - before; Array.Sort(samples);
    return new(samples[count / 2], samples[(int)Math.Floor((count - 1) * .95)], (double)bytes / count);
}
Task Save() => File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new {
    Complete = complete, CapturedAtUtc = DateTimeOffset.UtcNow,
    Environment = new { OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription, Environment.ProcessorCount,
        TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
        CoreMvid = typeof(IStoredContract<>).Assembly.ManifestModule.ModuleVersionId, TransportMvid = typeof(TcpStoreHost).Assembly.ManifestModule.ModuleVersionId },
    Transport = "Loopback TLS + RS256 JWT; production encoded-envelope operations", Durability = "Existing WriteThrough transport default", Reports = reports
}, new JsonSerializerOptions { WriteIndented = true }));

record Scenario(string Name, int Rows, int Tenants, bool Criteria);
record Measurement(double MedianMs, double P95Ms, double AllocatedBytesPerOperation);
[StorageScope(VersionTag = "1.0.0")]
public interface IBenchRecord : IStoredContract<IBenchRecord>
{
    [StorageAccessClaim("tenant_id")] string TenantId { get; set; }
    int Number { get; set; }
    string Text { get; set; }
}
[MessagePackObject(keyAsPropertyName: true)]
public sealed class BenchRecord : IBenchRecord
{
    public string TenantId { get; set; } = "";
    public int Number { get; set; }
    public string Text { get; set; } = "";
    [IgnoreMember] public IObjectEnvelope<IBenchRecord>? Envelope { get; private set; }
}
sealed class BenchConfig : IDbContractConfigDto<IBenchRecord>
{
    public string EnvelopeDICacheKey => "bench"; public string ContractName => "BenchRecord";
    public string FileExtension => ".mpk"; public string? ActiveVersion => "1.0.0";
    public string? ImplementationsPath => null; public string? ContractPath => null;
    public string? MigrationsPath => null; public string? DataRoot => null;
    public ContractRepositoryType RepositoryType => ContractRepositoryType.Remote;
    public StoragePolicy? StoragePolicy => null;
}
