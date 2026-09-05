using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class BinaryObjectSerializerExtensionMetadataTests
{
    private static readonly byte[] Metadata = [0x01, 0x23, 0x45, 0x67, 0x89];
    private static readonly MessagePackSerializerOptions LegacyOptions =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    [TestMethod]
    [DataRow(0)]
    [DataRow(8192)]
    public async Task PooledSerialize_ProducesTheExistingStreamingWireFormat(int valueLength)
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var envelope = CreateEnvelope(Metadata);
        envelope.ObjectInstance!.Value = new string('x', valueLength);
        await using var stream = new MemoryStream();
        await serializer.SerializeAsync(envelope, stream);

        CollectionAssert.AreEqual(stream.ToArray(), serializer.Serialize(envelope));
    }

    [TestMethod]
    public void DeserializeMemory_UsesOnlyTheSliceAndDetachesAllStandardBinaryValues()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var envelope = CreateEnvelope(Metadata);
        envelope.ObjectInstance!.Bytes = [10, 20, 30];
        envelope.ObjectInstance.Memory = new byte[] { 40, 50, 60 };
        envelope.ObjectInstance.ReadOnlyMemory = new byte[] { 70, 80, 90 };
        var payload = serializer.Serialize(envelope);
        var borrowed = new byte[payload.Length + 32];
        payload.CopyTo(borrowed, 16);

        var restored = serializer.DeserializeMemory(borrowed.AsMemory(16, payload.Length));
        borrowed.AsSpan().Fill(0xCC);

        AssertRecord(restored);
        CollectionAssert.AreEqual(Metadata, restored.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30 }, restored.ObjectInstance!.Bytes);
        CollectionAssert.AreEqual(new byte[] { 40, 50, 60 }, restored.ObjectInstance.Memory.ToArray());
        CollectionAssert.AreEqual(new byte[] { 70, 80, 90 }, restored.ObjectInstance.ReadOnlyMemory.ToArray());
    }

    [TestMethod]
    public void DeserializeMemory_RejectsTruncatedObjectsAndEmptyInput()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var payload = serializer.Serialize(CreateEnvelope(Metadata));

        Assert.ThrowsExactly<ArgumentException>(() => serializer.DeserializeMemory(ReadOnlyMemory<byte>.Empty));
        Assert.ThrowsExactly<MessagePackSerializationException>(() =>
            serializer.DeserializeMemory(payload.AsMemory(0, payload.Length - 1)));
    }

    [TestMethod]
    public void Serialize_RoundTripsNonNullExtensionMetadata()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var payload = serializer.Serialize(CreateEnvelope(Metadata));

        var restored = serializer.Deserialize(payload);

        Assert.IsTrue(payload.AsSpan(0, 4).SequenceEqual("IDB5"u8));
        CollectionAssert.AreEqual(Metadata, restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    public void Serialize_RoundTripsNullExtensionMetadata()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();

        var restored = serializer.Deserialize(serializer.Serialize(CreateEnvelope(extensionMetadata: null)));

        Assert.IsNull(restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    public async Task SerializeAsync_RoundTripsNonNullExtensionMetadata()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        await using var stream = new MemoryStream();
        await serializer.SerializeAsync(CreateEnvelope(Metadata), stream);
        Assert.IsTrue(stream.GetBuffer().AsSpan(0, 4).SequenceEqual("IDB5"u8));
        stream.Position = 0;

        var restored = await serializer.DeserializeAsync(stream);

        CollectionAssert.AreEqual(Metadata, restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    public async Task SerializeAsync_RoundTripsNullExtensionMetadata()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        await using var stream = new MemoryStream();
        await serializer.SerializeAsync(CreateEnvelope(extensionMetadata: null), stream);
        stream.Position = 0;

        var restored = await serializer.DeserializeAsync(stream);

        Assert.IsNull(restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    public void Serialize_RoundTripsExtensionMetadataAtThePublicLimit()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var metadata = new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength];
        metadata[0] = 0x11;
        metadata[^1] = 0x22;

        var restored = serializer.Deserialize(serializer.Serialize(CreateEnvelope(metadata)));

        Assert.HasCount(EnvelopeExtensionMetadataLimits.MaximumByteLength, restored.ExtensionMetadata!);
        Assert.AreEqual(0x11, restored.ExtensionMetadata![0]);
        Assert.AreEqual(0x22, restored.ExtensionMetadata[^1]);
    }

    [TestMethod]
    public void EncryptingSerializer_RoundTripsExtensionMetadata()
    {
        var serializer = new EncryptingObjectSerializer<SerializerRecord>(
            new BinaryObjectSerializer<SerializerRecord>(),
            new DeterministicTestEncryptor());

        var payload = serializer.Serialize(CreateEnvelope(Metadata));
        var restored = serializer.Deserialize(payload);

        Assert.AreEqual(DeterministicTestEncryptor.Marker, payload[0]);
        CollectionAssert.AreEqual(Metadata, restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    [DataRow("IDB2")]
    [DataRow("IDB3")]
    [DataRow("IDB4")]
    public void Deserialize_LegacyEnvelopeDefaultsExtensionMetadataToNull(string magic)
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();

        var restored = serializer.Deserialize(CreateLegacyEnvelope(magic));

        Assert.IsNull(restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    [DataRow("IDB2")]
    [DataRow("IDB3")]
    [DataRow("IDB4")]
    public async Task DeserializeAsync_LegacyEnvelopeDefaultsExtensionMetadataToNull(string magic)
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        await using var stream = new MemoryStream(CreateLegacyEnvelope(magic), writable: false);

        var restored = await serializer.DeserializeAsync(stream);

        Assert.IsNull(restored.ExtensionMetadata);
        AssertRecord(restored);
    }

    [TestMethod]
    public void Serialize_RejectsExtensionMetadataAboveThePublicLimit()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var envelope = CreateEnvelope(
            new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            serializer.Serialize(envelope));

        StringAssert.Contains(exception.ToString(), EnvelopeExtensionMetadataLimits.MaximumByteLength.ToString());
    }

    [TestMethod]
    public async Task SerializeAsync_RejectsExtensionMetadataAboveThePublicLimit()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var envelope = CreateEnvelope(
            new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]);
        await using var stream = new MemoryStream();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            serializer.SerializeAsync(envelope, stream).AsTask());

        StringAssert.Contains(exception.ToString(), EnvelopeExtensionMetadataLimits.MaximumByteLength.ToString());
    }

    [TestMethod]
    public void Deserialize_RejectsPersistedExtensionMetadataAboveThePublicLimit()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        var payload = CreateIdb5Envelope(
            new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]);

        var exception = Assert.ThrowsExactly<MessagePackSerializationException>(() =>
            serializer.Deserialize(payload));

        StringAssert.Contains(exception.ToString(), EnvelopeExtensionMetadataLimits.MaximumByteLength.ToString());
    }

    [TestMethod]
    public async Task DeserializeAsync_RejectsPersistedExtensionMetadataAboveThePublicLimit()
    {
        var serializer = new BinaryObjectSerializer<SerializerRecord>();
        await using var stream = new MemoryStream(
            CreateIdb5Envelope(new byte[EnvelopeExtensionMetadataLimits.MaximumByteLength + 1]),
            writable: false);

        var exception = await Assert.ThrowsExactlyAsync<MessagePackSerializationException>(() =>
            serializer.DeserializeAsync(stream).AsTask());

        StringAssert.Contains(exception.ToString(), EnvelopeExtensionMetadataLimits.MaximumByteLength.ToString());
    }

    private static FileObjectEnvelope<SerializerRecord> CreateEnvelope(byte[]? extensionMetadata)
        => new()
        {
            EnvelopeCacheKey = "record-001",
            VersionTag = "1.0.0",
            ExtensionMetadata = extensionMetadata?.ToArray(),
            ObjectInstance = new SerializerRecord
            {
                Id = "record-001",
                Value = "stored-value"
            },
            Location = "record-001.mpk"
        };

    private static byte[] CreateLegacyEnvelope(string magic)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(magic));
        MessagePackSerializer.Serialize(stream, "1.0.0", LegacyOptions);
        MessagePackSerializer.Serialize(stream, "record-001", LegacyOptions);
        MessagePackSerializer.Serialize(
            stream,
            typeof(SerializerRecord).AssemblyQualifiedName!,
            LegacyOptions);

        if (magic is "IDB3" or "IDB4")
        {
            MessagePackSerializer.Serialize(stream, true, LegacyOptions);
        }

        if (magic == "IDB4")
        {
            MessagePackSerializer.Serialize(
                stream,
                new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal),
                LegacyOptions);
        }

        MessagePackSerializer.Serialize(
            stream,
            new SerializerRecord
            {
                Id = "record-001",
                Value = "stored-value"
            },
            LegacyOptions);
        return stream.ToArray();
    }

    private static byte[] CreateIdb5Envelope(byte[] extensionMetadata)
    {
        using var stream = new MemoryStream();
        stream.Write("IDB5"u8);
        MessagePackSerializer.Serialize(stream, "1.0.0", LegacyOptions);
        MessagePackSerializer.Serialize(stream, "record-001", LegacyOptions);
        MessagePackSerializer.Serialize(
            stream,
            typeof(SerializerRecord).AssemblyQualifiedName!,
            LegacyOptions);
        MessagePackSerializer.Serialize(stream, true, LegacyOptions);
        MessagePackSerializer.Serialize(
            stream,
            new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal),
            LegacyOptions);
        MessagePackSerializer.Serialize(stream, extensionMetadata, LegacyOptions);
        MessagePackSerializer.Serialize(
            stream,
            new SerializerRecord
            {
                Id = "record-001",
                Value = "stored-value"
            },
            LegacyOptions);
        return stream.ToArray();
    }

    private static void AssertRecord(IObjectEnvelope<SerializerRecord> envelope)
    {
        Assert.IsNotNull(envelope.ObjectInstance);
        Assert.AreEqual("record-001", envelope.EnvelopeCacheKey);
        Assert.AreEqual("1.0.0", envelope.VersionTag);
        Assert.AreEqual("record-001", envelope.ObjectInstance.Id);
        Assert.AreEqual("stored-value", envelope.ObjectInstance.Value);
    }

    [MessagePackObject(true)]
    public sealed class SerializerRecord : IStoredContract<SerializerRecord>
    {
        public string Id { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public byte[] Bytes { get; set; } = [];

        public Memory<byte> Memory { get; set; }

        public ReadOnlyMemory<byte> ReadOnlyMemory { get; set; }

        [IgnoreMember]
        public IObjectEnvelope<SerializerRecord>? Envelope { get; private set; }
    }

    private sealed class DeterministicTestEncryptor : IEncryptor
    {
        internal const byte Marker = 0xA5;
        private const byte Mask = 0x5A;

        public byte[] Encrypt(ReadOnlySpan<byte> plain)
        {
            var cipher = new byte[plain.Length + 1];
            cipher[0] = Marker;
            for (var index = 0; index < plain.Length; index++)
            {
                cipher[index + 1] = (byte)(plain[index] ^ Mask);
            }

            return cipher;
        }

        public byte[] Decrypt(ReadOnlySpan<byte> cipher)
        {
            if (cipher.IsEmpty || cipher[0] != Marker)
            {
                throw new InvalidDataException("The test cipher marker is invalid.");
            }

            var plain = new byte[cipher.Length - 1];
            for (var index = 0; index < plain.Length; index++)
            {
                plain[index] = (byte)(cipher[index + 1] ^ Mask);
            }

            return plain;
        }
    }
}
