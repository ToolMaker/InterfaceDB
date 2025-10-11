using System.Collections.Generic;
using MessagePack;
using RepoTcpHost.Configuration;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;

namespace RepoTcpHost.Tests;

public class TransportCodecTests
{
    private readonly MsgPackSerializer _serializer = new(new SerializationConfig());

    [Fact]
    public void SerializeDeserializeRoundTrip()
    {
        var envelope = new MsgEnvelope
        {
            Command = "PING",
            CorrelationId = Guid.NewGuid(),
            Topic = "sample",
            Type = MsgType.Request,
            Status = 0
        };

        var payload = _serializer.Pack(new { hello = "world" });
        var framed = TransportCodec.SerializeFramed(_serializer, envelope, payload.Span);
        var slice = framed.AsSpan(sizeof(uint));
        var (deserializedEnvelope, deserializedPayload) = TransportCodec.DeserializeFrame(_serializer, slice);

        Assert.Equal(envelope.Command, deserializedEnvelope.Command);
        Assert.Equal(envelope.Topic, deserializedEnvelope.Topic);
        Assert.Equal(envelope.CorrelationId, deserializedEnvelope.CorrelationId);
        var unpacked = _serializer.Unpack<Dictionary<string, string>>(deserializedPayload.AsSpan());
        Assert.Equal("world", unpacked["hello"]);
    }

    [Fact]
    public void DeserializeRejectsInvalidHeader()
    {
        var invalid = new byte[] { 0x92, 0xA3, 0x66, 0x6F, 0x6F, 0xA3, 0x62, 0x61, 0x72 };
        Assert.Throws<MessagePackSerializationException>(() => TransportCodec.DeserializeFrame(_serializer, invalid));
    }
}

