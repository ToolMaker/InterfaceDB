using MessagePack;

namespace RepoTcpHost.Messaging;

[MessagePackObject]
public sealed class MsgFrame
{
    [Key(0)] public MsgEnvelope Envelope { get; set; } = new();
    [Key(1)] public byte[] Payload { get; set; } = Array.Empty<byte>();
}
