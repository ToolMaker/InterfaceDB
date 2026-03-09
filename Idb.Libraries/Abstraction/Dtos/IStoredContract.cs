using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Contract implemented by storage-facing types that carry their persistence envelope.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the stored payload.</typeparam>
public interface IStoredContract<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Envelope containing the serialized payload, version tag, and backing-location metadata for the contract instance.
    /// </summary>
    IObjectEnvelope<TContract>? Envelope { get; }
}
