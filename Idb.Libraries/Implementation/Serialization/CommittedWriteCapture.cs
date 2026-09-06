using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

// Active subscribers request encoded results from the write path. No read after commit can substitute a later writer's values.
internal sealed class CommittedWriteCapture<T> : IDisposable where T : class, IStoredContract<T>
{
    private static readonly AsyncLocal<CommittedWriteCapture<T>?> Current = new();
    private readonly CommittedWriteCapture<T>? _previous;
    private readonly ConcurrentDictionary<IObjectEnvelope<T>, ICommittedEnvelopeSnapshot<T>> _snapshots = new(ReferenceEqualityComparer.Instance);
    private ConcurrentDictionary<IObjectEnvelope<T>, ICommittedEnvelopeSnapshot<T>>? _before;
    private CommittedWriteCapture() { _previous = Current.Value; Current.Value = this; }
    internal static CommittedWriteCapture<T> Begin() => new();
    internal static bool Enabled => Current.Value is not null;
    internal static void Remember(IObjectEnvelope<T> envelope, byte[] payload, IObjectSerializer<T> serializer, IEnvelopeLifecycle<T> lifecycle)
    {
        if (Current.Value is { } capture)
            capture._snapshots[envelope] = new CommittedEnvelopeSnapshot<T>(payload, serializer, lifecycle,
                envelope.EnvelopeCacheKey, envelope.Location);
    }
    internal static ICommittedEnvelopeSnapshot<T>? Find(IObjectEnvelope<T>? envelope)
        => envelope is not null && Current.Value is { } capture && capture._snapshots.TryGetValue(envelope, out var snapshot) ? snapshot : null;
    internal static void RememberBefore(IObjectEnvelope<T> envelope, ICommittedEnvelopeSnapshot<T> snapshot)
    {
        if (Current.Value is not { } capture) return;
        var map = LazyInitializer.EnsureInitialized(ref capture._before,
            static () => new ConcurrentDictionary<IObjectEnvelope<T>, ICommittedEnvelopeSnapshot<T>>(ReferenceEqualityComparer.Instance));
        map[envelope] = snapshot;
    }
    internal static ICommittedEnvelopeSnapshot<T>? FindBefore(IObjectEnvelope<T>? envelope)
        => envelope is not null && Current.Value?._before is { } map && map.TryGetValue(envelope, out var snapshot) ? snapshot : null;
    public void Dispose() => Current.Value = _previous;
}
