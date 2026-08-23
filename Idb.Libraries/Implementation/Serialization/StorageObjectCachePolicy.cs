using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

internal static class StorageObjectCachePolicy
{
    public static bool ShouldRetainObjectInstance<TContract>(
        IObjectEnvelope<TContract> envelope,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
        => ResolveObjectCacheMode(envelope, storagePolicy) is StorageObjectCacheMode.StrongObject;

    public static StorageObjectCacheMode ResolveObjectCacheMode<TContract>(
        IObjectEnvelope<TContract> envelope,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        if (envelope.CacheObjectInstance
            || IsStorageCached(envelope.ObjectInstance))
        {
            return StorageObjectCacheMode.StrongObject;
        }

        return storagePolicy.ObjectCacheMode;
    }

    public static bool ShouldRetainObjectInstance<TContract>(
        TContract? instance,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
        => ResolveObjectCacheMode(instance, storagePolicy) is StorageObjectCacheMode.StrongObject;

    public static StorageObjectCacheMode ResolveObjectCacheMode<TContract>(
        TContract? instance,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(storagePolicy);

        if (instance?.Envelope?.CacheObjectInstance == true
            || IsStorageCached(instance))
        {
            return StorageObjectCacheMode.StrongObject;
        }

        return storagePolicy.ObjectCacheMode;
    }

    public static void StoreObjectReference<TContract>(
        IObjectEnvelope<TContract> envelope,
        TContract? instance,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        if (instance is null)
        {
            envelope.ObjectInstance = null;
            envelope.ObjectInstanceWeakReference = null;
            return;
        }

        switch (ResolveObjectCacheMode(instance, storagePolicy))
        {
            case StorageObjectCacheMode.StrongObject:
                envelope.ObjectInstance = instance;
                envelope.ObjectInstanceWeakReference = null;
                break;
            case StorageObjectCacheMode.WeakObject:
                envelope.ObjectInstance = null;
                envelope.ObjectInstanceWeakReference = new WeakReference<TContract>(instance);
                break;
            default:
                envelope.ObjectInstance = null;
                envelope.ObjectInstanceWeakReference = null;
                break;
        }
    }

    public static bool TryGetObjectInstance<TContract>(
        IObjectEnvelope<TContract> envelope,
        out TContract? instance)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ObjectInstance is not null)
        {
            instance = envelope.ObjectInstance;
            return true;
        }

        if (envelope.ObjectInstanceWeakReference is not null
            && envelope.ObjectInstanceWeakReference.TryGetTarget(out var target))
        {
            instance = target;
            return true;
        }

        envelope.ObjectInstanceWeakReference = null;
        instance = null;
        return false;
    }

    private static bool IsStorageCached<TContract>(TContract? instance)
        where TContract : class, IStoredContract<TContract>
        => instance?.GetType().IsDefined(typeof(StorageCachedAttribute), inherit: true) == true;
}
