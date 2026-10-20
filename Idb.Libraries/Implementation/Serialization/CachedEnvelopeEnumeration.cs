using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Shared cache-only enumeration for storage providers.</summary>
public static class CachedEnvelopeEnumeration
{
    public static IEnumerable<KeyValuePair<string, TContract>> Enumerate<TContract>(
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes)
        where TContract : class, IStoredContract<TContract>
    {
        if (envelopes is ICachedEnvelopeSource<TContract> cachedSource)
        {
            foreach (var entry in cachedSource.EnumerateCachedObjects()) yield return entry;
            yield break;
        }

        foreach (var entry in envelopes)
        {
            // Recheck membership: a concurrent delete/replacement may retire an entry
            // observed by the dictionary's non-snapshot enumerator.
            if (envelopes.TryGetValue(entry.Key, out var current)
                && ReferenceEquals(current, entry.Value))
            {
                var instance = current.ObjectInstance;
                if (instance is null) current.ObjectInstanceWeakReference?.TryGetTarget(out instance);
                if (instance is not null) yield return new(entry.Key, instance);
            }
        }
    }
}
