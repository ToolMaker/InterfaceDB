using System.Collections;
using System.Collections.Concurrent;
using Idb.Libraries.Abstraction.Services.Cache;

namespace Idb.Libraries.Implementation.Cache;

public class EnvelopeConcurrentDictionary<TKey, TValue> : IEnvelopeDictionaryCaheManager<TKey, TValue>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, TValue> _items = new();
    private readonly ConcurrentDictionary<TKey, GroupState> _groups = new();

    public TValue this[TKey key]
    {
        get => _items[key];
        set => AddOrUpdate(key, key, value);
    }

    public ICollection<TKey> Keys => _items.Keys;

    public ICollection<TValue> Values => _items.Values;

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(TKey key, TValue value) => AddOrUpdate(key, key, value);

    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

    public void Clear()
    {
        _items.Clear();
        _groups.Clear();
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();

    public bool Remove(TKey key)
    {
        if (!_items.TryRemove(key, out _))
        {
            return false;
        }

        RemoveFromGroups(key);
        return true;
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) => Remove(item.Key);

    public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value!);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int GetGroupCount(TKey groupKey)
    {
        if (!_groups.TryGetValue(groupKey, out var group))
        {
            return 0;
        }

        lock (group.SyncRoot)
        {
            return group.PhysicalKeys.Count;
        }
    }

    public IReadOnlyList<TValue> GetGroup(TKey groupKey)
    {
        return TryGetGroup(groupKey, out var values)
            ? values
            : Array.Empty<TValue>();
    }

    public bool TryGetGroup(TKey groupKey, out IReadOnlyList<TValue> values)
    {
        values = Array.Empty<TValue>();
        if (!_groups.TryGetValue(groupKey, out var group))
        {
            return false;
        }

        lock (group.SyncRoot)
        {
            if (group.PhysicalKeys.Count == 0)
            {
                return false;
            }

            var result = new List<TValue>(group.PhysicalKeys.Count);
            foreach (var physicalKey in group.PhysicalKeys)
            {
                if (_items.TryGetValue(physicalKey, out var value))
                {
                    result.Add(value);
                }
            }

            values = result;
            return result.Count > 0;
        }
    }

    public void AddOrUpdate(TKey physicalKey, TKey groupKey, TValue value)
    {
        _items[physicalKey] = value;
        var group = _groups.GetOrAdd(groupKey, static _ => new GroupState());
        lock (group.SyncRoot)
        {
            if (!group.PhysicalKeys.Contains(physicalKey))
            {
                group.PhysicalKeys.Add(physicalKey);
            }
        }
    }

    private void RemoveFromGroups(TKey physicalKey)
    {
        foreach (var item in _groups)
        {
            var group = item.Value;
            lock (group.SyncRoot)
            {
                group.PhysicalKeys.Remove(physicalKey);
                if (group.PhysicalKeys.Count == 0)
                {
                    _groups.TryRemove(item.Key, out _);
                }
            }
        }
    }

    private sealed class GroupState
    {
        public object SyncRoot { get; } = new();
        public List<TKey> PhysicalKeys { get; } = new();
    }
}
