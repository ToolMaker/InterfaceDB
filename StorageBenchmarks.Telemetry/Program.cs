using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Idb.Transport;
using InterfaceDB.Transport.Tests;

using var certificates = new TestCertificates();
var root = Path.Combine(Path.GetTempPath(), "idb-telemetry-bench-" + Guid.NewGuid().ToString("N"));
var output = args[0];
var count = 512;
var payload = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
var manifest = new CompatibilityManifest { ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0", EnvelopeFormat = "opaque",
    ContractIdentity = "bench", ContractVersion = "1.0", ContractSchemaHash = "schema", ImplementationManifestHash = "types", IndexDefinitionHash = "indexes" };
#if TELEMETRY
await using var telemetry = new BenchmarkTelemetry();
#endif
try
{
    await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(root
#if TELEMETRY
        , telemetry: telemetry.Service
#endif
    );
    await using var host = await TcpStoreHost.StartAsync(new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = certificates.Server,
        ClientCertificateValidation = certificates.ValidateClient, AuthorizeClient = (_, _) => true,
        Stores = [new("bench", manifest, store)]
#if TELEMETRY
        , Telemetry = telemetry.Service
#endif
    });
    await using var client = await TcpEncodedEnvelopeStore.ConnectAsync(new()
    {
        Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "bench", Manifest = manifest,
        ClientCertificate = certificates.Client, ServerCertificateValidation = certificates.ValidateServer
#if TELEMETRY
        , Telemetry = telemetry.Service
#endif
    });
    await client.PutAsync("record", payload);
    for (int i = 0; i < 100; i++) await Pair();
    await Task.Delay(100);
    var allocated = GC.GetTotalAllocatedBytes(true);
    var started = Stopwatch.GetTimestamp();
    var measurements = new double[count];
    for (int i = 0; i < count; i++)
    {
        var before = Stopwatch.GetTimestamp(); await Pair();
        measurements[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
    }
    var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
#if TELEMETRY
    await telemetry.DisposeAsync();
    var stats = telemetry.Statistics;
#else
    var stats = new { Mode = "baseline" };
#endif
    allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    Array.Sort(measurements);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Telemetry = stats, Count = count, PayloadBytes = payload.Length,
        P50Milliseconds = measurements[count / 2], P95Milliseconds = measurements[(int)(count * .95)],
        OperationsPerSecond = count * 2 / elapsed, AllocatedBytesPerPair = (double)allocated / count,
        CoreMvid = typeof(Idb.Libraries.Abstractions.Dtos.IStoredContract<>).Assembly.ManifestModule.ModuleVersionId,
        TransportMvid = typeof(TcpStoreHost).Assembly.ManifestModule.ModuleVersionId }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(output);
    async Task Pair()
    {
        await client.PutAsync("record", payload);
        var read = await client.GetAsync("record");
        if (read is null || !read.AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Round trip mismatch.");
    }
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
