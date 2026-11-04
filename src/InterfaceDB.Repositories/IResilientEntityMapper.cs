using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

/// <summary>
/// Supplies domain-owned identity and value-copy behavior without observing storage envelopes.
/// </summary>
public interface IResilientEntityMapper<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Returns the stable logical lookup key shared by the preferred and peer repositories. It
    /// must identify exactly one logical entity in each endpoint. Provider-owned physical
    /// envelope-cache keys may differ and are deliberately not supplied by this mapper.
    /// </summary>
    string GetStorageKey(TContract entity);

    /// <summary>
    /// Creates a value copy that has no storage envelope. The copy must not share mutable
    /// collections or mutable child objects with <paramref name="source"/>; immutable subgraphs
    /// may be shared.
    /// </summary>
    TContract CreateDetachedCopy(TContract source);

    /// <summary>
    /// Copies persisted domain values without reading, replacing, or attaching an envelope.
    /// Mutable collections and child objects must also be copied so later changes to either
    /// instance cannot mutate the other endpoint or an application result implicitly.
    /// </summary>
    void CopyValues(TContract source, TContract target);
}
