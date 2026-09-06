using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class BinarySerializerScanTests
{
    private static FileObjectEnvelope<Record> Envelope(Record? record = null) => new()
    {
        EnvelopeCacheKey = "record-0", VersionTag = "1.2.3", RevisionTimestampUtcMicroseconds = 37,
        ObjectInstance = record ?? new Record { Id = "record", Payload = [1, 2, 3] },
        ExtensionMetadata = [4, 5],
        EncryptedFields = new Dictionary<string, EncryptedFieldValue> { ["field"] = new() { ProfileName = "test", Ciphertext = [9, 8] } }
    };

    [TestMethod]
    public async Task RepeatedHeaderReusesOnlyImmutableTypeNameAndOwnsMutableMetadata()
    {
        var serializer = new BinaryObjectSerializer<Record>();
        var bytes = serializer.Serialize(Envelope());
        var first = serializer.Deserialize(bytes);
        var next = serializer.Deserialize(bytes);
        Assert.AreSame(first.ImplementationTypeName, next.ImplementationTypeName, "Repeated header decoding must actually reuse the cached string.");
        Assert.AreNotSame(first, next);
        Assert.AreNotSame(first.ObjectInstance, next.ObjectInstance);
        Assert.AreNotSame(first.ObjectInstance!.Payload, next.ObjectInstance!.Payload);
        Assert.AreNotSame(first.EncryptedFields, next.EncryptedFields);
        Assert.AreNotSame(first.EncryptedFields["field"], next.EncryptedFields["field"]);
        first.ObjectInstance.Payload[0] = 99; first.ExtensionMetadata![0] = 99;
        first.EncryptedFields["field"].Ciphertext[0] = 99;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, next.ObjectInstance.Payload);
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, next.ExtensionMetadata);
        CollectionAssert.AreEqual(new byte[] { 9, 8 }, next.EncryptedFields["field"].Ciphertext);
        using var stream = new MemoryStream(bytes);
        var streamed = await serializer.DeserializeAsync(stream);
        Assert.AreEqual(next.ImplementationTypeName, streamed.ImplementationTypeName);
        Assert.AreEqual(37, streamed.RevisionTimestampUtcMicroseconds);
        CollectionAssert.AreEqual(next.ObjectInstance.Payload, streamed.ObjectInstance!.Payload);
        var headerName = next.ImplementationTypeName;
        Array.Fill(bytes, (byte)0); // A cached header must never retain the caller's mutable backing array.
        var fresh = serializer.Deserialize(serializer.Serialize(Envelope()));
        Assert.AreSame(headerName, fresh.ImplementationTypeName);
    }

    [TestMethod]
    public async Task ConcurrentPolymorphicReadsKeepTypesAndEnvelopeMetadataSeparate()
    {
        var serializer = new BinaryObjectSerializer<Record>();
        var a = serializer.Serialize(Envelope());
        var other = Envelope(new OtherRecord { Id = "other", Payload = [7, 6], Extra = 123 });
        other.EnvelopeCacheKey = "other-0"; other.VersionTag = "2.0.0"; other.RevisionTimestampUtcMicroseconds = 58;
        var b = serializer.Serialize(other);
        var first = serializer.Deserialize(a);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 128; i++)
            {
                bool second = (i + worker) % 2 != 0;
                var read = serializer.Deserialize(second ? b : a);
                Assert.AreEqual(second ? typeof(OtherRecord) : typeof(Record), read.ObjectInstance!.GetType());
                Assert.AreEqual(second ? "other-0" : "record-0", read.EnvelopeCacheKey);
                Assert.AreEqual(second ? "2.0.0" : "1.2.3", read.VersionTag);
                Assert.AreEqual(second ? 58 : 37, read.RevisionTimestampUtcMicroseconds);
                CollectionAssert.AreEqual(second ? new byte[] { 7, 6 } : new byte[] { 1, 2, 3 }, read.ObjectInstance.Payload);
                if (second) Assert.AreEqual(123, ((OtherRecord)read.ObjectInstance).Extra);
                else Assert.AreSame(first.ImplementationTypeName, read.ImplementationTypeName);
            }
        })));
    }

    [TestMethod]
    [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    public void AllVersionHeadersKeepTheirFieldBoundaries(int format)
    {
        var serializer = new BinaryObjectSerializer<Record>();
        var current = serializer.Serialize(Envelope());
        var reader = new MessagePackReader(current.AsMemory(12));
        int a = (int)reader.Consumed; reader.Skip(); reader.Skip(); reader.Skip(); int namesEnd = (int)reader.Consumed;
        reader.Skip(); int flagEnd = (int)reader.Consumed;
        reader.Skip(); int fieldsEnd = (int)reader.Consumed;
        reader.Skip(); int metadataEnd = (int)reader.Consumed;
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("IDB" + format));
        if (format == 6) stream.Write(current.AsSpan(4, 8));
        stream.Write(current.AsSpan(12 + a, namesEnd - a));
        if (format >= 3) stream.Write(current.AsSpan(12 + namesEnd, flagEnd - namesEnd));
        if (format >= 4) stream.Write(current.AsSpan(12 + flagEnd, fieldsEnd - flagEnd));
        if (format >= 5) stream.Write(current.AsSpan(12 + fieldsEnd, metadataEnd - fieldsEnd));
        stream.Write(current.AsSpan(12 + metadataEnd));
        for (int i = 0; i < 2; i++)
        {
            var read = serializer.Deserialize(stream.ToArray());
            Assert.AreEqual("1.2.3", read.VersionTag); Assert.AreEqual("record-0", read.EnvelopeCacheKey);
            Assert.AreEqual(format == 6 ? 37 : 0, read.RevisionTimestampUtcMicroseconds);
            Assert.AreEqual(format >= 4 ? 1 : 0, read.EncryptedFields.Count);
            if (format >= 5) CollectionAssert.AreEqual(new byte[] { 4, 5 }, read.ExtensionMetadata);
            else Assert.IsNull(read.ExtensionMetadata);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, read.ObjectInstance!.Payload);
        }
    }

    [TestMethod]
    public void WarmHeaderStillRejectsTruncationBadTypeAndNegativeRevision()
    {
        var serializer = new BinaryObjectSerializer<Record>();
        var bytes = serializer.Serialize(Envelope());
        _ = serializer.Deserialize(bytes);
        for (int length = 0; length < bytes.Length; length++)
        {
            bool threw = false;
            try { serializer.Deserialize(bytes[..length]); } catch { threw = true; }
            Assert.IsTrue(threw, $"Truncated payload length {length} was accepted.");
        }
        var invalidRevision = (byte[])bytes.Clone(); BinaryPrimitives.WriteInt64LittleEndian(invalidRevision.AsSpan(4), -1);
        Assert.ThrowsExactly<InvalidDataException>(() => serializer.Deserialize(invalidRevision));
        var reader = new MessagePackReader(bytes.AsMemory(12)); reader.Skip(); reader.Skip();
        int start = 12 + (int)reader.Consumed; reader.Skip(); int end = 12 + (int)reader.Consumed;
        foreach (var name in new string?[] { null, "", " ", "Missing.Type, Missing.Assembly", typeof(string).AssemblyQualifiedName, new string('x', 2048) })
        {
            var buffer = new ArrayBufferWriter<byte>(); var writer = new MessagePackWriter(buffer); writer.Write(name); writer.Flush();
            byte[] modified = [.. bytes.AsSpan(0, start), .. buffer.WrittenSpan, .. bytes.AsSpan(end)];
            bool threw = false; try { serializer.Deserialize(modified); } catch { threw = true; }
            Assert.IsTrue(threw, $"Invalid contract type {name} was accepted.");
        }
        Assert.AreEqual(37, serializer.Deserialize(bytes).RevisionTimestampUtcMicroseconds);
    }

    [MessagePackObject(true)]
    public class Record : IStoredContract<Record>
    {
        public string Id { get; set; } = "";
        public byte[] Payload { get; set; } = [];
        [IgnoreMember] public IObjectEnvelope<Record>? Envelope { get; set; }
    }
    [MessagePackObject(true)]
    public sealed class OtherRecord : Record { public int Extra { get; set; } }
}
