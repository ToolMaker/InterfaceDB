using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Idb.Authentication;
using Idb.Transport;
using InterfaceDB.Transport.Tests;
using Microsoft.IdentityModel.Tokens;

// Measures the unusual mixed shared/partitioned store without changing production defaults.
// A forwarding wrapper and an old shared-request projection run against the same live store.
internal static class MixedPagingBenchmark
{
    internal static async Task RunAsync(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? ".tmp/policy-paging/benchmark");
        Directory.CreateDirectory(output);
        using var certificates = new TestCertificates();
        using var signingKey = RSA.Create(2048);
        using var validator = new JwtAccessTokenValidator(new()
        {
            Authorities = [new() { Id = "mixed-paging", Issuer = "https://benchmark.invalid", Audiences = ["storage"],
                SigningKeys = [new RsaSecurityKey(signingKey.ExportParameters(false)) { KeyId = "benchmark" }] }]
        });
        var limits = new TransportLimits { MaximumBatchItems = 512, MaximumPageItems = 1024 };
        var manifest = new CompatibilityManifest { ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0",
            EnvelopeFormat = "opaque", ContractIdentity = "MixedPaging", ContractVersion = "1.0.0",
            ContractSchemaHash = "raw64", ImplementationManifestHash = "mixed-paging-v1", IndexDefinitionHash = "none" };
        var subject = new SubjectDataAccessPolicy();
        var policy = new DelegateDataAccessPolicy(context => context.Identity?.Principal.FindFirst("sub")?.Value == "admin"
            ? DataAccessScope.Unrestricted : subject.Authorize(context));
        string data = Path.Combine(output, "data-" + Guid.NewGuid().ToString("N"));
        await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(data, limits,
            maximumEnumerationKeys: 20_000, maximumPartitionKeys: 1024);
        var counter = new CountingStore(store);
        await using var host = await TcpStoreHost.StartAsync(new()
        {
            ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = certificates.Server, Limits = limits,
            Stores = [new("bench", manifest, counter) { AccessPolicy = policy }],
            AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer, TokenValidator = validator,
            AuthorizeIdentity = (identity, alias, _) => alias == "bench" && identity.Principal.HasClaim("scope", "storage"),
            // Diagnostic only: the benchmark must observe an expensive page instead of timing out.
            MaximumOperationTimeout = TimeSpan.FromSeconds(60)
        });
        ValueTask<TcpEncodedEnvelopeStore> Connect(string name) => TcpEncodedEnvelopeStore.ConnectAsync(new()
        {
            Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "bench", Manifest = manifest,
            Limits = limits, AuthenticationMode = TcpStoreAuthenticationMode.JwtBearer,
            TokenProvider = new FixedAccessTokenProvider(new AccessToken(Token(signingKey, name))),
            ServerCertificateValidation = certificates.ValidateServer, OperationTimeout = TimeSpan.FromSeconds(60)
        });
        await using var admin = await Connect("admin");
        await admin.ApplyBatchAsync(Enumerable.Range(0, 10)
            .Select(n => new EncodedMutation(SharedKey(n), Payload("shared", n))).ToArray());
        var stages = new List<object>();
        int seeded = 0;
        foreach (int target in new[] { 0, 2000, 10_000 })
        {
            Console.WriteLine($"Seeding mixed paging: {target} tenant records + 10 shared records.");
            for (int tenant = seeded / 500; tenant < target / 500; tenant++)
            {
                string name = TenantName(tenant);
                await using var client = await Connect(name);
                await client.ApplyBatchAsync(Enumerable.Range(0, 500)
                    .Select(n => new EncodedMutation(TenantKey(n), Payload(name, n))).ToArray());
            }
            seeded = target;
            var pages = new List<object>();
            foreach (int pageSize in new[] { 50, 64 })
            {
                for (int warmup = 0; warmup < 2; warmup++)
                {
                    await MeasureShared(false, pageSize, -1);
                    await MeasureShared(true, pageSize, -1);
                }
                var samples = new List<Sample>();
                for (int trial = 0; trial < 5; trial++)
                {
                    // Reverse order on alternate trials to avoid consistently favoring the later run.
                    samples.Add(await MeasureShared(trial % 2 != 0, pageSize, trial));
                    samples.Add(await MeasureShared(trial % 2 == 0, pageSize, trial));
                }
                var summaries = samples.GroupBy(s => s.Mode).Select(group => new
                {
                    Mode = group.Key, MedianMs = Median(group.Select(s => s.ElapsedMs)),
                    PhysicalCalls = group.Select(s => s.PhysicalCalls).Distinct().ToArray(),
                    PhysicalRecords = group.Select(s => s.PhysicalRecords).Distinct().ToArray(),
                    PhysicalPayloadBytes = group.Select(s => s.PhysicalPayloadBytes).Distinct().ToArray()
                }).ToArray();
                foreach (var summary in summaries)
                    Console.WriteLine($"{target,5} tenants / page {pageSize}: {summary.Mode,-15} {summary.MedianMs:F3} ms; " +
                        $"physical calls={string.Join(',', summary.PhysicalCalls)}, records={string.Join(',', summary.PhysicalRecords)}");
                pages.Add(new { PageSize = pageSize, Summaries = summaries, Samples = samples });
            }
            counter.ForceSharedFallback = false;
            object? tenantControl = null;
            if (target > 0)
            {
                await using var tenant = await Connect(TenantName(0));
                CheckTenant(await tenant.EnumerateAsync(50), 50);
                var samples = new List<Sample>();
                for (int trial = 0; trial < 5; trial++)
                {
                    counter.Reset();
                    long start = Stopwatch.GetTimestamp();
                    var page = await tenant.EnumerateAsync(50);
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    CheckTenant(page, 50);
                    samples.Add(counter.Sample("tenant-forward", trial, elapsed));
                }
                tenantControl = new { MedianMs = Median(samples.Select(s => s.ElapsedMs)), Samples = samples };
            }
            int physicalCount = await CheckPhysical(store, target);
            stages.Add(new { TenantRecords = target, SharedRecords = 10, RawPhysicalRecordsVerified = physicalCount,
                Pages = pages, TenantControl = tenantControl });
            await Save(false);
        }
        await Save(true);
        Console.WriteLine($"Complete: {Path.Combine(output, "results.json")}");

        async Task<Sample> MeasureShared(bool fallback, int pageSize, int trial)
        {
            // Only the known unpartitioned admin request is projected; partitioned requests always forward.
            counter.ForceSharedFallback = fallback;
            counter.Reset();
            long start = Stopwatch.GetTimestamp();
            var page = await admin.EnumerateAsync(pageSize);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (page.Records is not { Length: 10 } || page.NextAfterKey is not null)
                throw new InvalidDataException("Shared page count or continuation mismatch.");
            for (int n = 0; n < 10; n++)
                if (page.Records[n].Key != SharedKey(n) || !page.Records[n].Payload.AsSpan().SequenceEqual(Payload("shared", n)))
                    throw new InvalidDataException("Shared page key or full payload mismatch.");
            return counter.Sample(fallback ? "forced-fallback" : "forward", trial, elapsed);
        }
        Task Save(bool complete) => File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            Complete = complete, CapturedAtUtc = DateTimeOffset.UtcNow,
            Environment = new { OS = RuntimeInformation.OSDescription, Runtime = RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount, TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                TransportMvid = typeof(TcpStoreHost).Assembly.ManifestModule.ModuleVersionId },
            Workload = "Loopback TLS/JWT, 64-byte opaque payloads, 500 records per tenant, 10 shared zz-* keys sort after tenant keys",
            Method = "Same live store, 2 warmups per mode, 5 interleaved trials per mode; connection setup and result assertions excluded from timing",
            Durability = "Existing transport defaults; no durability settings changed", DiagnosticTimeoutSeconds = 60,
            Stages = stages
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SharedKey(int n) => $"zz-{n}";
    private static string TenantKey(int n) => $"r{n:D4}";
    private static string TenantName(int n) => $"tenant-{n:D3}";
    private static byte[] Payload(string owner, int n)
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes($"{owner}/{n:D4}").CopyTo(bytes, 0);
        return bytes;
    }
    private static double Median(IEnumerable<double> values) => values.Order().ElementAt(2);
    private static void CheckTenant(EncodedStoreResponse page, int count)
    {
        if (page.Records?.Length != count || page.NextAfterKey != TenantKey(count - 1))
            throw new InvalidDataException("Tenant page count or continuation mismatch.");
        for (int n = 0; n < count; n++)
            if (page.Records[n].Key != TenantKey(n) || !page.Records[n].Payload.AsSpan().SequenceEqual(Payload(TenantName(0), n)))
                throw new InvalidDataException("Tenant page key or full payload mismatch.");
    }
    private static async Task<int> CheckPhysical(IEncodedEnvelopeStore store, int tenantRecords)
    {
        var expected = Enumerable.Range(0, 10).Select(n => Convert.ToBase64String(Payload("shared", n))).ToHashSet(StringComparer.Ordinal);
        for (int tenant = 0; tenant < tenantRecords / 500; tenant++)
            for (int n = 0; n < 500; n++) expected.Add(Convert.ToBase64String(Payload(TenantName(tenant), n)));
        int count = 0;
        string? after = null;
        do
        {
            var page = await store.EnumerateAsync(1024, after);
            foreach (var item in page.Records ?? throw new InvalidDataException("Missing raw records."))
            {
                if (!expected.Remove(Convert.ToBase64String(item.Payload))) throw new InvalidDataException("Unexpected or duplicate physical payload.");
                count++;
            }
            after = page.NextAfterKey;
        } while (after is not null);
        if (count != tenantRecords + 10 || expected.Count != 0) throw new InvalidDataException("Physical partition enumeration lost records.");
        return count;
    }
    private static string Token(RSA key, string subject)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "at+jwt", kid = "benchmark" }));
        string body = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "https://benchmark.invalid", ["sub"] = subject, ["aud"] = "storage", ["scope"] = "storage",
            ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()
        }));
        return header + "." + body + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private sealed record Sample(string Mode, int Trial, double ElapsedMs, long PhysicalCalls, long PhysicalRecords, long PhysicalPayloadBytes);
    private sealed class CountingStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        private long _calls, _records, _bytes;
        internal bool ForceSharedFallback { get; set; }
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        internal void Reset() { Interlocked.Exchange(ref _calls, 0); Interlocked.Exchange(ref _records, 0); Interlocked.Exchange(ref _bytes, 0); }
        internal Sample Sample(string mode, int trial, double elapsed) => new(mode, trial, elapsed,
            Interlocked.Read(ref _calls), Interlocked.Read(ref _records), Interlocked.Read(ref _bytes));
        public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            if (ForceSharedFallback && request.Command == EncodedStoreCommand.Enumerate)
                request = new() { Command = request.Command, PageSize = request.PageSize, AfterKey = request.AfterKey };
            var response = await inner.ExecuteAsync(request, cancellationToken);
            if (request.Command == EncodedStoreCommand.Enumerate)
            {
                Interlocked.Increment(ref _calls);
                Interlocked.Add(ref _records, response.Records?.LongLength ?? 0);
                Interlocked.Add(ref _bytes, response.Records?.Sum(record => (long)record.Payload.Length) ?? 0);
            }
            return response;
        }
    }
}
