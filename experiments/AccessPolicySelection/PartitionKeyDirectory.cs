// Experimental only. Compiled into the generated transport project, never a package.
namespace Idb.Transport;

internal enum SelectionVariant { Scan, Array, Tree, Lazy }
internal sealed record DirectoryPage(int Count, string[] Keys);

// Bounded derived cache: eviction never changes authorization or durable state.
internal sealed class LazyPartitionKeyDirectory(Func<IEnumerable<string>> snapshot,
    int maximumKeys = 10_000, int maximumPartitions = 16) : IPartitionKeyDirectory
{
    private sealed record Entry(IPartitionKeyDirectory Directory, LinkedListNode<string> Node, int Count);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private int _keyCount;
    public void Rebuild(IEnumerable<string> committedKeys)
    {
        // Invalidation is sufficient; the source is consulted on the next access.
        _entries.Clear(); _recency.Clear(); _keyCount = 0;
    }
    public DirectoryPage Select(string partition, string? after, int take)
    {
        if (_entries.TryGetValue(partition, out var entry))
        {
            _recency.Remove(entry.Node); _recency.AddLast(entry.Node);
            return entry.Directory.Select(partition, after, take);
        }
        var keys = snapshot().Where(key => key.StartsWith(partition, StringComparison.Ordinal)).ToArray();
        if (keys.Length > maximumKeys)
        {
            // Oversized tenants retain the original scan behavior rather than an unbounded cache.
            System.Array.Sort(keys, StringComparer.Ordinal);
            int start = after is null ? 0 : System.Array.BinarySearch(keys, after, StringComparer.Ordinal);
            if (after is not null) start = start < 0 ? ~start : start + 1;
            return new(keys.Length, keys.AsSpan(start, Math.Min(take, keys.Length - start)).ToArray());
        }
        IPartitionKeyDirectory directory = new PartitionKeyDirectory(SelectionVariant.Tree);
        directory.Rebuild(keys);
        var page = directory.Select(partition, after, take);
        if (page.Count <= maximumKeys)
        {
            _entries.Add(partition, new(directory, _recency.AddLast(partition), page.Count));
            _keyCount += page.Count; Trim();
        }
        return page;
    }
    public void Apply(IEnumerable<EncodedMutation> committedMutations)
    {
        foreach (var mutation in committedMutations)
        {
            if (!ScopedEncodedStore.TryGetPrefix(mutation.Key, out var prefix) || !_entries.TryGetValue(prefix, out var entry)) continue;
            entry.Directory.Apply([mutation]);
            int count = entry.Directory.Select(prefix, null, 1).Count;
            _keyCount += count - entry.Count;
            _entries[prefix] = entry with { Count = count };
        }
        Trim();
    }
    private void Trim()
    {
        while (_keyCount > maximumKeys || _entries.Count > maximumPartitions)
        {
            var oldest = _recency.First!;
            _keyCount -= _entries[oldest.Value].Count;
            _entries.Remove(oldest.Value); _recency.RemoveFirst();
        }
    }
}

internal interface IPartitionKeyDirectory
{
    void Rebuild(IEnumerable<string> committedKeys);
    void Apply(IEnumerable<EncodedMutation> committedMutations);
    DirectoryPage Select(string partition, string? after, int take);
}

internal sealed class PartitionKeyDirectory(SelectionVariant variant) : IPartitionKeyDirectory
{
    private interface IBucket
    {
        int Count { get; }
        void Set(string key, bool exists);
        string[] Page(string? after, int take);
    }
    private readonly Dictionary<string, IBucket> _partitions = new(StringComparer.Ordinal);

    public void Rebuild(IEnumerable<string> committedKeys)
    {
        _partitions.Clear();
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in committedKeys)
        {
            if (!ScopedEncodedStore.TryGetPrefix(key, out var prefix)) continue;
            if (!groups.TryGetValue(prefix, out var keys)) groups.Add(prefix, keys = []);
            keys.Add(key);
        }
        foreach (var (prefix, keys) in groups)
            _partitions.Add(prefix, Create(keys));
    }

    private IBucket Create(IEnumerable<string> keys) => variant == SelectionVariant.Array
        ? new ArrayBucket(keys) : new TreeBucket(keys);

    public void Apply(IEnumerable<EncodedMutation> committedMutations)
    {
        foreach (var mutation in committedMutations)
        {
            if (!ScopedEncodedStore.TryGetPrefix(mutation.Key, out var prefix)) continue;
            if (!_partitions.TryGetValue(prefix, out var bucket))
            {
                if (mutation.Payload is null) continue;
                _partitions.Add(prefix, bucket = Create([]));
            }
            bucket.Set(mutation.Key, mutation.Payload is not null);
            if (bucket.Count == 0) _partitions.Remove(prefix);
        }
    }

    public DirectoryPage Select(string partition, string? after, int take) =>
        _partitions.TryGetValue(partition, out var bucket)
            ? new(bucket.Count, bucket.Page(after, take)) : new(0, []);

    private sealed class ArrayBucket : IBucket
    {
        private string[] _keys;
        public ArrayBucket(IEnumerable<string> keys)
        { _keys = keys.ToArray(); System.Array.Sort(_keys, StringComparer.Ordinal); }
        public int Count => _keys.Length;
        public void Set(string key, bool exists)
        {
            int at = System.Array.BinarySearch(_keys, key, StringComparer.Ordinal);
            if ((at >= 0) == exists) return;
            if (exists)
            {
                at = ~at;
                var next = new string[_keys.Length + 1];
                _keys.AsSpan(0, at).CopyTo(next);
                next[at] = key;
                _keys.AsSpan(at).CopyTo(next.AsSpan(at + 1));
                _keys = next;
            }
            else
            {
                var next = new string[_keys.Length - 1];
                _keys.AsSpan(0, at).CopyTo(next);
                _keys.AsSpan(at + 1).CopyTo(next.AsSpan(at));
                _keys = next;
            }
        }
        public string[] Page(string? after, int take)
        {
            int start = after is null ? 0 : System.Array.BinarySearch(_keys, after, StringComparer.Ordinal);
            if (after is not null) start = start < 0 ? ~start : start + 1;
            return _keys.AsSpan(start, Math.Min(take, _keys.Length - start)).ToArray();
        }
    }

    private sealed class TreeBucket(IEnumerable<string> keys) : IBucket
    {
        private readonly SortedSet<string> _keys = new(keys, StringComparer.Ordinal);
        public int Count => _keys.Count;
        public void Set(string key, bool exists)
        { if (exists) _keys.Add(key); else _keys.Remove(key); }
        public string[] Page(string? after, int take)
        {
            if (_keys.Count == 0 || (after is not null && StringComparer.Ordinal.Compare(after, _keys.Max) >= 0)) return [];
            var result = new List<string>(Math.Min(take, _keys.Count));
            var range = after is null ? _keys : _keys.GetViewBetween(after, _keys.Max!);
            foreach (var key in range)
            {
                if (after is not null && StringComparer.Ordinal.Compare(key, after) <= 0) continue;
                result.Add(key);
                if (result.Count == take) break;
            }
            return result.ToArray();
        }
    }
}
