using System.Collections;
using System.Collections.Concurrent;
using Idb.Libraries.Abstractions.Services.Cache;

namespace Idb.Libraries.Implementation.Cache;

public class EnvelopeConcurrentDictionary<TKey, TValue> : IEnvelopeDictionaryCacheManager<TKey, TValue>
    where TKey : notnull
{
    private const int MembershipLockCount = 64;
    private readonly ConcurrentDictionary<TKey, TValue> _items = new();
    private readonly ConcurrentDictionary<TKey, GroupState> _groups = new();
    private readonly MembershipStripe[] _membershipStripes = CreateMembershipStripes();

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
        var locksTaken = 0;
        try
        {
            for (; locksTaken < _membershipStripes.Length; locksTaken++)
            {
                Monitor.Enter(_membershipStripes[locksTaken].SyncRoot);
            }

            _items.Clear();
            _groups.Clear();
            foreach (var stripe in _membershipStripes)
            {
                stripe.GroupKeys.Clear();
            }
        }
        finally
        {
            for (var index = locksTaken - 1; index >= 0; index--)
            {
                Monitor.Exit(_membershipStripes[index].SyncRoot);
            }
        }
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();

    public bool Remove(TKey key)
    {
        var stripe = GetMembershipStripe(key);
        lock (stripe.SyncRoot)
        {
            if (!_items.TryRemove(key, out _))
            {
                return false;
            }

            if (stripe.GroupKeys.Remove(key, out var groupKey))
            {
                RemoveFromGroup(groupKey, key);
            }

            return true;
        }
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) => Remove(item.Key);

    public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value!);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int GetGroupCount(TKey groupKey)
    {
        while (_groups.TryGetValue(groupKey, out var group))
        {
            lock (group.SyncRoot)
            {
                if (!group.IsRetired)
                {
                    return group.Count;
                }
            }
        }

        return 0;
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
        TKey[] physicalKeys;
        while (true)
        {
            if (!_groups.TryGetValue(groupKey, out var group))
            {
                return false;
            }

            lock (group.SyncRoot)
            {
                if (group.IsRetired)
                {
                    continue;
                }

                if (group.Count == 0)
                {
                    return false;
                }

                physicalKeys = group.GetKeysSnapshot();
                break;
            }
        }

        var result = new List<TValue>(physicalKeys.Length);
        foreach (var physicalKey in physicalKeys)
        {
            if (_items.TryGetValue(physicalKey, out var value))
            {
                result.Add(value);
            }
        }

        values = result;
        return result.Count > 0;
    }

    public void AddOrUpdate(TKey physicalKey, TKey groupKey, TValue value)
    {
        var stripe = GetMembershipStripe(physicalKey);
        lock (stripe.SyncRoot)
        {
            _items[physicalKey] = value;
            if (stripe.GroupKeys.TryGetValue(physicalKey, out var previousGroupKey))
            {
                if (EqualityComparer<TKey>.Default.Equals(previousGroupKey, groupKey))
                {
                    return;
                }

                RemoveFromGroup(previousGroupKey, physicalKey);
            }

            AddToGroup(groupKey, physicalKey);
            stripe.GroupKeys[physicalKey] = groupKey;
        }
    }

    private void AddToGroup(TKey groupKey, TKey physicalKey)
    {
        while (true)
        {
            var group = _groups.GetOrAdd(groupKey, static _ => new GroupState());
            lock (group.SyncRoot)
            {
                if (group.IsRetired)
                {
                    continue;
                }

                group.Add(physicalKey);
                return;
            }
        }
    }

    private void RemoveFromGroup(TKey groupKey, TKey physicalKey)
    {
        if (!_groups.TryGetValue(groupKey, out var group))
        {
            return;
        }

        lock (group.SyncRoot)
        {
            if (group.IsRetired)
            {
                return;
            }

            group.Remove(physicalKey);
            if (group.Count == 0)
            {
                group.IsRetired = true;
                _ = ((ICollection<KeyValuePair<TKey, GroupState>>)_groups).Remove(
                    new KeyValuePair<TKey, GroupState>(groupKey, group));
            }
        }
    }

    private MembershipStripe GetMembershipStripe(TKey key)
    {
        var hashCode = EqualityComparer<TKey>.Default.GetHashCode(key);
        return _membershipStripes[(int)((uint)hashCode % MembershipLockCount)];
    }

    private static MembershipStripe[] CreateMembershipStripes()
    {
        var stripes = new MembershipStripe[MembershipLockCount];
        for (var index = 0; index < stripes.Length; index++)
        {
            stripes[index] = new MembershipStripe();
        }

        return stripes;
    }

    private sealed class MembershipStripe
    {
        public object SyncRoot { get; } = new();

        public Dictionary<TKey, TKey> GroupKeys { get; } = new();
    }

    private sealed class GroupState
    {
        private const int PromotionThreshold = 8;
        private const int DemotionThreshold = PromotionThreshold / 2;
        private List<TKey>? _smallKeys = new(1);
        private HashSet<TKey>? _largeKeys;

        public object SyncRoot { get; } = new();

        public bool IsRetired { get; set; }

        public int Count => _largeKeys?.Count ?? _smallKeys!.Count;

        public void Add(TKey key)
        {
            if (_largeKeys is not null)
            {
                _largeKeys.Add(key);
                return;
            }

            if (_smallKeys!.Contains(key))
            {
                return;
            }

            _smallKeys.Add(key);
            if (_smallKeys.Count <= PromotionThreshold)
            {
                return;
            }

            _largeKeys = new HashSet<TKey>(_smallKeys);
            _smallKeys = null;
        }

        public void Remove(TKey key)
        {
            if (_largeKeys is null)
            {
                _smallKeys!.Remove(key);
                return;
            }

            _largeKeys.Remove(key);
            if (_largeKeys.Count > DemotionThreshold)
            {
                return;
            }

            _smallKeys = new List<TKey>(_largeKeys);
            _largeKeys = null;
        }

        public TKey[] GetKeysSnapshot()
            => _largeKeys is not null ? [.. _largeKeys] : [.. _smallKeys!];
    }
}
