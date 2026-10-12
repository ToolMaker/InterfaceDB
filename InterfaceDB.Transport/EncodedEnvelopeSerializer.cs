using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Transport;

/// <summary>
/// Reuses InterfaceDB's canonical envelope serializer at the trusted client. Callers prepare the
/// envelope lifecycle/revision and explicitly supply its stable authenticated encryption context.
/// This bridge adds no domain deserialization to the storage host and is not a repository adapter.
/// </summary>
public sealed class EncodedEnvelopeSerializer<TContract>(IObjectSerializer<TContract> serializer, int maximumPayloadBytes = int.MaxValue)
    where TContract : class, IStoredContract<TContract>
{
    private readonly IObjectSerializer<TContract> _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly int _maximumPayloadBytes = maximumPayloadBytes > 0 ? maximumPayloadBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

    private void ValidatePayload(byte[] payload)
    {
        if (payload is null || payload.Length > _maximumPayloadBytes)
            throw new InvalidDataException("The serialized envelope exceeds the repository or negotiated payload limit.");
    }

    public byte[] Serialize(string physicalKey, IObjectEnvelope<TContract> envelope, ReadOnlySpan<byte> associatedData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(physicalKey, envelope.EnvelopeCacheKey, StringComparison.Ordinal))
            throw new ArgumentException("Envelope identity must match the physical storage key.", nameof(envelope));
        var payload = ContextualObjectSerialization.Serialize(_serializer, envelope, associatedData);
        ValidatePayload(payload);
        return payload;
    }

    public IObjectEnvelope<TContract> Deserialize(string physicalKey, byte[] payload, ReadOnlySpan<byte> associatedData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload);
        var envelope = ContextualObjectSerialization.Deserialize(_serializer, payload, associatedData);
        if (!string.Equals(physicalKey, envelope.EnvelopeCacheKey, StringComparison.Ordinal))
            throw new InvalidDataException("The stored envelope identity does not match the requested key.");
        return envelope;
    }
}
