using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Dtos;

/// <summary>
/// Transport model that pairs a repository envelope with its contract instance metadata.
/// Designed to be returned by higher-level abstractions such as <see cref="Services.Serialization.IEnvelopeRepository{TContract}"/>.
/// </summary>
/// <typeparam name="TContract">Contract interface represented by the DTO payload.</typeparam>
public interface IRepoDto<TContract>
    where TContract : class
{
    /// <summary>
    /// Envelope containing the serialized payload, version tag, and file reference for the contract instance.
    /// </summary>
    IObjectEnvelope<TContract> Envelope { get; set; }
}
