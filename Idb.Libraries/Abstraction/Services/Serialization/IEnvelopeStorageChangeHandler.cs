using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Applies observed storage changes to the envelope cache.
/// </summary>
public interface IEnvelopeStorageChangeHandler<TContract>
    where TContract : class, IStoredContract<TContract>
{
    ValueTask<EnvelopeStorageChangeResult<TContract>> HandleAsync(
        EnvelopeStorageChange change,
        CancellationToken cancellationToken = default);
}
