namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// One key-to-slot map handles deduplication and out-of-order completions. The returned
/// result owns only its value array, not this builder or the caller's key collection.
/// </summary>
internal sealed class OrderedReadBatch<T> where T : class
{
    private readonly IReadOnlyList<string> _keys;
    private readonly Dictionary<string, int> _firstSlots;
    private readonly T[] _values;

    internal OrderedReadBatch(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        _keys = keys;
        _firstSlots = new Dictionary<string, int>(keys.Count, StringComparer.Ordinal);
        _values = new T[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = keys[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            _firstSlots.TryAdd(key, index);
        }
    }

    internal Dictionary<string, int> UniqueKeys => _firstSlots;

    // Different workers may fill different slots; the map is immutable after construction.
    internal void Set(int slot, T? value) => _values[slot] = value!;

    internal void Set(string key, T value) => Set(_firstSlots[key], value);

    internal bool HasValue(string key) => _values[_firstSlots[key]] is not null;

    internal IReadOnlyList<T> Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_firstSlots.Count != _keys.Count)
        {
            // Resolve all duplicates BEFORE compacting misses. Otherwise compaction could
            // overwrite a first occurrence that a later duplicate still needs to reference.
            for (var index = 0; index < _keys.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _values[index] = _values[_firstSlots[_keys[index]]];
            }
        }

        var count = 0;
        for (var index = 0; index < _values.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values[index] is { } value)
            {
                _values[count++] = value;
            }
        }

        if (count == _values.Length)
        {
            return _values;
        }
        Array.Clear(_values, count, _values.Length - count);
        if (count == 0)
        {
            return Array.Empty<T>();
        }
        return new ArraySegment<T>(_values, 0, count);
    }
}
