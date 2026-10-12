using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// An envelope serializer that preserves authenticated storage context during encryption,
/// decryption, revision reads and key rotation. Implementations are trusted application services.
/// </summary>
public interface IContextualObjectSerializer<TContract> :
    IRevisionObjectSerializer<TContract>, IRotationAwareObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>The encryption policy shared with the envelope lifecycle.</summary>
    IEncryptor Encryptor { get; }
    byte[] Serialize(IObjectEnvelope<TContract> envelope, ReadOnlySpan<byte> associatedData);
    IObjectEnvelope<TContract> Deserialize(byte[] payload, ReadOnlySpan<byte> associatedData);
    RotationAwareDeserializationResult<TContract> DeserializeForRead(byte[] payload, ReadOnlySpan<byte> associatedData);
    long ReadRevision(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> associatedData);
}
