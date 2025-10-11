using System.Buffers.Binary;
using RepoTcpHost.Messaging;

namespace RepoTcpHost.Serialization;

public static class TransportCodec
{
    public static byte[] SerializeFramed(ISerializer serializer, MsgEnvelope envelope, ReadOnlySpan<byte> payload)
    {
        var message = new MsgFrame
        {
            Envelope = envelope,
            Payload = payload.Length == 0 ? Array.Empty<byte>() : payload.ToArray()
        };

        var bodyMemory = serializer.Pack(message);
        var body = bodyMemory.Length == 0 ? Array.Empty<byte>() : bodyMemory.ToArray();
        var framed = new byte[sizeof(uint) + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(0, sizeof(uint)), (uint)body.Length);
        body.CopyTo(framed.AsSpan(sizeof(uint)));
        return framed;
    }

    public static (MsgEnvelope Envelope, byte[] Payload) DeserializeFrame(ISerializer serializer, ReadOnlySpan<byte> frame)
    {
        var message = serializer.Unpack<MsgFrame>(frame);
        return (message.Envelope, message.Payload ?? Array.Empty<byte>());
    }
}
