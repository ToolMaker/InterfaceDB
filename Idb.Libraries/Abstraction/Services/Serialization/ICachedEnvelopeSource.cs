using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Optional repository capability for querying shared mutable objects without reading storage.
/// </summary>
public interface ICachedEnvelopeSource<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Enumerates physical keys and currently live cached objects. Must not hydrate missing
    /// objects or return deleted/replaced cache entries. Enumeration has ordinary concurrent
    /// read semantics; each returned object is strongly referenced while the caller uses it.
    /// </summary>
    IEnumerable<KeyValuePair<string, TContract>> EnumerateCachedObjects();
}
