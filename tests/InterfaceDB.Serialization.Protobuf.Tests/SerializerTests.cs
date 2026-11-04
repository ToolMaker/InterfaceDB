using System.Security.Cryptography;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Encryption.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Extensions;
using Idb.Libraries.Implementation.Serialization;
using Idb.Serialization.Protobuf;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Meta;

namespace InterfaceDB.Serialization.Protobuf.Tests;

[TestClass]
public sealed class SerializerTests
{
    internal static ProtobufObjectSerializer<IRecord> Create(int limit = 1024 * 1024)
    {
        var model = RuntimeTypeModel.Create();
        model.Add(typeof(Record), false).Add(1, nameof(Record.Name)).Add(2, nameof(Record.Score))
            .Add(3, nameof(Record.Secret)).Add(4, nameof(Record.Amount)).Add(5, nameof(Record.When));
        model.Add(typeof(OtherRecord), false).Add(1, nameof(OtherRecord.Name)).Add(2, nameof(OtherRecord.Score))
            .Add(3, nameof(OtherRecord.Secret));
        return new(model, new Dictionary<string, Type> { ["record-v1"] = typeof(Record), ["other-v1"] = typeof(OtherRecord) }, limit);
    }

    private static FileObjectEnvelope<IRecord> Envelope(IRecord? record = null) => new()
    {
        EnvelopeCacheKey = "order-6", VersionTag = "1.0.0", RevisionTimestampUtcMicroseconds = 123456789,
        ObjectInstance = record ?? new Record { Name = "Unicode ☀", Score = 42, Amount = 123.456789m, When = new DateTime(2026, 11, 4) },
        ExtensionMetadata = [0, 1, 255], CacheObjectInstance = true,
        EncryptedFields = new Dictionary<string, EncryptedFieldValue>
        { ["Secret"] = new() { Ciphertext = [1, 2, 3], ProfileName = "profile", PropertyTypeName = "System.String" } }
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RoundTripPreservesEnvelopeAndConcreteTypes(bool other)
    {
        var serializer = Create();
        var original = Envelope(other ? new OtherRecord { Name = null, Score = int.MinValue } : null);
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(original, stream);
        var bytes = stream.ToArray();
        Assert.AreEqual(original.RevisionTimestampUtcMicroseconds, serializer.ReadRevision(bytes));
        stream.Position = 0;
        Assert.AreEqual(original.RevisionTimestampUtcMicroseconds, await serializer.ReadRevisionAsync(stream));
        stream.Position = 0;
        var restored = await serializer.DeserializeAsync(stream);
        Assert.AreEqual(original.ObjectInstance!.GetType(), restored.ObjectInstance!.GetType());
        Assert.AreEqual(original.ObjectInstance.Name, restored.ObjectInstance.Name);
        Assert.AreEqual(original.ObjectInstance.Score, restored.ObjectInstance.Score);
        Assert.AreEqual(original.EnvelopeCacheKey, restored.EnvelopeCacheKey);
        Assert.AreEqual(original.VersionTag, restored.VersionTag);
        Assert.IsTrue(restored.CacheObjectInstance);
        CollectionAssert.AreEqual(original.ExtensionMetadata, restored.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, restored.EncryptedFields["Secret"].Ciphertext);
        Assert.AreEqual("profile", restored.EncryptedFields["Secret"].ProfileName);
        Assert.AreEqual("System.String", restored.EncryptedFields["Secret"].PropertyTypeName);
        if (restored.ObjectInstance is Record record)
        {
            Assert.AreEqual(123.456789m, record.Amount);
            Assert.AreEqual(new DateTime(2026, 11, 4), record.When);
        }
    }

    [TestMethod]
    [DataRow(DateTimeKind.Unspecified)]
    [DataRow(DateTimeKind.Utc)]
    [DataRow(DateTimeKind.Local)]
    public void DateTimeKindAndTicksSurviveRoundTrip(DateTimeKind kind)
    {
        var value = new DateTime(2026, 11, 4, 12, 34, 56, kind).AddTicks(1234);
        var serializer = Create();
        var restored = (Record)serializer.Deserialize(serializer.Serialize(Envelope(new Record { When = value }))).ObjectInstance!;
        Assert.AreEqual(value.Ticks, restored.When.Ticks);
        Assert.AreEqual(value.Kind, restored.When.Kind);
    }

    [TestMethod]
    public void NullAndEmptyMetadataRemainDistinct()
    {
        var serializer = Create();
        var envelope = Envelope();
        envelope.ExtensionMetadata = null;
        Assert.IsNull(serializer.Deserialize(serializer.Serialize(envelope)).ExtensionMetadata);
        envelope.ExtensionMetadata = [];
        Assert.AreEqual(0, serializer.Deserialize(serializer.Serialize(envelope)).ExtensionMetadata!.Length);
    }

    [TestMethod]
    public void UnknownTypesWrongFormatAndInvalidMetadataAreRejected()
    {
        var serializer = Create();
        var bytes = serializer.Serialize(Envelope());
        // ASCII persistent type IDs are length-preserving here.
        var text = System.Text.Encoding.UTF8.GetBytes("record-v1");
        int at = bytes.AsSpan().IndexOf(text);
        Assert.IsTrue(at >= 0);
        bytes[at] = (byte)'x';
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(bytes));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.ReadRevision(bytes));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize([]));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize([0x08, 0x02]));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize([0x12, 0xff]));
        var envelope = Envelope();
        envelope.RevisionTimestampUtcMicroseconds = -1;
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Serialize(envelope));
        envelope.RevisionTimestampUtcMicroseconds = 1;
        envelope.ExtensionMetadata = new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1];
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Serialize(envelope));
    }

    [TestMethod]
    public async Task BoundsAndCancellationApplyToStreamsAndArrays()
    {
        var serializer = Create(128);
        var large = Envelope(new Record { Name = new string('x', 500) });
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Serialize(large));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(new byte[129]));
        using var source = new NonSeekable(new byte[129]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => serializer.DeserializeAsync(source).AsTask());
        using var output = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => serializer.SerializeAsync(large, output).AsTask());
        Assert.AreEqual(0L, output.Length);
        var token = new CancellationToken(true);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => serializer.SerializeAsync(Envelope(), output, token).AsTask());
    }

    [TestMethod]
    public void EncryptionAuthenticatesTheExactRepositoryContext()
    {
        var serializer = new EncryptingObjectSerializer<IRecord>(Create(), new AesGcmEncryptor(new Keys()), 1024 * 1024);
        var bytes = serializer.Serialize(Envelope(), "store:key"u8);
        Assert.AreEqual(123456789L, serializer.ReadRevision(bytes, "store:key"u8));
        Assert.AreEqual("Unicode ☀", serializer.Deserialize(bytes, "store:key"u8).ObjectInstance!.Name);
        Assert.ThrowsExactly<CryptographicException>(() => serializer.Deserialize(bytes, "other:key"u8));
    }

    [TestMethod]
    public void EncryptionRotationPreservesContextAndProtobufData()
    {
        var keys = new RotatingKeys();
        var encryption = new AesGcmEncryptor(keys, new AesGcmEncryptorOptions
        { RotationMode = EncryptionRotationMode.ReencryptOnRead });
        var serializer = new EncryptingObjectSerializer<IRecord>(Create(), encryption, 1024 * 1024);
        var oldPayload = serializer.Serialize(Envelope(), "store:key"u8);
        keys.Active = "new";
        var rotated = serializer.DeserializeForRead(oldPayload, "store:key"u8);
        Assert.IsNotNull(rotated.ReplacementPayload);
        Assert.AreEqual(42, rotated.Envelope.ObjectInstance!.Score);
        var current = serializer.DeserializeForRead(rotated.ReplacementPayload, "store:key"u8);
        Assert.IsNull(current.ReplacementPayload);
        Assert.AreEqual(123456789L, current.Envelope.RevisionTimestampUtcMicroseconds);
        CollectionAssert.AreEqual(new byte[] { 0, 1, 255 }, current.Envelope.ExtensionMetadata);
        Assert.ThrowsExactly<CryptographicException>(() => serializer.Deserialize(rotated.ReplacementPayload, "other:key"u8));
    }

    [TestMethod]
    public void ForgedLengthDoesNotAllocateItsAdvertisedSize()
    {
        var serializer = Create();
        _ = serializer.Deserialize(serializer.Serialize(Envelope())); // Warm up the compiled model.
        // Envelope bytes field 7 claims 16 MiB, but supplies no payload.
        byte[] truncated = [0x3a, 0x80, 0x80, 0x80, 0x08];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(truncated));
        Assert.IsLessThan(1024L * 1024, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public void IndependentWireFixtureAndUnknownFutureFieldsAreReadable()
    {
        // Protobuf wire types: field 1=version, 2=key, 3=contract version, 4=revision,
        // 5=type ID, 7=entity (field 1 string "x", field 2 int32 42).
        var bytes = Convert.FromHexString("080112016B1A013120072A097265636F72642D76313A050A0178102A");
        var serializer = Create();
        var restored = serializer.Deserialize(bytes);
        Assert.AreEqual("k", restored.EnvelopeCacheKey);
        Assert.AreEqual(7L, restored.RevisionTimestampUtcMicroseconds);
        Assert.AreEqual("x", restored.ObjectInstance!.Name);
        Assert.AreEqual(42, restored.ObjectInstance.Score);
        // Unknown optional envelope field 20 must not change known data.
        Assert.AreEqual(42, serializer.Deserialize([.. bytes, 0xA0, 0x01, 0x01]).ObjectInstance!.Score);
    }

    [TestMethod]
    public void RegistrationRejectsAmbiguousOrUnmappedTypesAndFreezesTheModel()
    {
        var model = RuntimeTypeModel.Create();
        var types = new Dictionary<string, Type> { ["record"] = typeof(Record) };
        Assert.ThrowsExactly<ArgumentException>(() => new ProtobufObjectSerializer<IRecord>(model, types));
        model.Add(typeof(Record), false).Add(1, nameof(Record.Name));
        Assert.ThrowsExactly<ArgumentException>(() => new ProtobufObjectSerializer<IRecord>(model,
            new Dictionary<string, Type> { ["one"] = typeof(Record), ["two"] = typeof(Record) }));
        _ = new ProtobufObjectSerializer<IRecord>(model, types);
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Add(typeof(OtherRecord), false));
    }

    [TestMethod]
    public void RevisionInspectionDoesNotMaterializeTheEntity()
    {
        var model = RuntimeTypeModel.Create();
        model.Add(typeof(CountedRecord), false).Add(1, nameof(CountedRecord.Score));
        var serializer = new ProtobufObjectSerializer<IRecord>(model,
            new Dictionary<string, Type> { ["counted"] = typeof(CountedRecord) });
        var bytes = serializer.Serialize(Envelope(new CountedRecord { Score = 3 }));
        CountedRecord.Constructions = 0;
        Assert.AreEqual(123456789L, serializer.ReadRevision(bytes));
        Assert.AreEqual(0, CountedRecord.Constructions);
        Assert.AreEqual(3, serializer.Deserialize(bytes).ObjectInstance!.Score);
        Assert.AreEqual(1, CountedRecord.Constructions);
    }

    [TestMethod]
    public void ConfiguredDepthBoundsApplyToReadsAndWrites()
    {
        static ProtobufObjectSerializer<IRecord> Serializer(int depth)
        {
            var model = RuntimeTypeModel.Create();
            model.Add(typeof(TreeRecord), false).Add(1, nameof(TreeRecord.Child));
            return new(model, new Dictionary<string, Type> { ["tree"] = typeof(TreeRecord) }, maximumDepth: depth);
        }
        TreeRecord tree = new();
        for (int i = 0; i < 20; i++) tree = new TreeRecord { Child = tree };
        var bytes = Serializer(64).Serialize(Envelope(tree));
        var bounded = Serializer(8);
        StringAssert.Contains(Assert.Throws<Exception>(() => bounded.Serialize(Envelope(tree))).Message.ToLowerInvariant(), "depth");
        StringAssert.Contains(Assert.Throws<Exception>(() => bounded.Deserialize(bytes)).ToString().ToLowerInvariant(), "depth");
    }

    [TestMethod]
    public void FrozenSerializerSupportsConcurrentCalls()
    {
        var serializer = Create();
        Parallel.For(0, 100, i =>
        {
            var envelope = Envelope(new Record { Name = i.ToString(), Score = i });
            var restored = serializer.Deserialize(serializer.Serialize(envelope));
            Assert.AreEqual(i, restored.ObjectInstance!.Score);
        });
    }

    [TestMethod]
    [DataRow("File", false, false)]
    [DataRow("File", true, false)]
    [DataRow("File", false, true)]
    [DataRow("File", true, true)]
    [DataRow("SegmentCore", false, false)]
    [DataRow("SegmentCore", true, false)]
    [DataRow("SegmentCore", false, true)]
    [DataRow("SegmentCore", true, true)]
    [DataRow("Atlas", false, false)]
    [DataRow("Atlas", true, false)]
    [DataRow("Atlas", false, true)]
    [DataRow("Atlas", true, true)]
    public async Task RepositoryReopenQueryConflictAndDelete(string engine, bool protobuf, bool weakCache)
    {
        var scratch = OperatingSystem.IsWindows() ? @"C:\Temp\InterfaceDBProtobufTests" : Path.Combine(Path.GetTempPath(), "InterfaceDBProtobufTests");
        var root = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            long revision;
            await using (var provider = Provider(root, engine, protobuf, weakCache))
            {
                var repo = provider.GetRequiredKeyedService<IRepository<IRecord>>("test");
                await repo.InitializeAsync();
                var saved = await repo.SaveAsync(new Record { Name = "saved", Score = 17, Secret = "protected" });
                revision = saved.Envelope!.RevisionTimestampUtcMicroseconds;
                var stale = await repo.QuerySingleAsync(x => x.Score == 17);
                saved.Score = 18;
                await repo.SaveAsync(saved);
                await Assert.ThrowsExactlyAsync<WriteConflictException<IRecord>>(() => repo.SaveAsync(stale).AsTask());
            }
            await using (var provider = Provider(root, engine, protobuf, weakCache))
            {
                var repo = provider.GetRequiredKeyedService<IRepository<IRecord>>("test");
                await repo.InitializeAsync();
                var saved = await repo.QuerySingleAsync(x => x.Score == 18);
                Assert.AreEqual("protected", saved.Secret);
                Assert.IsTrue(saved.Envelope!.RevisionTimestampUtcMicroseconds > revision);
                var detached = await repo.QuerySingleAsync(x => x.Score == 18);
                detached.Score = 999;
                Assert.AreEqual(18, (await repo.QuerySingleAsync(x => x.Score == 18)).Score);
                await repo.DeleteAsync(saved);
                Assert.IsNull(await repo.QueryFirstAsync(_ => true));
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath(scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid owned test path.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
    }

    private static ServiceProvider Provider(string root, string engine, bool protobuf, bool weakCache)
    {
        var services = new ServiceCollection();
        var encryptor = new AesGcmEncryptor(new Keys());
        IRevisionObjectSerializer<IRecord> serializer = protobuf ? Create() : new BinaryObjectSerializer<IRecord>(runtimeTypeAllowlist: new(typeof(Record), typeof(OtherRecord)));
        services.AddKeyedSingleton<IObjectSerializer<IRecord>>("test", new EncryptingObjectSerializer<IRecord>(serializer, encryptor, 1024 * 1024));
        var config = new Config(root, engine, weakCache);
        if (engine == "File") services.AddKeyedSingleton<IEncryptor>("test", encryptor).AddFileRepository<IRecord>(config);
        else if (engine == "SegmentCore") services.AddSegmentCoreRepository<IRecord>(config, encryptor: encryptor);
        else Idb.Storage.AtlasCore.AtlasCoreServiceCollectionExtensions.AddAtlasCoreRepository<IRecord>(services, config, encryptor: encryptor);
        return services.BuildServiceProvider();
    }

    private sealed class RotatingKeys : IRotatingKeyProvider
    {
        private readonly byte[] _old = Enumerable.Repeat((byte)1, 32).ToArray();
        private readonly byte[] _new = Enumerable.Repeat((byte)2, 32).ToArray();
        public string Active { get; set; } = "old";
        public string GetActiveKeyId() => Active;
        public ReadOnlyMemory<byte> GetCurrentKey() => GetKey(Active);
        public ReadOnlyMemory<byte> GetKey(string id) => id switch
        {
            "old" => _old, "new" => _new, _ => throw new CryptographicException("Unknown test key.")
        };
    }

    private sealed class Keys : IKeyProvider
    { public ReadOnlyMemory<byte> GetCurrentKey() => Enumerable.Repeat((byte)19, 32).ToArray(); }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }

    private sealed class Config(string root, string engine, bool weakCache) : IDbContractConfigDto<IRecord>
    {
        public string EnvelopeDICacheKey => "test";
        public string ContractName => "Record";
        public string FileExtension => ".pb";
        public string? ImplementationsPath => null;
        public string? ContractPath => null;
        public string? MigrationsPath => null;
        public string? DataRoot => root;
        public string? ActiveVersion => "1.0.0";
        public ContractRepositoryType RepositoryType => engine == "File" ? ContractRepositoryType.FileSystem : engine == "Atlas" ? ContractRepositoryType.AtlasCore : ContractRepositoryType.SegmentCore;
        public StoragePolicy? StoragePolicy => new() { ObjectCacheMode = weakCache ? StorageObjectCacheMode.WeakObject : StorageObjectCacheMode.StrongObject };
    }
}

[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IRecord : IStoredContract<IRecord>
{
    string? Name { get; set; }
    [SearchIndex] int Score { get; set; }
    [EncryptedStorageField] string? Secret { get; set; }
}
public sealed class Record : IRecord
{
    public string? Name { get; set; }
    public int Score { get; set; }
    public string? Secret { get; set; }
    public decimal Amount { get; set; }
    public DateTime When { get; set; }
    public IObjectEnvelope<IRecord>? Envelope { get; set; }
}
public sealed class OtherRecord : IRecord
{
    public string? Name { get; set; }
    public int Score { get; set; }
    public string? Secret { get; set; }
    public IObjectEnvelope<IRecord>? Envelope { get; set; }
}

public sealed class CountedRecord : IRecord
{
    public static int Constructions;
    public CountedRecord() => Interlocked.Increment(ref Constructions);
    public string? Name { get; set; }
    public int Score { get; set; }
    public string? Secret { get; set; }
    public IObjectEnvelope<IRecord>? Envelope { get; set; }
}
public sealed class TreeRecord : IRecord
{
    public TreeRecord? Child { get; set; }
    public string? Name { get; set; }
    public int Score { get; set; }
    public string? Secret { get; set; }
    public IObjectEnvelope<IRecord>? Envelope { get; set; }
}
