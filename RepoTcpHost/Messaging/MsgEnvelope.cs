using System;
using MessagePack;

namespace RepoTcpHost.Messaging;

[MessagePackObject]
public sealed class MsgEnvelope
{
    [Key(0)] public byte Version { get; set; } = 1;
    [Key(1)] public MsgType Type { get; set; } = MsgType.Request;
    [Key(2)] public Guid CorrelationId { get; set; } = Guid.NewGuid();
    [Key(3)] public string Command { get; set; } = "CALL";
    [Key(4)] public string? Topic { get; set; }

    [Key(5)] public int Status { get; set; }
}

public enum MsgType : byte
{
    Request = 0,
    Response = 1,
    Event = 2,
    Heartbeat = 3
}
