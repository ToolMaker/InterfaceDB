using System.Collections;
using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Cache;

/// <summary>
/// Composes the grouped metadata dictionary with a directory of retained object references.
/// Metadata-only entries do not add work to cache-aware searches. Dead weak entries are pruned.
/// </summary>
public sealed class ObjectEnvelopeDictionary<TContract> :
    IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>,
    ICachedEnvelopeSource<TContract>, ICachedEnvelopeTracker<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly EnvelopeConcurrentDictionary<string, IObjectEnvelope<TContract>> _items = new();
    private readonly ConcurrentDictionary<string, IObjectEnvelope<TContract>> _cached = new(StringComparer.Ordinal);
    private readonly object[] _gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    private object Gate(string key) => _gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_gates.Length];
    public IObjectEnvelope<TContract> this[string key] { get => _items[key]; set => AddOrUpdate(key, key, value); }
    public ICollection<string> Keys => _items.Keys;
    public ICollection<IObjectEnvelope<TContract>> Values => _items.Values;
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public int GetGroupCount(string groupKey) => _items.GetGroupCount(groupKey);
    public IReadOnlyList<IObjectEnvelope<TContract>> GetGroup(string groupKey) => _items.GetGroup(groupKey);
    public bool TryGetGroup(string groupKey, out IReadOnlyList<IObjectEnvelope<TContract>> values) => _items.TryGetGroup(groupKey, out values);
    public bool TryGetValue(string key, out IObjectEnvelope<TContract> value) => _items.TryGetValue(key, out value!);
    public bool ContainsKey(string key) => _items.ContainsKey(key);
    public bool Contains(KeyValuePair<string, IObjectEnvelope<TContract>> item) => _items.Contains(item);
    public void CopyTo(KeyValuePair<string, IObjectEnvelope<TContract>>[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<string, IObjectEnvelope<TContract>>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(string key, IObjectEnvelope<TContract> value) => AddOrUpdate(key, key, value);
    public void Add(KeyValuePair<string, IObjectEnvelope<TContract>> item) => Add(item.Key, item.Value);
    public bool Remove(KeyValuePair<string, IObjectEnvelope<TContract>> item) => Remove(item.Key);

    public void AddOrUpdate(string physicalKey, string groupKey, IObjectEnvelope<TContract> value)
    {
        lock (Gate(physicalKey))
        {
            _items.AddOrUpdate(physicalKey, groupKey, value);
            Track(physicalKey, value);
        }
    }

    public bool Remove(string key)
    {
        lock (Gate(key))
        {
            _cached.TryRemove(key, out _);
            return _items.Remove(key);
        }
    }

    public void Clear()
    {
        var taken = 0;
        try
        {
            for (; taken < _gates.Length; taken++) Monitor.Enter(_gates[taken]);
            _items.Clear();
            _cached.Clear();
        }
        finally
        {
            for (var i = taken - 1; i >= 0; i--) Monitor.Exit(_gates[i]);
        }
    }

    public void RefreshCachedObject(IObjectEnvelope<TContract> envelope)
    {
        var key = envelope.EnvelopeCacheKey;
        lock (Gate(key))
        {
            if (_items.TryGetValue(key, out var current) && ReferenceEquals(current, envelope))
                Track(key, current);
        }
    }

    public IEnumerable<KeyValuePair<string, TContract>> EnumerateCachedObjects()
    {
        foreach (var entry in _cached)
        {
            TContract? instance = null;
            lock (Gate(entry.Key))
            {
                if (_items.TryGetValue(entry.Key, out var current))
                {
                    // Inspect without clearing an expired weak reference: hydration can
                    // install a new reference in place before RefreshCachedObject takes
                    // this gate. The directory must not erase that newer reference.
                    instance = current.ObjectInstance;
                    if (instance is null) current.ObjectInstanceWeakReference?.TryGetTarget(out instance);
                    if (instance is null) _cached.TryRemove(entry.Key, out _);
                }
                else _cached.TryRemove(entry.Key, out _);
            }
            // Never retain a gate across consumer code or async enumeration.
            if (instance is not null) yield return new(entry.Key, instance);
        }
    }

    private void Track(string key, IObjectEnvelope<TContract> envelope)
    {
        if (envelope.ObjectInstance is not null || envelope.ObjectInstanceWeakReference is not null)
            _cached[key] = envelope;
        else _cached.TryRemove(key, out _);
    }
}
