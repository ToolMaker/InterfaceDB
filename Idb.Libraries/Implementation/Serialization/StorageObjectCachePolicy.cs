using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

internal static class StorageObjectCachePolicy
{
    public static bool ShouldRetainObjectInstance<TContract>(
        IObjectEnvelope<TContract> envelope,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        return storagePolicy.KeepObjectInstanceInCacheOnWrite
            || envelope.CacheObjectInstance
            || IsStorageCached(envelope.ObjectInstance);
    }

    public static bool ShouldRetainObjectInstance<TContract>(
        TContract? instance,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(storagePolicy);

        return storagePolicy.KeepObjectInstanceInCacheOnWrite
            || instance?.Envelope?.CacheObjectInstance == true
            || IsStorageCached(instance);
    }

    private static bool IsStorageCached<TContract>(TContract? instance)
        where TContract : class, IStoredContract<TContract>
        => instance?.GetType().IsDefined(typeof(StorageCachedAttribute), inherit: true) == true;
}
