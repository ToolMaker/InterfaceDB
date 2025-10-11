using System.Collections.Generic;
using System.Linq;
using RepoTcpHost.Configuration;
using RepoTcpHost.Messaging;
using RepoTcpHost.Serialization;
using RepoTcpHost.Transport;

namespace RepoTcpHost.Tests;

public class FrameReassemblerTests
{
    [Fact]
    public void ReassemblesFramesAcrossBoundaries()
    {
        var reassembler = new FrameReassembler();
        var serializer = new MsgPackSerializer(new SerializationConfig());
        var envelope = new MsgEnvelope { Command = "PING", CorrelationId = Guid.NewGuid() };
        var payload = serializer.Pack(new { answer = 42 });
        var framed = TransportCodec.SerializeFramed(serializer, envelope, payload.Span);

        var part1 = framed[..6];
        var part2 = framed[6..];

        var firstPass = reassembler.Feed(part1);
        Assert.Empty(firstPass);

        var frames = reassembler.Feed(part2);
        Assert.Single(frames);

        var (env, body) = TransportCodec.DeserializeFrame(serializer, frames[0].Span);
        Assert.Equal(envelope.Command, env.Command);
        var unpacked = serializer.Unpack<Dictionary<string, int>>(body);
        Assert.Equal(42, unpacked["answer"]);
    }

    [Fact]
    public void ProcessesMultipleFramesInSingleFeed()
    {
        var reassembler = new FrameReassembler();
        var serializer = new MsgPackSerializer(new SerializationConfig());
        var frames = Enumerable.Range(0, 3)
            .Select(i => TransportCodec.SerializeFramed(serializer,
                new MsgEnvelope { Command = "PING", CorrelationId = Guid.NewGuid() },
                serializer.Pack(new { index = i }).Span))
            .ToArray();

        var concatenated = frames.SelectMany(f => f).ToArray();
        var result = reassembler.Feed(concatenated);
        Assert.Equal(3, result.Count);
    }
}
