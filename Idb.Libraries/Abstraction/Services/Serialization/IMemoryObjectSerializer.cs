using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Optional deserialization from a bounded borrowed buffer.</summary>
public interface IMemoryObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Materializes an independent envelope. Neither the envelope nor its values may retain
    /// references to <paramref name="data"/> after this method returns.
    /// </summary>
    IObjectEnvelope<TContract> DeserializeMemory(ReadOnlyMemory<byte> data);
}
