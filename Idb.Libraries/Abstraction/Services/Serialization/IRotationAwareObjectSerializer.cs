using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

public interface IRotationAwareObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    RotationAwareDeserializationResult<TContract> DeserializeForRead(byte[] data);
}

public sealed record RotationAwareDeserializationResult<TContract>(
    IObjectEnvelope<TContract> Envelope,
    byte[]? ReplacementPayload)
    where TContract : class, IStoredContract<TContract>;
