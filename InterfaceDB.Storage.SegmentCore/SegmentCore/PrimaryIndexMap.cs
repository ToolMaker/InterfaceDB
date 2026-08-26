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
    private readonly IReadOnlyDictionary<string, StripedObjectLogRecordAddress> _basis;
    private readonly Dictionary<string, PrimaryIndexChange> _changes = new(StringComparer.Ordinal);
    private int _count;
    private int _disposed;

    public PrimaryIndexMap(IReadOnlyDictionary<string, StripedObjectLogRecordAddress>? basis = null)
    {
        _basis = basis ?? new Dictionary<string, StripedObjectLogRecordAddress>(StringComparer.Ordinal);
        _count = _basis.Count;
    }

    public int Count => _count;

    public bool IsPaged => _basis is IPagedPrimaryIndexView { IsPaged: true };

    public IEnumerable<string> Keys => this.Select(static pair => pair.Key);

    public IEnumerable<StripedObjectLogRecordAddress> Values => this.Select(static pair => pair.Value);

    public StripedObjectLogRecordAddress this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
        set => Set(key, value);
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

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
            if (!pair.Value.IsDelete && !_basis.ContainsKey(pair.Key))
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
        var exists = TryGetValue(key, out _);
        _changes[key] = new PrimaryIndexChange(false, value);
        if (!exists)
        {
            _count++;
        }
    }

    private void RemoveKnown(string key)
    {
        if (_basis.ContainsKey(key))
        {
            _changes[key] = new PrimaryIndexChange(true, default);
        }
        else
        {
            _changes.Remove(key);
        }

        _count--;
    }
}
