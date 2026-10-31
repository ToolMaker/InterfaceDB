using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Idb.Transport;
using MessagePack;

namespace InterfaceDB.Transport.Tests;

[TestClass]
public sealed class FrameCodecTests
{
    private static readonly TransportLimits Limits = new()
    {
        MaximumFrameBytes = 16 * 1024, MaximumPayloadBytes = 8192,
        MaximumKeyBytes = 64, MaximumBatchItems = 4, MaximumPageItems = 8
    };

    [TestMethod]
    public async Task FragmentedAndCoalescedFramesPreserveOpaqueBytesAndCorrelation()
    {
        var expected = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        var first = Request(new() { Command = EncodedStoreCommand.Put, Key = "café", Payload = expected })
            with { CorrelationId = long.MaxValue };
        var second = Request(new() { Command = EncodedStoreCommand.Ping }) with { CorrelationId = 2 };
        var frames = MessagePackFrameCodec.Encode(first, Limits).Concat(MessagePackFrameCodec.Encode(second, Limits)).ToArray();
        await using var input = new FragmentedStream(frames, 3);
        var decodedFirst = await MessagePackFrameCodec.ReadAsync(input, Limits);
        var decodedSecond = await MessagePackFrameCodec.ReadAsync(input, Limits);
        Assert.IsNotNull(decodedFirst);
        Assert.AreEqual(long.MaxValue, decodedFirst.CorrelationId);
        Assert.AreEqual("café", decodedFirst.Request!.Key);
        CollectionAssert.AreEqual(expected, decodedFirst.Request.Payload);
        Assert.AreEqual(2, decodedSecond!.CorrelationId);
        Assert.AreEqual(EncodedStoreCommand.Ping, decodedSecond.Request!.Command);
        Assert.IsNull(await MessagePackFrameCodec.ReadAsync(input, Limits));
    }

    [TestMethod]
    public async Task HandshakeManifestAndLimitsRoundTrip()
    {
        var expected = new WireMessage
        {
            Kind = WireMessageKind.Hello, StoreAlias = "store_42-test", Manifest = Manifest(),
            Limits = Limits, Capabilities = EncodedStoreCapabilities.All
        };
        var decoded = await RoundTrip(expected);
        Assert.AreEqual(expected, decoded);
    }

    [TestMethod]
    public async Task BatchDeletionAndEmptyPayloadRemainDistinct()
    {
        var decoded = await RoundTrip(Request(new()
        {
            Command = EncodedStoreCommand.ApplyBatch,
            Mutations = [new("empty", []), new("deleted", null)]
        }));
        Assert.IsNotNull(decoded.Request!.Mutations![0].Payload);
        Assert.AreEqual(0, decoded.Request.Mutations[0].Payload!.Length);
        Assert.IsNull(decoded.Request.Mutations[1].Payload);
    }

    [TestMethod]
    public async Task GetManyAndPagedResponseRoundTrip()
    {
        var getMany = await RoundTrip(Request(new() { Command = EncodedStoreCommand.GetMany, Keys = ["a", "b"] }));
        CollectionAssert.AreEqual(new[] { "a", "b" }, getMany.Request!.Keys);
        var response = await RoundTrip(new()
        {
            Kind = WireMessageKind.Response, CorrelationId = 7,
            Response = new() { Records = [new("a", [0, 1]), new("b", [])], NextAfterKey = "b", AffectedCount = 2 }
        });
        Assert.AreEqual("b", response.Response!.NextAfterKey);
        Assert.AreEqual(2, response.Response.Records!.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 1 }, response.Response.Records[0].Payload);
        var enumeration = await RoundTrip(Request(new() { Command = EncodedStoreCommand.Enumerate, AfterKey = "b", PageSize = 8 }));
        Assert.AreEqual("b", enumeration.Request!.AfterKey);
        Assert.AreEqual(8, enumeration.Request.PageSize);
    }

    [TestMethod]
    public async Task WriteConvenienceProducesTheSameCompleteFrame()
    {
        var message = Request(new() { Command = EncodedStoreCommand.Get, Key = "value" });
        await using var output = new MemoryStream();
        await MessagePackFrameCodec.WriteAsync(output, message, Limits);
        CollectionAssert.AreEqual(MessagePackFrameCodec.Encode(message, Limits), output.ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(43)]
    [DataRow(44)]
    [DataRow(47)]
    public async Task PartialHeaderOrBodyIsNeverCleanEof(int retainedBytes)
    {
        var frame = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits);
        await Reject(frame[..retainedBytes], RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task InvalidMagicVersionOrReservedHeaderIsRejected(int byteOffset)
    {
        var frame = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits);
        frame[byteOffset] ^= 0xff;
        await Reject(frame, RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public async Task OversizedHeaderIsRejectedBeforeBodyIsRead()
    {
        var header = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits)[..44];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), uint.MaxValue);
        await Reject(header, RemoteErrorCode.LimitExceeded);
    }

    [TestMethod]
    public async Task HelloFrameCeilingRejectsDeclaredBodyBeforeBodyIsRead()
    {
        var operationalLimits = new TransportLimits
        {
            MaximumFrameBytes = 128 * 1024, MaximumPayloadBytes = 64 * 1024,
            MaximumKeyBytes = 64, MaximumBatchItems = 4, MaximumPageItems = 8
        };
        var header = MessagePackFrameCodec.Encode(
            Request(new() { Command = EncodedStoreCommand.Ping }), operationalLimits)[..MessagePackFrameCodec.HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4),
            (uint)(TransportValidation.MaximumHelloFrameBytes - MessagePackFrameCodec.HeaderBytes + 1));
        await using var input = new MemoryStream(header);

        var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => MessagePackFrameCodec.ReadAsync(
            input, operationalLimits, TransportValidation.MaximumHelloFrameBytes).AsTask());

        Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
    }

    [TestMethod]
    [DataRow((int)WireMessageKind.Close)] [DataRow((int)WireMessageKind.Changed)]
    public async Task SubscriptionControlFramesHaveTheirOwnSmallBudget(int messageKind)
    {
        var kind = (WireMessageKind)messageKind;
        var valid = MessagePackFrameCodec.Encode(new() { Kind = kind, CorrelationId = 2 }, Limits);
        await using (var stream = new MemoryStream(valid))
            Assert.AreEqual(kind, (await MessagePackFrameCodec.ReadSubscriptionControlAsync(stream, Limits))!.Kind);
        var header = valid[..MessagePackFrameCodec.HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), MessagePackFrameCodec.MaximumSubscriptionControlFrameBytes);
        await using var input = new MemoryStream(header);
        var failure = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => MessagePackFrameCodec.ReadSubscriptionControlAsync(input, Limits).AsTask());
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, failure.Code);
    }

    [TestMethod]
    public async Task EmptyBodyAndCorruptChecksumAreRejected()
    {
        await Reject(Frame([]), RemoteErrorCode.ProtocolError);
        var frame = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits);
        frame[^1] ^= 1;
        await Reject(frame, RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public async Task TrailingMessagePackObjectIsRejected()
    {
        var frame = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits);
        await Reject(Frame(frame[44..].Concat(new byte[] { 0xc0 }).ToArray()), RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    [DataRow(0x90)]
    [DataRow(0x9b)]
    [DataRow(0x80)]
    [DataRow(0xc1)]
    public async Task InvalidRootShapeIsRejected(int rootCode)
    {
        await Reject(Frame([(byte)rootCode]), RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public async Task HugeNestedCollectionHeadersAreRejectedWithoutAllocation()
    {
        var keys = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer);
            writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.GetMany);
            writer.WriteNil(); writer.WriteNil(); writer.WriteArrayHeader(100_000_000);
        });
        await Reject(keys, RemoteErrorCode.LimitExceeded);
        var records = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteNil();
            writer.WriteArrayHeader(5); writer.Write(false); writer.WriteNil(); writer.WriteArrayHeader(100_000_000);
        });
        await Reject(records, RemoteErrorCode.LimitExceeded);
    }

    [TestMethod]
    public async Task DeclaredHugeStringAndBinaryAreRejectedWithoutAllocatingTheirLength()
    {
        var hugeString = MessageBody((ref MessagePackWriter writer) =>
        {
            writer.WriteArrayHeader(14); writer.Write(1); writer.Write(0);
            writer.WriteRaw(new byte[] { 0xdb, 0x7f, 0xff, 0xff, 0xff });
        });
        await Reject(hugeString, RemoteErrorCode.ProtocolError);
        var hugeBinary = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Put); writer.Write("key");
            writer.WriteRaw(new byte[] { 0xc6, 0x7f, 0xff, 0xff, 0xff });
        });
        await Reject(hugeBinary, RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public async Task CompleteOversizedStringsAndBinariesAreRejected()
    {
        var key = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Get);
            writer.Write(new string('x', Limits.MaximumKeyBytes + 1));
        });
        await Reject(key, RemoteErrorCode.LimitExceeded);
        var binary = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Put); writer.Write("key");
            writer.Write(new byte[Limits.MaximumPayloadBytes + 1]);
        });
        await Reject(binary, RemoteErrorCode.LimitExceeded);
    }

    [TestMethod]
    public async Task InvalidUtf8AndStringEncodedAsBinaryPayloadAreRejected()
    {
        var utf8 = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Get);
            writer.WriteRaw(new byte[] { 0xa2, 0xc0, 0xaf });
        });
        await Reject(utf8, RemoteErrorCode.ProtocolError);
        var stringPayload = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Put);
            writer.Write("key"); writer.Write("not-binary");
        });
        await Reject(stringPayload, RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public async Task InvalidEnumsAndCanonicalRequestShapeAreRejected()
    {
        var valid = MessagePackFrameCodec.Encode(Request(new() { Command = EncodedStoreCommand.Ping }), Limits)[44..];
        valid[1] = 127;
        await Reject(Frame(valid), RemoteErrorCode.ProtocolError);
        var unknownCommand = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write(127);
            writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.Write(0);
            Suffix(ref writer);
        });
        await Reject(unknownCommand, RemoteErrorCode.ProtocolError);
        var unexpectedField = MessageBody((ref MessagePackWriter writer) =>
        {
            Prefix(ref writer); writer.WriteArrayHeader(7); writer.Write((int)EncodedStoreCommand.Ping);
            writer.Write("unexpected"); writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.Write(0);
            Suffix(ref writer);
        });
        await Reject(unexpectedField, RemoteErrorCode.ProtocolError);
    }

    [TestMethod]
    public void AggregateEncodingStopsAtTheFrameBudget()
    {
        var message = Request(new()
        {
            Command = EncodedStoreCommand.ApplyBatch,
            Mutations = [new("a", new byte[8192]), new("b", new byte[8192])]
        });
        var exception = Assert.ThrowsExactly<RemoteStoreException>(() => MessagePackFrameCodec.Encode(message, Limits));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
    }

    [TestMethod]
    public async Task ExactFrameBudgetIsAcceptedAndOneMoreByteIsRejected()
    {
        var message = Request(new()
        {
            Command = EncodedStoreCommand.ApplyBatch,
            Mutations = [new("a", new byte[8000]), new("b", new byte[8000])]
        });
        var initial = MessagePackFrameCodec.Encode(message, Limits);
        var remaining = Limits.MaximumFrameBytes - initial.Length;
        var exact = message with
        {
            Request = message.Request! with
            {
                Mutations = [new("a", new byte[8000 + remaining / 2]), new("b", new byte[8000 + remaining - remaining / 2])]
            }
        };
        var encoded = MessagePackFrameCodec.Encode(exact, Limits);
        Assert.AreEqual(Limits.MaximumFrameBytes, encoded.Length);
        await using var input = new MemoryStream(encoded);
        Assert.IsNotNull(await MessagePackFrameCodec.ReadAsync(input, Limits));
        var tooLarge = exact with
        {
            Request = exact.Request! with
            {
                Mutations = [new("a", new byte[8001 + remaining / 2]), new("b", new byte[8000 + remaining - remaining / 2])]
            }
        };
        var exception = Assert.ThrowsExactly<RemoteStoreException>(() => MessagePackFrameCodec.Encode(tooLarge, Limits));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
    }

    [TestMethod]
    public async Task DeclaredFrameBudgetIncludesTheHeader()
    {
        var header = Frame([]);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), (uint)(Limits.MaximumFrameBytes - 43));
        await Reject(header, RemoteErrorCode.LimitExceeded);
    }

    [TestMethod]
    public void OversizedControlValuesAreRejectedBeforeEncoding()
    {
        var message = new WireMessage
        {
            Kind = WireMessageKind.Hello, Manifest = Manifest() with { ApiVersion = new string('a', 1025) }
        };
        var exception = Assert.ThrowsExactly<RemoteStoreException>(() => MessagePackFrameCodec.Encode(message, Limits));
        Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
    }

    [TestMethod]
    public void OutgoingLimitsAndInvalidTextAreRejected()
    {
        foreach (var request in new EncodedStoreRequest[]
        {
            new() { Command = EncodedStoreCommand.Put, Key = "a", Payload = new byte[8193] },
            new() { Command = EncodedStoreCommand.Get, Key = new string('é', 33) },
            new() { Command = EncodedStoreCommand.GetMany, Keys = ["a", "b", "c", "d", "e"] },
            new() { Command = EncodedStoreCommand.Enumerate, PageSize = 9 }
        })
        {
            var exception = Assert.ThrowsExactly<RemoteStoreException>(() => MessagePackFrameCodec.Encode(Request(request), Limits));
            Assert.AreEqual(RemoteErrorCode.LimitExceeded, exception.Code);
        }
        var invalid = Assert.ThrowsExactly<RemoteStoreException>(() => MessagePackFrameCodec.Encode(
            Request(new() { Command = EncodedStoreCommand.Get, Key = "\ud800" }), Limits));
        Assert.AreEqual(RemoteErrorCode.ProtocolError, invalid.Code);
    }

    [TestMethod]
    public async Task CancellationIsPreserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => MessagePackFrameCodec.ReadAsync(stream, Limits, cancellation.Token).AsTask());
    }

    private static WireMessage Request(EncodedStoreRequest request) => new()
    {
        Kind = WireMessageKind.Request, CorrelationId = 1, Request = request, TimeoutMilliseconds = 30_000
    };

    private static CompatibilityManifest Manifest() => new()
    {
        ApiVersion = "1", StorageEngine = "segment", StorageFormat = "1", EnvelopeFormat = "1",
        ContractIdentity = "example", ContractVersion = "1", ContractSchemaHash = "schema",
        ImplementationManifestHash = "implementation", IndexDefinitionHash = "index", ApprovedBuildIdentity = "build"
    };

    private static async Task<WireMessage> RoundTrip(WireMessage message)
    {
        await using var input = new MemoryStream(MessagePackFrameCodec.Encode(message, Limits));
        return (await MessagePackFrameCodec.ReadAsync(input, Limits))!;
    }

    private static async Task Reject(byte[] frame, RemoteErrorCode error)
    {
        await using var input = new MemoryStream(frame);
        var exception = await Assert.ThrowsExactlyAsync<RemoteStoreException>(() => MessagePackFrameCodec.ReadAsync(input, Limits).AsTask());
        Assert.AreEqual(error, exception.Code);
    }

    private delegate void BodyWriter(ref MessagePackWriter writer);

    private static byte[] MessageBody(BodyWriter write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        write(ref writer);
        writer.Flush();
        return Frame(buffer.WrittenSpan.ToArray());
    }

    private static void Prefix(ref MessagePackWriter writer)
    {
        writer.WriteArrayHeader(14); writer.Write((int)WireMessageKind.Request); writer.Write(1L);
        writer.WriteNil(); writer.WriteNil(); writer.WriteNil(); writer.Write(0);
    }

    private static void Suffix(ref MessagePackWriter writer)
    {
        writer.WriteNil(); writer.Write(0); writer.Write(30_000); writer.WriteNil(); writer.Write(0); writer.WriteNil(); writer.WriteNil();
    }

    private static byte[] Frame(byte[] body)
    {
        var frame = new byte[44 + body.Length];
        "IDBT"u8.CopyTo(frame); frame[4] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), (uint)body.Length);
        SHA256.HashData(body, frame.AsSpan(12, 32)); body.CopyTo(frame, 44);
        return frame;
    }

    private sealed class FragmentedStream(byte[] bytes, int maximumRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
    }
}
