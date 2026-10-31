using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// A mutable overlay over either an in-memory or paged checkpoint. SegmentCore's visibility lock
/// supplies synchronization, so this map avoids a second concurrent-dictionary copy at reopen.
/// </summary>
internal sealed class PrimaryIndexMap :
    IReadOnlyDictionary<string, StripedObjectLogRecordAddress>,
    IDisposable
{
    private IReadOnlyDictionary<string, StripedObjectLogRecordAddress> _basis;
    private readonly Dictionary<string, OverlayChange> _changes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StripedObjectLogRecordAddress> _deletions = new(StringComparer.Ordinal);
    private int _count;
    private int _disposed;

    public PrimaryIndexMap(IReadOnlyDictionary<string, StripedObjectLogRecordAddress>? basis = null)
    {
        _basis = basis ?? new Dictionary<string, StripedObjectLogRecordAddress>(StringComparer.Ordinal);
        _count = _basis.Count;
    }

    public int Count => _count;

    public bool IsPaged => _basis is IPagedPrimaryIndexView { IsPaged: true };

    internal void Rebase(IReadOnlyDictionary<string, StripedObjectLogRecordAddress> basis,
        IReadOnlyDictionary<string, PrimaryIndexChange> changesAfterCapture)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var previous = _basis;
        _basis = basis;
        _changes.Clear();
        _count = basis.Count;
        foreach (var pair in changesAfterCapture)
        {
            if (pair.Value.IsDelete) TryRemove(pair.Key, out _);
            else Set(pair.Key, pair.Value.Address);
        }
        if (!ReferenceEquals(previous, basis) && previous is IDisposable disposable) disposable.Dispose();
    }

    public IEnumerable<string> Keys => this.Select(static pair => pair.Key);

    public IEnumerable<StripedObjectLogRecordAddress> Values => this.Select(static pair => pair.Value);

    public StripedObjectLogRecordAddress this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set => Set(key, value);
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    internal bool TryGetLatestAddress(string key, out StripedObjectLogRecordAddress value)
        => _deletions.TryGetValue(key, out value) || TryGetValue(key, out value);

    internal bool RecordDeletion(string key, StripedObjectLogRecordAddress tombstone)
    {
        var removed = TryRemove(key, out _);
        _deletions[key] = tombstone;
        return removed;
    }

    internal void RetireDeletionMarkers(IReadOnlyList<ObjectLogWritePosition> positions)
    {
        // Capture drained all mutations admitted before these stripe positions. Only
        // newer tombstones can still race an outstanding publication continuation.
        foreach (var pair in _deletions.ToArray())
            if (pair.Value.Address.Lsn <= positions[pair.Value.Stripe].LastLsn)
                _deletions.Remove(pair.Key);
    }

    public bool TryGetValue(string key, out StripedObjectLogRecordAddress value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_changes.TryGetValue(key, out var change))
        {
            value = change.Address;
            return !change.IsDelete;
        }

        return _basis.TryGetValue(key, out value);
    }

    public bool TryAdd(string key, StripedObjectLogRecordAddress value)
    {
        if (ContainsKey(key))
        {
            return false;
        }

        Set(key, value);
        return true;
    }

    public bool TryUpdate(
        string key,
        StripedObjectLogRecordAddress value,
        StripedObjectLogRecordAddress comparisonValue)
    {
        if (!TryGetValue(key, out var current) || current != comparisonValue)
        {
            return false;
        }

        Set(key, value);
        return true;
    }

    public bool TryRemove(string key, out StripedObjectLogRecordAddress value)
    {
        if (!TryGetValue(key, out value))
        {
            return false;
        }

        RemoveKnown(key);
        return true;
    }

    public bool Remove(string key, StripedObjectLogRecordAddress comparisonValue)
    {
        if (!TryGetValue(key, out var current) || current != comparisonValue)
        {
            return false;
        }

        RemoveKnown(key);
        return true;
    }

    public IEnumerator<KeyValuePair<string, StripedObjectLogRecordAddress>> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        foreach (var pair in _basis)
        {
            if (!_changes.TryGetValue(pair.Key, out var change))
            {
                yield return pair;
            }
            else if (!change.IsDelete)
            {
                yield return new KeyValuePair<string, StripedObjectLogRecordAddress>(pair.Key, change.Address);
            }
        }

        foreach (var pair in _changes)
        {
            if (!pair.Value.IsDelete && !pair.Value.ExistedInBasis)
            {
                yield return new KeyValuePair<string, StripedObjectLogRecordAddress>(
                    pair.Key,
                    pair.Value.Address);
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _basis is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Set(string key, StripedObjectLogRecordAddress value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var hasChange = _changes.TryGetValue(key, out var previous);
        var existedInBasis = hasChange ? previous.ExistedInBasis : _basis.ContainsKey(key);
        var exists = hasChange ? !previous.IsDelete : existedInBasis;
        _changes[key] = new OverlayChange(false, value, existedInBasis);
        _deletions.Remove(key);
        if (!exists)
        {
            _count++;
        }
    }

    private void RemoveKnown(string key)
    {
        var existedInBasis = _changes.TryGetValue(key, out var previous)
            ? previous.ExistedInBasis
            : _basis.ContainsKey(key);
        if (existedInBasis)
        {
            _changes[key] = new OverlayChange(true, default, true);
        }
        else
        {
            _changes.Remove(key);
        }

        _count--;
    }

    private readonly record struct OverlayChange(
        bool IsDelete,
        StripedObjectLogRecordAddress Address,
        bool ExistedInBasis);
}
