using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Optional bounded multi-key materialization capability for envelope providers.</summary>
public interface IBatchedEnvelopeRepository<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Returns materialized envelopes in requested order, omitting missing keys. Callers supply
    /// bounded windows; providers retain their normal caching, cancellation and visibility rules.
    /// </summary>
    ValueTask<IReadOnlyList<IObjectEnvelope<TContract>>> GetPhysicalManyAsync(
        IReadOnlyList<string> physicalStorageKeys,
        CancellationToken cancellationToken = default);
}
