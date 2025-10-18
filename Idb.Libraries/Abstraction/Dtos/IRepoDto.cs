using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Transport model that pairs a contract instance with its persistence envelope.
/// Designed to be returned by higher-level abstractions such as <see cref="Services.Serialization.IEnvelopeRepository{TContract, TLocationType}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the DTO payload.</typeparam>
/// <typeparam name="TLocationType">Descriptor used to locate the persisted payload.</typeparam>
public interface IRepoDto<TContract, TLocationType>
    where TContract : class, IRepoDto<TContract, TLocationType>
{
    /// <summary>
    /// Envelope containing the serialized payload, version tag, and backing-location metadata for the contract instance.
    /// </summary>
    IObjectEnvelope<TContract, TLocationType>? Envelope { get; set; }
}
