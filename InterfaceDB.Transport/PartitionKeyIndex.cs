namespace Idb.Transport;

public enum PartitionIndexMode { Disabled, Lazy, Eager }

/// <summary>Per-store limits for derived key metadata, never cached permission grants.</summary>
public sealed record PartitionIndexOptions
{
    public PartitionIndexMode Mode { get; init; } = PartitionIndexMode.Lazy;
    public int MaximumKeys { get; init; } = 10_000;
    public int MaximumPartitions { get; init; } = 16;
    public long MaximumEstimatedBytes { get; init; } = 8 * 1024 * 1024;
    internal void Validate()
    {
        if (!Enum.IsDefined(Mode) || MaximumKeys < 1 || MaximumPartitions < 1 || MaximumEstimatedBytes < 1024)
            throw new ArgumentOutOfRangeException(nameof(PartitionIndexOptions));
    }
}

public sealed record PartitionIndexStatistics(long Hits, long Misses, long Evictions, long Fallbacks,
    int CachedKeys, int CachedPartitions, long EstimatedBytes);
public sealed record PartitionKeyPage(int PartitionCount, IReadOnlyList<string> Keys);

/// <summary>
/// Derived ordinal key selection. The owner serializes calls with its commit gate and supplies
/// exclusively owned committed keys. Null means use the provider's ordinary enumeration path.
/// ApplyCommitted is called only after the durable commit. Invalidate discards all derived state.
/// </summary>
public interface IPartitionKeyIndex
{
    void Initialize(Func<IReadOnlyList<string>> committedKeys, CancellationToken cancellationToken);
    PartitionKeyPage? Select(string partition, string? afterKey, int take,
        Func<IReadOnlyList<string>> committedKeys, CancellationToken cancellationToken);
    void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations);
    void Invalidate();
    PartitionIndexStatistics Statistics { get; }
}

/// <summary>Creates an independent index for each store; the factory may be a DI singleton.</summary>
public interface IPartitionKeyIndexFactory { IPartitionKeyIndex Create(); }

public interface IPartitionIndexDiagnostics { PartitionIndexStatistics PartitionIndexStatistics { get; } }

// Emitted only by the built-in selector before it changes any cached bucket. An arbitrary
// OperationCanceledException from an injected index cannot make this recovery guarantee.
internal sealed class UnchangedPartitionIndexCancellationException(CancellationToken token)
    : OperationCanceledException(token);

public sealed class BoundedPartitionKeyIndexFactory : IPartitionKeyIndexFactory
{
    private readonly PartitionIndexOptions _options;
    public BoundedPartitionKeyIndexFactory(PartitionIndexOptions? options = null)
    { _options = options ?? new(); _options.Validate(); }
    public IPartitionKeyIndex Create() => new BoundedPartitionKeyIndex(_options);
    internal static IPartitionKeyIndexFactory Default { get; } = new BoundedPartitionKeyIndexFactory();
}

internal sealed class BoundedPartitionKeyIndex(PartitionIndexOptions options) : IPartitionKeyIndex
{
    private sealed class Bucket(string prefix, SortedSet<string> keys, LinkedListNode<string> node)
    {
        internal readonly SortedSet<string> Keys = keys;
        internal readonly LinkedListNode<string> Node = node;
        internal long Bytes = 256L + 2L * prefix.Length + keys.Sum(KeyBytes);
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private long _hits, _misses, _evictions, _fallbacks, _bytes;
    private int _keys;
    private static long KeyBytes(string key) => 96L + 2L * key.Length;
    private static bool Canonical(string prefix) => prefix.Length == ScopedEncodedStore.PrefixBytes
        && ScopedEncodedStore.TryGetPrefix(prefix + "x", out var found) && found == prefix;

    private static void CheckSelectionCancellation(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new UnchangedPartitionIndexCancellationException(token);
    }

    public PartitionIndexStatistics Statistics
    {
        get { lock (_sync) return new(_hits, _misses, _evictions, _fallbacks, _keys, _buckets.Count, _bytes); }
    }
    public void Initialize(Func<IReadOnlyList<string>> committedKeys, CancellationToken cancellationToken)
    {
        if (options.Mode != PartitionIndexMode.Eager) return;
        lock (_sync)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var key in committedKeys())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ScopedEncodedStore.TryGetPrefix(key, out var prefix)) continue;
                if (!groups.TryGetValue(prefix, out var keys)) groups.Add(prefix, keys = []);
                keys.Add(key);
            }
            foreach (var (prefix, keys) in groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache(prefix, keys);
            }
        }
    }
    public PartitionKeyPage? Select(string partition, string? afterKey, int take,
        Func<IReadOnlyList<string>> committedKeys, CancellationToken cancellationToken)
    {
        if (options.Mode == PartitionIndexMode.Disabled || !Canonical(partition)) return null;
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        CheckSelectionCancellation(cancellationToken);
        lock (_sync)
        {
            if (_buckets.TryGetValue(partition, out var bucket))
            {
                _hits++; _recency.Remove(bucket.Node); _recency.AddLast(bucket.Node);
                return Page(bucket.Keys, afterKey, take);
            }
            _misses++;
            var keys = new List<string>();
            foreach (var key in committedKeys())
            {
                CheckSelectionCancellation(cancellationToken);
                if (key.StartsWith(partition, StringComparison.Ordinal)) keys.Add(key);
            }
            if (Cache(partition, keys)) return Page(_buckets[partition].Keys, afterKey, take);
            _fallbacks++;
            keys.Sort(StringComparer.Ordinal);
            int start = afterKey is null ? 0 : keys.BinarySearch(afterKey, StringComparer.Ordinal);
            if (afterKey is not null) start = start < 0 ? ~start : start + 1;
            return new(keys.Count, keys.GetRange(start, Math.Min(take, keys.Count - start)).ToArray());
        }
    }
    private bool Cache(string prefix, IReadOnlyCollection<string> keys)
    {
        long bytes = 256L + 2L * prefix.Length + keys.Sum(KeyBytes);
        if (keys.Count > options.MaximumKeys || bytes > options.MaximumEstimatedBytes) return false;
        var bucket = new Bucket(prefix, new(keys, StringComparer.Ordinal), _recency.AddLast(prefix));
        _buckets.Add(prefix, bucket); _keys += bucket.Keys.Count; _bytes += bucket.Bytes;
        Trim();
        return true;
    }
    private static PartitionKeyPage Page(SortedSet<string> keys, string? after, int take)
    {
        if (keys.Count == 0 || (after is not null && StringComparer.Ordinal.Compare(after, keys.Max) >= 0)) return new(keys.Count, []);
        var result = new List<string>(Math.Min(take, keys.Count));
        foreach (var key in after is null ? keys : keys.GetViewBetween(after, keys.Max!))
        {
            if (after is not null && StringComparer.Ordinal.Compare(key, after) <= 0) continue;
            result.Add(key); if (result.Count == take) break;
        }
        return new(keys.Count, result.ToArray());
    }
    public void ApplyCommitted(IReadOnlyList<EncodedMutation> mutations)
    {
        if (options.Mode == PartitionIndexMode.Disabled) return;
        lock (_sync)
        {
            if (_buckets.Count == 0) return;
            foreach (var mutation in mutations)
            {
                if (!ScopedEncodedStore.TryGetPrefix(mutation.Key, out var prefix) || !_buckets.TryGetValue(prefix, out var bucket)) continue;
                int delta = mutation.Payload is not null ? (bucket.Keys.Add(mutation.Key) ? 1 : 0)
                    : (bucket.Keys.Remove(mutation.Key) ? -1 : 0);
                _keys += delta;
                long bytes = delta * KeyBytes(mutation.Key); _bytes += bytes; bucket.Bytes += bytes;
            }
            Trim();
        }
    }
    private void Trim()
    {
        while (_keys > options.MaximumKeys || _buckets.Count > options.MaximumPartitions || _bytes > options.MaximumEstimatedBytes)
        {
            var node = _recency.First!; var bucket = _buckets[node.Value];
            _keys -= bucket.Keys.Count; _bytes -= bucket.Bytes; _buckets.Remove(node.Value); _recency.RemoveFirst(); _evictions++;
        }
    }
    public void Invalidate()
    { lock (_sync) { _buckets.Clear(); _recency.Clear(); _bytes = 0; _keys = 0; } }
}
