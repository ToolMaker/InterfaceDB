using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

// Encoded state belongs to the object graph, never to a key or a global repository setting.
// Built-in envelopes own the state so read copies do not create global weak handles.
// Weak associations preserve support for custom envelopes without that capability.
// The snapshot abstraction uses the repository's configured serializer and encryption context.
internal static class CommittedCacheCopies<TContract> where TContract : class, IStoredContract<TContract>
{
    private static readonly ConditionalWeakTable<IObjectEnvelope<TContract>, ICommittedEnvelopeSnapshot<TContract>> Snapshots = new();
    private static readonly ConditionalWeakTable<TContract, Anchor> Anchors = new();
    private static readonly ConditionalWeakTable<IObjectEnvelope<TContract>, IObjectEnvelope<TContract>> PreparedCopies = new();
    private sealed record Anchor(TContract Cached);

    internal static void Remember(IObjectEnvelope<TContract> envelope, ReadOnlyMemory<byte> payload,
        IObjectSerializer<TContract> serializer, IEnvelopeLifecycle<TContract> lifecycle,
        StoragePolicy policy, byte[]? context = null, bool prepareCacheCopy = false)
    {
        if (StorageObjectCachePolicy.ResolveObjectCacheMode(envelope, policy) == StorageObjectCacheMode.MetadataOnly) return;
        // A formatter may retain slices of its input; keep separate, privately owned bytes.
        var snapshot = new CommittedEnvelopeSnapshot<TContract>(payload.ToArray(), serializer, lifecycle,
            envelope.EnvelopeCacheKey, envelope.Location, context);
        Set(envelope, snapshot);
        if (prepareCacheCopy)
        {
            // Validate/materialize before a write commits. Cache publication must not introduce
            // a new deserialization failure after durable storage has already changed.
            var prepared = snapshot.Materialize().Envelope!;
            Set(prepared, snapshot);
            lock (PreparedCopies) { PreparedCopies.Remove(envelope); PreparedCopies.Add(envelope, prepared); }
        }
    }

    internal static IObjectEnvelope<TContract> Copy(IObjectEnvelope<TContract> source, bool retainCache = true,
        TContract? pinnedInstance = null)
    {
        var instance = pinnedInstance;
        if (instance is null) StorageObjectCachePolicy.TryGetObjectInstance(source, out instance);
        if (!TryGet(source, out var snapshot)
            && (instance?.Envelope is null || !TryGet(instance.Envelope, out snapshot)))
            return source; // Metadata-only entries and custom readers have no retained snapshot.

        IObjectEnvelope<TContract>? envelope = null;
        if (!retainCache)
        {
            lock (PreparedCopies)
            {
                if (PreparedCopies.TryGetValue(source, out envelope)) PreparedCopies.Remove(source);
            }
        }
        envelope ??= snapshot.Materialize().Envelope!;
        var result = envelope.ObjectInstance!;
        envelope.EnvelopeCacheKey = source.EnvelopeCacheKey;
        envelope.Location = source.Location;
        envelope.State = source.State;
        envelope.CacheObjectInstance = source.CacheObjectInstance;
        envelope.ImplementationTypeName = source.ImplementationTypeName;
        EnvelopeCommitOrder.Copy(source, envelope);
        Set(envelope, snapshot);
        if (retainCache && instance is not null) KeepAlive(result, instance);
        return envelope;
    }

    internal static void KeepAlive(TContract owner, TContract cached)
    {
        if (ReferenceEquals(owner, cached)) return;
        if (owner.Envelope is IEnvelopeCacheState<TContract> state) state.CachedOwner = cached;
        else Anchors.AddOrUpdate(owner, new(cached));
    }

    private static bool TryGet(IObjectEnvelope<TContract> envelope, out ICommittedEnvelopeSnapshot<TContract> snapshot)
    {
        if (envelope is IEnvelopeCacheState<TContract> state)
        {
            snapshot = state.CommittedSnapshot!;
            return snapshot is not null;
        }
        return Snapshots.TryGetValue(envelope, out snapshot!);
    }

    private static void Set(IObjectEnvelope<TContract> envelope, ICommittedEnvelopeSnapshot<TContract> snapshot)
    {
        if (envelope is IEnvelopeCacheState<TContract> state) state.CommittedSnapshot = snapshot;
        else Snapshots.AddOrUpdate(envelope, snapshot);
    }
}
