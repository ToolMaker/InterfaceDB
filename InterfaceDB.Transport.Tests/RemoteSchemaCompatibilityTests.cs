using System.Net;
using Idb.Transport;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;

namespace InterfaceDB.Transport.Tests;

/// <summary>
/// Transport compatibility gates and persisted-data migration are separate responsibilities.
/// These tests deliberately document currently unsafe application schema rewrites; they do not
/// register a migration callback or claim that the opaque transport migrates application data.
/// </summary>
[TestClass]
public sealed class RemoteSchemaCompatibilityTests
{
    private static readonly MessagePackSerializerOptions DtoOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    private readonly BinaryObjectSerializer<SchemaDocument> _envelopes = new();
    private TestCertificates _certificates = null!;
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _certificates = new();
        _directory = Path.Combine(Path.GetTempPath(), "InterfaceDB.RemoteSchemaCompatibilityTests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _certificates.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    [DataRow("contract-version")]
    [DataRow("schema-hash")]
    public async Task NewClientIsRejectedByOldHostBeforeStoredEnvelopeIsAccessed(string changedField)
    {
        var original = CanonicalEnvelope("1.0", "original-v1", [1, 2, 3]);
        await using var physical = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory);
        var counted = new CountingBorrowedStore(physical);
        await using var host = await TcpStoreHost.StartAsync(HostOptions(counted, ManifestV1()));
        await using (var oldClient = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, ManifestV1())))
            await oldClient.PutAsync("document", original);

        var changed = changedField == "contract-version"
            ? ManifestV1() with { ContractVersion = "2.0" }
            : ManifestV1() with { ContractSchemaHash = "schema-v2" };
        var beforeHandshake = counted.Calls;
        var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
        {
            await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, changed));
        });

        Assert.AreEqual(RemoteErrorCode.IncompatibleManifest, rejected.Code);
        Assert.AreEqual(beforeHandshake, counted.Calls, "A schema/version mismatch must not invoke the storage provider.");
        CollectionAssert.AreEqual(original, await physical.GetAsync("document"));
        Assert.AreEqual("1.0", _envelopes.Deserialize(original).VersionTag);
    }

    [TestMethod]
    public async Task ReopeningSameStoreWithV2ManifestDoesNotMigrateExistingCanonicalEnvelope()
    {
        var original = CanonicalEnvelope("1.0", "original-v1", MessagePackSerializer.Serialize(
            new ProgressV1 { RecordId = "lesson-1", Completed = 7 }, DtoOptions));
        await using (var physical = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(physical, ManifestV1())))
        await using (var oldClient = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, ManifestV1())))
            await oldClient.PutAsync("document", original);

        var versionTwo = ManifestV1() with { ContractVersion = "2.0", ContractSchemaHash = "schema-v2" };
        await using (var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory))
        {
            var counted = new CountingBorrowedStore(reopened);
            await using var updatedHost = await TcpStoreHost.StartAsync(HostOptions(counted, versionTwo));
            await using var newClient = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(updatedHost, versionTwo));
            var returned = await newClient.GetAsync("document");

            CollectionAssert.AreEqual(original, returned, "Updating the host's declared manifest must not rewrite opaque stored bytes.");
            var envelope = _envelopes.Deserialize(returned!);
            Assert.AreEqual("1.0", envelope.VersionTag, "The accepted v2 handshake does not upgrade the persisted v1 envelope tag.");
            Assert.AreEqual("original-v1", envelope.ObjectInstance!.Label);
            var forwardRead = MessagePackSerializer.Deserialize<ProgressV2>(envelope.ObjectInstance.DomainPayload, DtoOptions);
            Assert.AreEqual(7, forwardRead.Completed);
            Assert.IsNull(forwardRead.Award, "A new field's default is not an application migration or backfill.");

            var calls = counted.Calls;
            var rejected = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () =>
            {
                await using var unexpected = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(updatedHost, ManifestV1()));
            });
            Assert.AreEqual(RemoteErrorCode.IncompatibleManifest, rejected.Code);
            Assert.AreEqual(calls, counted.Calls);
        }
        await using var finalReopen = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory);
        var persisted = await finalReopen.GetAsync("document");
        CollectionAssert.AreEqual(original, persisted);
        Assert.AreEqual("1.0", _envelopes.Deserialize(persisted!).VersionTag);
    }

    [TestMethod]
    public void AdditiveMessagePackDtoFixtureReadsOldAndNewDataWithoutInventingNewFields()
    {
        // These DTOs deliberately share numeric keys. They are a compatibility fixture,
        // not two CLR implementations implicitly substituted by BinaryObjectSerializer.
        var oldBytes = MessagePackSerializer.Serialize(new ProgressV1 { RecordId = "lesson-1", Completed = 7 }, DtoOptions);
        var readByNewClient = MessagePackSerializer.Deserialize<ProgressV2>(oldBytes, DtoOptions);
        Assert.AreEqual("lesson-1", readByNewClient.RecordId);
        Assert.AreEqual(7, readByNewClient.Completed);
        Assert.IsNull(readByNewClient.Award);

        var newBytes = MessagePackSerializer.Serialize(new ProgressV2 { RecordId = "lesson-1", Completed = 7, Award = "gold" }, DtoOptions);
        var readByOldClient = MessagePackSerializer.Deserialize<ProgressV1>(newBytes, DtoOptions);
        Assert.AreEqual("lesson-1", readByOldClient.RecordId);
        Assert.AreEqual(7, readByOldClient.Completed);
        Assert.AreEqual("gold", MessagePackSerializer.Deserialize<ProgressV2>(newBytes, DtoOptions).Award,
            "Reading through the old DTO alone does not alter the source bytes.");
    }

    [TestMethod]
    public async Task OldDtoReadModifyWriteDropsFutureFieldWhenBothClientsAreDeclaredCompatible()
    {
        // The application deliberately advertises one manifest for both DTO revisions. The
        // server cannot inspect this nested domain payload or recover a field the old writer drops.
        var fixtureManifest = ManifestV1() with { ContractIdentity = "ExplicitMessagePackCompatibilityFixture" };
        var newerDto = MessagePackSerializer.Serialize(new ProgressV2
        {
            RecordId = "lesson-1", Completed = 7, Award = "gold"
        }, DtoOptions);
        var original = CanonicalEnvelope("2.0", "compatible-fixture", newerDto);
        byte[] rewritten;
        await using (var physical = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory))
        await using (var host = await TcpStoreHost.StartAsync(HostOptions(physical, fixtureManifest)))
        await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(ClientOptions(host, fixtureManifest)))
        {
            await client.PutAsync("document", original);
            var beforeEdit = await client.GetAsync("document");
            var envelope = _envelopes.Deserialize(beforeEdit!);
            Assert.AreEqual("gold", MessagePackSerializer.Deserialize<ProgressV2>(envelope.ObjectInstance!.DomainPayload, DtoOptions).Award);

            var oldApplicationView = MessagePackSerializer.Deserialize<ProgressV1>(envelope.ObjectInstance.DomainPayload, DtoOptions);
            oldApplicationView.Completed = 8;
            envelope.ObjectInstance.DomainPayload = MessagePackSerializer.Serialize(oldApplicationView, DtoOptions);
            rewritten = _envelopes.Serialize(envelope);
            await client.PutAsync("document", rewritten);
        }

        await using var reopened = await SegmentCoreEncodedEnvelopeStore.OpenAsync(_directory);
        var persisted = await reopened.GetAsync("document");
        CollectionAssert.AreEqual(rewritten, persisted, "The remote store durably preserved exactly what the old application rewrote.");
        var finalEnvelope = _envelopes.Deserialize(persisted!);
        var finalDto = MessagePackSerializer.Deserialize<ProgressV2>(finalEnvelope.ObjectInstance!.DomainPayload, DtoOptions);
        Assert.AreEqual(8, finalDto.Completed);
        Assert.IsNull(finalDto.Award,
            "Observed unsafe behavior: the old DTO silently removes the newer Award field during read/modify/write.");
        Assert.AreEqual("2.0", finalEnvelope.VersionTag,
            "An envelope version label does not preserve fields omitted by the application's serializer.");
    }

    private byte[] CanonicalEnvelope(string version, string label, byte[] domainPayload) => _envelopes.Serialize(
        new FileObjectEnvelope<SchemaDocument>
        {
            EnvelopeCacheKey = "document", VersionTag = version, RevisionTimestampUtcMicroseconds = 123,
            ObjectInstance = new() { Label = label, DomainPayload = domainPayload }
        });

    private TcpStoreHostOptions HostOptions(IEncodedEnvelopeStore store, CompatibilityManifest manifest) => new()
    {
        ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = _certificates.Server,
        Stores = [new("schema-test", manifest, store)], ClientCertificateValidation = _certificates.ValidateClient,
        AuthorizeClient = (certificate, alias) => certificate.Thumbprint == _certificates.Client.Thumbprint && alias == "schema-test",
        HandshakeTimeout = TimeSpan.FromSeconds(5), MaximumOperationTimeout = TimeSpan.FromSeconds(5)
    };

    private TcpStoreClientOptions ClientOptions(TcpStoreHost host, CompatibilityManifest manifest) => new()
    {
        Host = IPAddress.Loopback.ToString(), Port = host.LocalEndPoint.Port, TargetHost = "localhost", StoreAlias = "schema-test",
        Manifest = manifest, ClientCertificate = _certificates.Client, ServerCertificateValidation = _certificates.ValidateServer,
        ConnectTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5)
    };

    private static CompatibilityManifest ManifestV1() => new()
    {
        ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1", EnvelopeFormat = "IDB6",
        ContractIdentity = "SchemaDocument", ContractVersion = "1.0", ContractSchemaHash = "schema-v1",
        ImplementationManifestHash = "schema-document-type", IndexDefinitionHash = "none"
    };

    private sealed class CountingBorrowedStore(IEncodedEnvelopeStore inner) : IEncodedEnvelopeStore
    {
        private int _calls;
        public EncodedStoreCapabilities Capabilities => inner.Capabilities;
        internal int Calls => Volatile.Read(ref _calls);
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.ExecuteAsync(request, cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [MessagePackObject]
    public sealed class SchemaDocument : IStoredContract<SchemaDocument>
    {
        [Key(0)] public string Label { get; set; } = "";
        [Key(1)] public byte[] DomainPayload { get; set; } = [];
        [IgnoreMember] public IObjectEnvelope<SchemaDocument>? Envelope { get; set; }
    }

    [MessagePackObject]
    public sealed class ProgressV1
    {
        [Key(0)] public string RecordId { get; set; } = "";
        [Key(1)] public int Completed { get; set; }
    }

    [MessagePackObject]
    public sealed class ProgressV2
    {
        [Key(0)] public string RecordId { get; set; } = "";
        [Key(1)] public int Completed { get; set; }
        [Key(2)] public string? Award { get; set; }
    }
}
