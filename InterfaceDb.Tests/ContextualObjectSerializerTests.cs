using System.Security.Cryptography;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class ContextualObjectSerializerTests
{
    [TestMethod]
    public void ContextualInterfacePreservesEncryptionRevisionMetadataAndWrongContextRejection()
    {
        var inner = new AlternativeSerializer();
        var encrypted = new EncryptingObjectSerializer<Record>(inner, new AesGcmEncryptor(new Keys()), 4096);
        var serializer = new ContextOnlyDecorator(encrypted);
        var envelope = Envelope();
        var payload = ContextualObjectSerialization.Serialize(serializer, envelope, "store-a:key-a"u8);
        Assert.IsTrue(payload.AsSpan().StartsWith("IDBE"u8));
        Assert.AreEqual(3, payload[4]);
        Assert.AreEqual(123L, ContextualObjectSerialization.ReadRevision(serializer, payload, "store-a:key-a"u8));
        var restored = ContextualObjectSerialization.Deserialize(serializer, payload, "store-a:key-a"u8);
        Assert.AreEqual("value", restored.ObjectInstance!.Name);
        CollectionAssert.AreEqual(new byte[] { 8, 9 }, restored.ExtensionMetadata);
        var read = ContextualObjectSerialization.DeserializeForRead(serializer, payload, "store-a:key-a"u8);
        Assert.AreEqual(123L, read.Envelope.RevisionTimestampUtcMicroseconds);
        Assert.IsNull(read.ReplacementPayload);
        Assert.ThrowsExactly<CryptographicException>(() => ContextualObjectSerialization.Deserialize(serializer, payload, "other-key"u8));
        Assert.ThrowsExactly<CryptographicException>(() => ContextualObjectSerialization.ReadRevision(serializer, payload, "other-key"u8));
        Assert.ThrowsExactly<CryptographicException>(() => ContextualObjectSerialization.DeserializeForRead(serializer, payload, "other-key"u8));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GenericEncryptionWrapperEnforcesItsOwnLimitsBeforeReadingInner(bool encrypted)
    {
        var inner = new AlternativeSerializer();
        IEncryptor encryptor = encrypted ? new AesGcmEncryptor(new Keys()) : new EncryptionNone();
        var serializer = new EncryptingObjectSerializer<Record>(inner, encryptor, 32);
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(new byte[33]));
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.ReadRevision(new byte[33]));
        await using var data = new MemoryStream(new byte[33]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => serializer.DeserializeAsync(data).AsTask());
        data.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => serializer.ReadRevisionAsync(data).AsTask());
        Assert.AreEqual(0, inner.Reads);
        await using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => serializer.SerializeAsync(Envelope(), destination).AsTask());
        Assert.AreEqual(0L, destination.Length);
    }

    [TestMethod]
    public void GenericEncryptionWrapperRequiresPositiveLimit()
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new EncryptingObjectSerializer<Record>(new AlternativeSerializer(), new EncryptionNone(), 0));

    private static FileObjectEnvelope<Record> Envelope() => new()
    {
        EnvelopeCacheKey = "key-a", VersionTag = "1.0.0", RevisionTimestampUtcMicroseconds = 123,
        ObjectInstance = new Record { Name = "value" }, ExtensionMetadata = [8, 9]
    };

    private sealed class AlternativeSerializer : IRevisionObjectSerializer<Record>
    {
        private readonly BinaryObjectSerializer<Record> _inner = new();
        public int Reads { get; private set; }
        public byte[] Serialize(IObjectEnvelope<Record> envelope) => [0xAC, .. _inner.Serialize(envelope)];
        public IObjectEnvelope<Record> Deserialize(byte[] data) { Reads++; return _inner.Deserialize(data[1..]); }
        public long ReadRevision(ReadOnlyMemory<byte> payload) { Reads++; return _inner.ReadRevision(payload[1..]); }
        public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            return ReadRevision(buffer.ToArray());
        }
    }

    // Contextless methods deliberately fail: provider helpers must dispatch through the interface.
    private sealed class ContextOnlyDecorator(IContextualObjectSerializer<Record> inner) : IContextualObjectSerializer<Record>
    {
        public IEncryptor Encryptor => inner.Encryptor;
        public byte[] Serialize(IObjectEnvelope<Record> envelope, ReadOnlySpan<byte> context) => inner.Serialize(envelope, context);
        public IObjectEnvelope<Record> Deserialize(byte[] payload, ReadOnlySpan<byte> context) => inner.Deserialize(payload, context);
        public long ReadRevision(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> context) => inner.ReadRevision(payload, context);
        public RotationAwareDeserializationResult<Record> DeserializeForRead(byte[] payload, ReadOnlySpan<byte> context) => inner.DeserializeForRead(payload, context);
        public byte[] Serialize(IObjectEnvelope<Record> envelope) => throw new AssertFailedException("Context was lost.");
        public IObjectEnvelope<Record> Deserialize(byte[] payload) => throw new AssertFailedException("Context was lost.");
        public long ReadRevision(ReadOnlyMemory<byte> payload) => throw new AssertFailedException("Context was lost.");
        public ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default) => throw new AssertFailedException("Context was lost.");
        public RotationAwareDeserializationResult<Record> DeserializeForRead(byte[] payload) => throw new AssertFailedException("Context was lost.");
    }
    private sealed class Keys : IKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public ReadOnlyMemory<byte> GetCurrentKey() => _key;
    }
    [MessagePackObject(true)]
    public sealed class Record : IStoredContract<Record>
    {
        public string Name { get; set; } = "";
        [IgnoreMember] public IObjectEnvelope<Record>? Envelope { get; set; }
    }
}
