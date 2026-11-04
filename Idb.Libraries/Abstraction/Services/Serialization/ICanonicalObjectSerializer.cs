using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Deterministic logical encoding for integrity comparisons across application builds.
/// Must preserve all serialized values and runtime type identity, but exclude assembly versions.
/// This encoding is not a storage payload and must not be used for deserialization.</summary>
public interface ICanonicalObjectSerializer<T> where T : class, IStoredContract<T>
{
    byte[] SerializeCanonical(IObjectEnvelope<T> envelope);
}
