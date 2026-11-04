using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Commercial.Repositories.Peers;

namespace Idb.Transport.Peers;

/// <summary>Bounded control framing plus an unmodified object payload. Never invokes an entity serializer.</summary>
internal static class PeerFrameCodec
{
    private const int MaximumHeaderBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static async ValueTask WriteAsync(Stream stream, PeerMessage message, PeerLimits limits, CancellationToken token)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var payload = message.Payload;
        if (header.Length > MaximumHeaderBytes || payload?.Length > limits.MaximumPayloadBytes) throw new PeerProtocolException(PeerError.LimitExceeded);
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(prefix, header.Length);
        BinaryPrimitives.WriteInt32BigEndian(prefix.AsSpan(4), payload?.Length ?? -1);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (payload is { Length: > 0 }) await stream.WriteAsync(payload, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async ValueTask<PeerMessage> ReadAsync(Stream stream, PeerLimits limits, CancellationToken token)
    {
        var prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(prefix);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(4));
        if (headerLength is < 2 or > MaximumHeaderBytes || payloadLength < -1 || payloadLength > limits.MaximumPayloadBytes)
            throw new PeerProtocolException(PeerError.LimitExceeded);
        var header = new byte[headerLength];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<PeerMessage>(header, Json) ?? throw new InvalidDataException("Missing peer frame.");
        if (!Enum.IsDefined(message.Command) || !Enum.IsDefined(message.Error)) throw new InvalidDataException("Unknown peer command.");
        byte[]? payload = payloadLength == 0 ? [] : null;
        if (payloadLength > 0)
        {
            payload = new byte[payloadLength];
            await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        }
        return message with { Payload = payload };
    }
}
