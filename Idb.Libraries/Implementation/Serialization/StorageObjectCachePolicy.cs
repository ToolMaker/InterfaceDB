using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using System.Runtime.CompilerServices;

namespace Idb.Libraries.Implementation.Serialization;

public static class StorageObjectCachePolicy
{
    // Attributes are fixed for a runtime Type. Weak keys avoid retaining collectible types;
    // only this type-level decision is cached, never an entity or its mutable envelope override.
    private static readonly ConditionalWeakTable<Type, CacheAttributeState> CacheAttributes = new();
    private sealed record CacheAttributeState(bool IsCached);

    // Only reuse complete, object-free metadata. Nonempty encrypted fields deliberately take
    // the copying path: their values are mutable reference objects, not immutable metadata.
    internal static bool CanReuseMetadata<TContract>(
        IObjectEnvelope<TContract> cached,
        IObjectEnvelope<TContract> hydrated,
        TContract instance,
        StoragePolicy storagePolicy)
        where TContract : class, IStoredContract<TContract>
        => storagePolicy.ObjectCacheMode == StorageObjectCacheMode.MetadataOnly
            && cached.ObjectInstance is null
            && cached.ObjectInstanceWeakReference is null
            && cached.ImplementationTypeName is not null
            && !cached.CacheObjectInstance
            && !hydrated.CacheObjectInstance
            && cached.EncryptedFields.Count == 0
            && hydrated.EncryptedFields.Count == 0
            && cached.EnvelopeCacheKey == hydrated.EnvelopeCacheKey
            && cached.VersionTag == hydrated.VersionTag
            && cached.Location == hydrated.Location
            && cached.ImplementationTypeName == hydrated.ImplementationTypeName
            && cached.State == hydrated.State
            && cached.RevisionTimestampUtcMicroseconds == hydrated.RevisionTimestampUtcMicroseconds
            && (cached.ExtensionMetadata is null) == (hydrated.ExtensionMetadata is null)
            && cached.ExtensionMetadata.AsSpan().SequenceEqual(hydrated.ExtensionMetadata)
            && ResolveObjectCacheMode(instance, storagePolicy) == StorageObjectCacheMode.MetadataOnly;

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
        => instance is not null && CacheAttributes.GetValue(ContractInstance<TContract>.Implementation(instance).GetType(), static type =>
            new CacheAttributeState(type.IsDefined(typeof(StorageCachedAttribute), inherit: true))).IsCached;
}
