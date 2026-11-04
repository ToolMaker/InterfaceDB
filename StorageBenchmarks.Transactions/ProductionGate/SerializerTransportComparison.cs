using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Serialization.Protobuf;
using Idb.Transport;

internal static class SerializerTransportComparison
{
    private sealed record Measurement(string Serializer, string Operation, int RecordsPerOperation,
        double[] SamplesUs, double P50Us, double P95Us, double AllocatedBytesPerOperation);

    public static async Task RunAsync(string[] args)
    {
        var output = Path.GetFullPath(ProductionGateOptions.ParseString(args, "--output") ?? "serializer-comparison.json");
        var iterationsText = ProductionGateOptions.ParseString(args, "--iterations") ?? "200";
        if (!int.TryParse(iterationsText, out var iterations) || iterations < 3)
            throw new ArgumentException("--iterations must be an integer of at least 3.");
        const int pageSize = 128;
        const int datasetSize = 512;
        const string alias = "serializer-benchmark";
        var scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InterfaceDbSerializerComparison"));
        var root = Path.GetFullPath(Path.Combine(scratch, Guid.NewGuid().ToString("N")));
        if (Path.GetDirectoryName(root) != scratch) throw new InvalidOperationException("Invalid benchmark scratch directory.");
        var dataset = Enumerable.Range(0, datasetSize).Select(i => GateRecord.Create(i) with { Revision = i + 1 }).ToArray();
        var metrics = new List<Measurement>();
        var sizes = new List<object>();
        (string Name, string Format, Func<IRevisionObjectSerializer<IProductionGateRecord>> Create)[] formats =
        [
            ("MessagePack", "IDB6-or-IDBE", GateSerializers.CreateMessagePack),
            ("Protobuf", ProtobufObjectSerializer<IProductionGateRecord>.EnvelopeFormat, GateSerializers.CreateProtobuf)
        ];
        var reverse = args.Contains("--reverse");
        if (reverse) Array.Reverse(formats);
        using var server = CreateCertificate("localhost", server: true);
        using var client = CreateCertificate("serializer-benchmark-client", server: false);
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (name, format, create) in formats)
            {
                Console.WriteLine($"Serializer comparison: {name}");
                var codec = create();
                var envelopes = dataset.Select(row => new FileObjectEnvelope<IProductionGateRecord>
                {
                    EnvelopeCacheKey = row.Id, VersionTag = "1.0.0",
                    RevisionTimestampUtcMicroseconds = row.Revision,
                    ObjectInstance = new ProductionGateRecord(row.Id, row.Name, row.Age, row.Role, row.Content, row.Revision)
                }).ToArray();
                var payloads = envelopes.Select(codec.Serialize).ToArray();
                for (var i = 0; i < dataset.Length; i++) Validate(codec, payloads[i], dataset[i]);
                sizes.Add(new { Serializer = name, TotalBytes = payloads.Sum(p => (long)p.Length), MeanBytes = payloads.Average(p => p.Length) });

                await MeasureAsync(name, "encode-envelope", pageSize, i =>
                {
                    var start = i % (datasetSize / pageSize) * pageSize;
                    for (var j = start; j < start + pageSize; j++) GC.KeepAlive(codec.Serialize(envelopes[j]));
                    return ValueTask.CompletedTask;
                });
                await MeasureAsync(name, "decode-envelope", pageSize, i =>
                {
                    var start = i % (datasetSize / pageSize) * pageSize;
                    for (var j = start; j < start + pageSize; j++) GC.KeepAlive(codec.Deserialize(payloads[j]));
                    return ValueTask.CompletedTask;
                });

                await using var store = await SegmentCoreEncodedEnvelopeStore.OpenAsync(Path.Combine(root, name));
                foreach (var batch in dataset.Select((row, i) => new EncodedMutation(row.Id, payloads[i])).Chunk(64))
                    await store.ApplyBatchAsync(batch);
                var manifest = new CompatibilityManifest
                {
                    ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0", EnvelopeFormat = format,
                    ContractIdentity = alias, ContractVersion = "1.0.0",
                    ContractSchemaHash = "six-fields-v1", ImplementationManifestHash = "gate-record-v1",
                    IndexDefinitionHash = "opaque-keys-v1"
                };
                await using var host = await TcpStoreHost.StartAsync(new()
                {
                    ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = server,
                    Stores = [new(alias, manifest, store)], AuthorizeClient = (certificate, selected) =>
                        selected == alias && certificate.GetCertHashString(HashAlgorithmName.SHA256) == client.GetCertHashString(HashAlgorithmName.SHA256),
                    ClientCertificateValidation = Pin(client)
                });
                await using var remote = await TcpEncodedEnvelopeStore.ConnectAsync(new()
                {
                    Host = IPAddress.Loopback.ToString(), Port = host.LocalEndPoint.Port, TargetHost = "localhost",
                    StoreAlias = alias, Manifest = manifest, ClientCertificate = client, ServerCertificateValidation = Pin(server),
                    RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate | EncodedStoreCapabilities.DurableCommits
                });
                foreach (var (prefix, backend) in new (string, IEncodedEnvelopeStore)[] { ("local", store), ("tcp", remote) })
                {
                    // Check every returned byte and full object before timing the same read paths.
                    for (var i = 0; i < dataset.Length; i++)
                    {
                        var row = dataset[i];
                        var bytes = await backend.GetAsync(row.Id) ?? throw new InvalidDataException("Missing benchmark record.");
                        VerifyBytes(bytes, payloads[i]);
                        Validate(codec, bytes, row);
                    }
                    for (var start = 0; start < datasetSize; start += pageSize)
                    {
                        var page = await backend.EnumerateAsync(pageSize, start == 0 ? null : dataset[start - 1].Id);
                        if (page.Records is not { Length: pageSize }) throw new InvalidDataException("Wrong benchmark page size.");
                        for (var j = 0; j < pageSize; j++)
                        {
                            var expected = dataset[start + j];
                            var actual = page.Records[j];
                            if (actual.Key != expected.Id) throw new InvalidDataException("Wrong benchmark page key.");
                            VerifyBytes(actual.Payload, payloads[start + j]);
                            Validate(codec, actual.Payload, expected);
                        }
                    }

                    await MeasureAsync(name, prefix + "-get-bytes", 1, async i =>
                    {
                        var bytes = await backend.GetAsync(dataset[i % datasetSize].Id)
                            ?? throw new InvalidDataException("Missing benchmark record.");
                        GC.KeepAlive(bytes);
                    });
                    await MeasureAsync(name, prefix + "-get-object", 1, async i =>
                    {
                        var bytes = await backend.GetAsync(dataset[i % datasetSize].Id)
                            ?? throw new InvalidDataException("Missing benchmark record.");
                        GC.KeepAlive(codec.Deserialize(bytes));
                    });
                    await MeasureAsync(name, prefix + "-page-objects", pageSize, async i =>
                    {
                        var start = i % (datasetSize / pageSize) * pageSize;
                        var page = await backend.EnumerateAsync(pageSize, start == 0 ? null : dataset[start - 1].Id);
                        if (page.Records is not { Length: pageSize }) throw new InvalidDataException("Wrong benchmark page size.");
                        foreach (var record in page.Records) GC.KeepAlive(codec.Deserialize(record.Payload));
                    });
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var report = new FileStream(output, FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(report, new
            {
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Transport = "Persistent mutual TLS TCP over loopback, MessagePack command framing",
                Reverse = reverse, Iterations = iterations, Records = datasetSize,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
                Verification = "Exact stored bytes and all six entity fields, plus key, version and envelope revision, before timing",
                Sizes = sizes, Metrics = metrics, Passed = true
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            // Only this invocation's newly created GUID directory.
            Directory.Delete(root, recursive: true);
        }

        async Task MeasureAsync(string name, string operation, int count, Func<int, ValueTask> run)
        {
            for (var i = 0; i < 32; i++) await run(i);
            var samples = new double[iterations];
            var allocated = GC.GetTotalAllocatedBytes(true);
            for (var i = 0; i < iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await run(i);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
            var bytes = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)iterations;
            var sorted = samples.Order().ToArray();
            var p50 = sorted[(int)Math.Ceiling(sorted.Length * .50) - 1];
            var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            metrics.Add(new(name, operation, count, samples, p50, p95, bytes));
            Console.WriteLine(FormattableString.Invariant($"  {operation} ({count} record(s)): p50 {p50:F2} us; p95 {p95:F2} us"));
        }
    }

    private static void Validate(IRevisionObjectSerializer<IProductionGateRecord> codec, byte[] bytes, GateRecord expected)
    {
        var decoded = codec.Deserialize(bytes);
        if (decoded.ObjectInstance is not ProductionGateRecord record || record.ToGateRecord() != expected
            || decoded.EnvelopeCacheKey != expected.Id || decoded.VersionTag != "1.0.0"
            || decoded.RevisionTimestampUtcMicroseconds != expected.Revision || codec.ReadRevision(bytes) != expected.Revision)
            throw new InvalidDataException("Serializer did not preserve the benchmark envelope.");
    }

    private static void VerifyBytes(byte[] actual, byte[] expected)
    {
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Encoded transport changed the stored bytes.");
    }

    private static RemoteCertificateValidationCallback Pin(X509Certificate2 certificate)
    {
        var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return (_, peer, _, errors) => peer is not null
            && (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) == 0
            && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected;
    }

    private static X509Certificate2 CreateCertificate(string name, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);
            request.CertificateExtensions.Add(names.Build());
        }
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel needs a temporary private-key container. Pin these test-only certificates.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    }
}
