using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>One observation epoch owns its cache. No baseline survives an unobserved phase.</summary>
internal sealed class NotificationBaselineCache<T>(RepositoryNotificationOptions options) where T : class, IStoredContract<T>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ICommittedEnvelopeSnapshot<T> Snapshot, LinkedListNode<string> Node)> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private long _bytes;

    internal ICommittedEnvelopeSnapshot<T>? Find(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var found)) return null;
            _order.Remove(found.Node); _order.AddLast(found.Node);
            return found.Snapshot;
        }
    }

    internal void Forget(T entity)
    {
        if (entity.Envelope is not { } envelope) return;
        lock (_gate) Remove(envelope.EnvelopeCacheKey);
    }

    internal void Remember(T entity, ICommittedEnvelopeSnapshot<T> snapshot)
    {
        if (options.MaximumBaselineEntries == 0 || snapshot.ByteLength > options.MaximumBaselineBytes) return;
        var key = entity.Envelope!.EnvelopeCacheKey;
        lock (_gate)
        {
            Remove(key);
            while (_entries.Count >= options.MaximumBaselineEntries || _bytes + snapshot.ByteLength > options.MaximumBaselineBytes)
                Remove(_order.First!.Value);
            _entries.Add(key, (snapshot, _order.AddLast(key))); _bytes += snapshot.ByteLength;
        }
    }

    private void Remove(string key)
    {
        if (!_entries.Remove(key, out var found)) return;
        _order.Remove(found.Node); _bytes -= found.Snapshot.ByteLength;
    }

    internal void Clear()
    {
        lock (_gate) { _entries.Clear(); _order.Clear(); _bytes = 0; }
    }
}
