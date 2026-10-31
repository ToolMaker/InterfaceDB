using System.Buffers.Binary;
using System.Numerics;
using Idb.Libraries.Abstractions.Enums;
using RadixForge;

namespace Idb.Libraries.Implementation.Indexes;

internal enum SearchComparisonOperator
{
    Equal,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    StartsWith,
    EndsWith,
    Contains
}

internal sealed class PropertySearchIndex : IDisposable
{
    private readonly SearchIndexDefinition _definition;
    private readonly Dictionary<object, HashSet<string>> _exact = new();
    private readonly AdaptiveContainsSearchIndex? _contains;
    private readonly RadixPrefixSearchIndex? _prefix;
    private readonly IRangeSearchIndex? _range;
    private readonly RadixSuffixSearchIndex? _suffix;

    public PropertySearchIndex(SearchIndexDefinition definition)
    {
        _definition = definition;
        _contains = definition.Mode.HasFlag(SearchIndexMode.Contains)
            ? new AdaptiveContainsSearchIndex()
            : null;
        _prefix = definition.Mode.HasFlag(SearchIndexMode.Prefix)
            ? new RadixPrefixSearchIndex()
            : null;
        _range = definition.Mode.HasFlag(SearchIndexMode.Range)
            ? RangeSearchIndexFactory.Create(definition.ValueType)
            : null;
        _suffix = definition.Mode.HasFlag(SearchIndexMode.Suffix)
            ? new RadixSuffixSearchIndex()
            : null;
    }

    public bool TryNormalizeValue(object value, out object normalized)
        => ExactNumericConversion.TryConvert(value, _definition.ValueType, out normalized);

    public void Clear()
    {
        _exact.Clear();
        _contains?.Clear();
        _prefix?.Clear();
        _range?.Clear();
        _suffix?.Clear();
    }

    public void Dispose()
    {
        _exact.Clear();
        _contains?.Dispose();
        _prefix?.Dispose();
        _range?.Dispose();
        _suffix?.Dispose();
    }

    internal void BeginBulkLoad()
        => _contains?.BeginBulkLoad();

    internal void CompleteBulkLoad()
        => _contains?.CompleteBulkLoad();

    public void Add(object value, string physicalKey)
    {
        if (_definition.Mode.HasFlag(SearchIndexMode.Exact))
        {
            AddToExact(value, physicalKey);
        }

        if (_contains is not null && value is string containsText)
        {
            _contains.Upsert(containsText, physicalKey);
        }

        _range?.Add(value, physicalKey);
        if (_prefix is not null && value is string text)
        {
            _prefix.Add(text, physicalKey);
        }

        if (_suffix is not null && value is string suffixText)
        {
            _suffix.Add(suffixText, physicalKey);
        }
    }

    public void Remove(object value, string physicalKey)
    {
        if (_definition.Mode.HasFlag(SearchIndexMode.Exact))
        {
            RemoveFromExact(value, physicalKey);
        }

        _contains?.Remove(physicalKey);

        _range?.Remove(value, physicalKey);
        if (_prefix is not null && value is string text)
        {
            _prefix.Remove(text, physicalKey);
        }

        if (_suffix is not null && value is string suffixText)
        {
            _suffix.Remove(suffixText, physicalKey);
        }
    }

    public HashSet<string>? Search(SearchComparisonOperator comparisonOperator, object value)
    {
        if (comparisonOperator is SearchComparisonOperator.Equal
            && _definition.Mode.HasFlag(SearchIndexMode.Exact))
        {
            return _exact.TryGetValue(value, out var keys)
                ? new HashSet<string>(keys, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }

        if (comparisonOperator is SearchComparisonOperator.StartsWith
            && _prefix is not null
            && value is string prefix)
        {
            return _prefix.Search(prefix);
        }

        if (comparisonOperator is SearchComparisonOperator.StartsWith)
        {
            return null;
        }

        if (comparisonOperator is SearchComparisonOperator.EndsWith
            && _suffix is not null
            && value is string suffix)
        {
            return _suffix.Search(suffix);
        }

        if (comparisonOperator is SearchComparisonOperator.EndsWith)
        {
            return null;
        }

        if (comparisonOperator is SearchComparisonOperator.Contains
            && _contains is not null
            && value is string substring)
        {
            return _contains.Search(substring);
        }

        if (comparisonOperator is SearchComparisonOperator.Contains)
        {
            return null;
        }

        return _range?.Search(comparisonOperator, value);
    }

    public HashSet<string>? SearchRange(ObjectRangeQuery query)
        => _range?.Search(query);

    public int? EstimateCount(SearchComparisonOperator comparisonOperator, object value)
    {
        if (comparisonOperator is SearchComparisonOperator.Equal
            && _definition.Mode.HasFlag(SearchIndexMode.Exact))
        {
            return _exact.TryGetValue(value, out var keys) ? keys.Count : 0;
        }

        if (comparisonOperator is SearchComparisonOperator.StartsWith
            && _prefix is not null
            && value is string prefix)
        {
            return _prefix.EstimateCount(prefix);
        }

        if (comparisonOperator is SearchComparisonOperator.StartsWith)
        {
            return null;
        }

        if (comparisonOperator is SearchComparisonOperator.EndsWith
            && _suffix is not null
            && value is string suffix)
        {
            return _suffix.EstimateCount(suffix);
        }

        if (comparisonOperator is SearchComparisonOperator.EndsWith)
        {
            return null;
        }

        if (comparisonOperator is SearchComparisonOperator.Contains
            && _contains is not null
            && value is string substring)
        {
            return _contains.EstimateCount(substring);
        }

        if (comparisonOperator is SearchComparisonOperator.Contains)
        {
            return null;
        }

        return _range?.EstimateCount(comparisonOperator, value);
    }

    public int? EstimateRange(ObjectRangeQuery query)
        => _range?.EstimateCount(query);

    private void AddToExact(object value, string physicalKey)
    {
        if (!_exact.TryGetValue(value, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            _exact[value] = keys;
        }

        keys.Add(physicalKey);
    }

    private void RemoveFromExact(object value, string physicalKey)
    {
        if (!_exact.TryGetValue(value, out var keys))
        {
            return;
        }

        keys.Remove(physicalKey);
        if (keys.Count == 0)
        {
            _exact.Remove(value);
        }
    }
}

internal sealed class AdaptiveContainsSearchIndex : IDisposable
{
    // InterfaceDB owns the normal threshold check so catalog installation can temporarily
    // defer compaction without buffering values or changing their immediate visibility.
    private readonly AdaptiveGramIndex<string> _index = new(
        new AdaptiveGramIndexOptions { AutomaticCompactionThreshold = 0 },
        itemEqualityComparer: StringComparer.Ordinal);
    private int _bulkLoadDepth;

    public int Count => _index.Count;

    public int PendingMutationCount => _index.PendingMutationCount;

    public void Upsert(string value, string physicalKey)
    {
        _index.Upsert(physicalKey, value);
        CompactAutomaticallyIfNeeded();
    }

    public void Remove(string physicalKey)
    {
        _index.Remove(physicalKey);
        CompactAutomaticallyIfNeeded();
    }

    public int EstimateCount(string substring)
        => _index.CountContaining(substring);

    public HashSet<string> Search(string substring)
        => new(_index.FindContaining(substring), StringComparer.Ordinal);

    public AdaptiveGramIndexCompactionResult Compact()
        => _index.Compact();

    internal void BeginBulkLoad()
    {
        checked
        {
            _bulkLoadDepth++;
        }
    }

    internal void CompleteBulkLoad()
    {
        if (_bulkLoadDepth <= 0)
        {
            throw new InvalidOperationException("No adaptive contains-index bulk load is active.");
        }

        _bulkLoadDepth--;
        if (_bulkLoadDepth == 0)
        {
            _index.Compact();
        }
    }

    public void Clear() => _index.Clear();

    public void Dispose() => _index.Dispose();

    private void CompactAutomaticallyIfNeeded()
    {
        if (_bulkLoadDepth == 0
            && _index.PendingMutationCount >= AdaptiveGramIndexOptions.DefaultAutomaticCompactionThreshold)
        {
            _index.Compact();
        }
    }
}

internal sealed class RadixPrefixSearchIndex : IDisposable
{
    private readonly RadixIndex<string, string> _index;

    public RadixPrefixSearchIndex(IRadixKeyCodec<string>? codec = null)
    {
        _index = new RadixIndex<string, string>(
            codec ?? RadixKeyCodecs.Utf16Ordinal,
            new RadixIndexOptions { MutationMode = RadixIndexMutationMode.Direct },
            itemEqualityComparer: StringComparer.Ordinal);
    }

    public void Add(string value, string physicalKey)
        => _index.Add(value, physicalKey);

    public void Remove(string value, string physicalKey)
        => _index.Remove(value, physicalKey);

    public int EstimateCount(string prefix)
        => _index.CountByPrefix(prefix);

    public HashSet<string> Search(string prefix)
    {
        var result = new HashSet<string>(EstimateCount(prefix), StringComparer.Ordinal);
        _index.VisitByPrefix(prefix, physicalKey => result.Add(physicalKey));
        return result;
    }

    public void Clear() => _index.Clear();

    public void Dispose() => _index.Dispose();
}

internal sealed class RadixSuffixSearchIndex : IDisposable
{
    private readonly RadixPrefixSearchIndex _reversedIndex = new(
        ReverseUtf16OrdinalRadixKeyCodec.Instance);

    public void Add(string value, string physicalKey)
        => _reversedIndex.Add(value, physicalKey);

    public void Remove(string value, string physicalKey)
        => _reversedIndex.Remove(value, physicalKey);

    public int EstimateCount(string suffix)
        => _reversedIndex.EstimateCount(suffix);

    public HashSet<string> Search(string suffix)
        => _reversedIndex.Search(suffix);

    public void Clear() => _reversedIndex.Clear();

    public void Dispose() => _reversedIndex.Dispose();

}

internal sealed class ReverseUtf16OrdinalRadixKeyCodec : IRadixKeyCodec<string>
{
    public static ReverseUtf16OrdinalRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return checked(key.Length * sizeof(char));
    }

    public int Encode(string key, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        var requiredLength = GetEncodedLength(key);
        if (destination.Length < requiredLength)
        {
            throw new ArgumentException("The destination is too short.", nameof(destination));
        }

        for (var sourceIndex = key.Length - 1; sourceIndex >= 0; sourceIndex--)
        {
            var outputIndex = (key.Length - sourceIndex - 1) * sizeof(char);
            BinaryPrimitives.WriteUInt16BigEndian(destination[outputIndex..], key[sourceIndex]);
        }

        return requiredLength;
    }
}

internal interface IRangeSearchIndex : IDisposable
{
    void Add(object value, string physicalKey);

    void Remove(object value, string physicalKey);

    int EstimateCount(SearchComparisonOperator comparisonOperator, object value);

    int EstimateCount(ObjectRangeQuery query);

    HashSet<string> Search(SearchComparisonOperator comparisonOperator, object value);

    HashSet<string> Search(ObjectRangeQuery query);

    void Clear();
}

internal readonly record struct ObjectRangeQuery(
    bool HasLowerBound,
    object? LowerBound,
    bool IncludeLowerBound,
    bool HasUpperBound,
    object? UpperBound,
    bool IncludeUpperBound)
{
    public static ObjectRangeQuery From(SearchComparisonOperator comparisonOperator, object value)
        => comparisonOperator switch
        {
            SearchComparisonOperator.Equal => new(true, value, true, true, value, true),
            SearchComparisonOperator.GreaterThan => new(true, value, false, false, null, false),
            SearchComparisonOperator.GreaterThanOrEqual => new(true, value, true, false, null, false),
            SearchComparisonOperator.LessThan => new(false, null, false, true, value, false),
            SearchComparisonOperator.LessThanOrEqual => new(false, null, false, true, value, true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(comparisonOperator),
                comparisonOperator,
                "The comparison cannot be represented as an ordered range.")
        };
}

internal static class RangeSearchIndexFactory
{
    public static IRangeSearchIndex Create(Type valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        var indexType = typeof(AdaptiveRangeSearchIndex<>).MakeGenericType(valueType);
        return (IRangeSearchIndex)(Activator.CreateInstance(indexType)
            ?? throw new InvalidOperationException($"Unable to create a range index for '{valueType.FullName}'."));
    }
}

internal sealed class AdaptiveRangeSearchIndex<T> : IRangeSearchIndex
    where T : notnull
{
    internal const int SortedBucketMaximumDistinctValues = 1_024;
    internal const int RadixMinimumDistinctValues = 4_096;

    private readonly Func<IRangeIndexStrategy<T>>? _radixFactory =
        RadixRangeIndexStrategyFactory.CreateFactory<T>();
    private IRangeIndexStrategy<T> _strategy = new SortedBucketRangeIndexStrategy<T>();

    internal int DistinctValueCount => _strategy.DistinctValueCount;

    internal bool UsesRadixStrategy => _strategy is RadixRangeIndexStrategy<T>;

    public void Add(object value, string physicalKey)
    {
        _strategy.Add((T)value, physicalKey);
        if (_strategy is SortedBucketRangeIndexStrategy<T>
            && _radixFactory is not null
            && _strategy.DistinctValueCount >= RadixMinimumDistinctValues)
        {
            ReplaceStrategy(_radixFactory());
        }
    }

    public void Remove(object value, string physicalKey)
    {
        _strategy.Remove((T)value, physicalKey);
        if (_strategy is RadixRangeIndexStrategy<T>
            && _strategy.DistinctValueCount <= SortedBucketMaximumDistinctValues)
        {
            ReplaceStrategy(new SortedBucketRangeIndexStrategy<T>());
        }
    }

    public int EstimateCount(SearchComparisonOperator comparisonOperator, object value)
        => EstimateCount(ObjectRangeQuery.From(comparisonOperator, value));

    public int EstimateCount(ObjectRangeQuery query)
        => _strategy.EstimateCount(RangeQuery<T>.From(query));

    public HashSet<string> Search(SearchComparisonOperator comparisonOperator, object value)
        => Search(ObjectRangeQuery.From(comparisonOperator, value));

    public HashSet<string> Search(ObjectRangeQuery query)
        => _strategy.Search(RangeQuery<T>.From(query));

    public void Clear()
    {
        if (_strategy is SortedBucketRangeIndexStrategy<T>)
        {
            _strategy.Clear();
            return;
        }

        _strategy.Dispose();
        _strategy = new SortedBucketRangeIndexStrategy<T>();
    }

    public void Dispose() => _strategy.Dispose();

    private void ReplaceStrategy(IRangeIndexStrategy<T> replacement)
    {
        _strategy.VisitAll(replacement.Add);
        _strategy.Dispose();
        _strategy = replacement;
    }
}

internal readonly record struct RangeQuery<T>(
    bool HasLowerBound,
    T LowerBound,
    bool IncludeLowerBound,
    bool HasUpperBound,
    T UpperBound,
    bool IncludeUpperBound)
    where T : notnull
{
    public static RangeQuery<T> From(ObjectRangeQuery query)
        => new(
            query.HasLowerBound,
            query.HasLowerBound ? (T)query.LowerBound! : default!,
            query.IncludeLowerBound,
            query.HasUpperBound,
            query.HasUpperBound ? (T)query.UpperBound! : default!,
            query.IncludeUpperBound);

    public static RangeQuery<T> From(SearchComparisonOperator comparisonOperator, T value)
        => comparisonOperator switch
        {
            SearchComparisonOperator.Equal => new(true, value, true, true, value, true),
            SearchComparisonOperator.GreaterThan => new(true, value, false, false, default!, false),
            SearchComparisonOperator.GreaterThanOrEqual => new(true, value, true, false, default!, false),
            SearchComparisonOperator.LessThan => new(false, default!, false, true, value, false),
            SearchComparisonOperator.LessThanOrEqual => new(false, default!, false, true, value, true),
            _ => throw new ArgumentOutOfRangeException(
                nameof(comparisonOperator),
                comparisonOperator,
                "The comparison cannot be represented as an ordered range.")
        };

    public RadixKeyRange<T> ToRadixRange()
    {
        if (HasLowerBound && HasUpperBound)
        {
            return RadixKeyRange.Between(
                LowerBound,
                UpperBound,
                IncludeLowerBound,
                IncludeUpperBound);
        }

        if (HasLowerBound)
        {
            return IncludeLowerBound
                ? RadixKeyRange.GreaterThanOrEqual(LowerBound)
                : RadixKeyRange.GreaterThan(LowerBound);
        }

        if (HasUpperBound)
        {
            return IncludeUpperBound
                ? RadixKeyRange.LessThanOrEqual(UpperBound)
                : RadixKeyRange.LessThan(UpperBound);
        }

        return RadixKeyRange.All<T>();
    }
}

internal interface IRangeIndexStrategy<T> : IDisposable
    where T : notnull
{
    int DistinctValueCount { get; }

    void Add(T value, string physicalKey);

    bool Remove(T value, string physicalKey);

    int EstimateCount(RangeQuery<T> query);

    HashSet<string> Search(RangeQuery<T> query);

    void VisitAll(Action<T, string> visitor);

    void Clear();
}

internal sealed class SortedBucketRangeIndexStrategy<T> : IRangeIndexStrategy<T>
    where T : notnull
{
    private readonly SortedList<T, HashSet<string>> _buckets = new(
        typeof(T) == typeof(string) ? (IComparer<T>)(object)StringComparer.Ordinal : Comparer<T>.Default);

    public int DistinctValueCount => _buckets.Count;

    public void Add(T value, string physicalKey)
    {
        if (!_buckets.TryGetValue(value, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            _buckets.Add(value, keys);
        }

        keys.Add(physicalKey);
    }

    public bool Remove(T value, string physicalKey)
    {
        if (!_buckets.TryGetValue(value, out var keys) || !keys.Remove(physicalKey))
        {
            return false;
        }

        if (keys.Count == 0)
        {
            _buckets.Remove(value);
        }

        return true;
    }

    public int EstimateCount(RangeQuery<T> query)
    {
        var (start, end) = GetBounds(query);
        var count = 0;
        for (var index = start; index < end; index++)
        {
            count += _buckets.Values[index].Count;
        }

        return count;
    }

    public HashSet<string> Search(RangeQuery<T> query)
    {
        var (start, end) = GetBounds(query);
        var result = new HashSet<string>(EstimateCount(query), StringComparer.Ordinal);
        for (var index = start; index < end; index++)
        {
            result.UnionWith(_buckets.Values[index]);
        }

        return result;
    }

    public void VisitAll(Action<T, string> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        for (var index = 0; index < _buckets.Count; index++)
        {
            var value = _buckets.Keys[index];
            foreach (var physicalKey in _buckets.Values[index])
            {
                visitor(value, physicalKey);
            }
        }
    }

    public void Clear() => _buckets.Clear();

    public void Dispose() => Clear();

    private (int Start, int End) GetBounds(RangeQuery<T> query)
    {
        var start = !query.HasLowerBound
            ? 0
            : query.IncludeLowerBound
                ? LowerBound(query.LowerBound)
                : UpperBound(query.LowerBound);
        var end = !query.HasUpperBound
            ? _buckets.Count
            : query.IncludeUpperBound
                ? UpperBound(query.UpperBound)
                : LowerBound(query.UpperBound);
        return start <= end ? (start, end) : (start, start);
    }

    private int LowerBound(T value)
    {
        var low = 0;
        var high = _buckets.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_buckets.Comparer.Compare(_buckets.Keys[middle], value) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int UpperBound(T value)
    {
        var low = 0;
        var high = _buckets.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_buckets.Comparer.Compare(_buckets.Keys[middle], value) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

internal readonly record struct RangeIndexEntry<T>(T Value, string PhysicalKey)
    where T : notnull;

internal sealed class RadixRangeIndexStrategy<T> : IRangeIndexStrategy<T>
    where T : notnull
{
    private readonly RadixIndex<T, RangeIndexEntry<T>> _index;
    private readonly Dictionary<T, int> _valueCounts = new();

    public RadixRangeIndexStrategy(IRadixKeyCodec<T> codec)
    {
        _index = new RadixIndex<T, RangeIndexEntry<T>>(
            codec,
            new RadixIndexOptions { MutationMode = RadixIndexMutationMode.Direct });
    }

    public int DistinctValueCount => _valueCounts.Count;

    public void Add(T value, string physicalKey)
    {
        _index.Add(value, new RangeIndexEntry<T>(value, physicalKey));
        _valueCounts.TryGetValue(value, out var count);
        _valueCounts[value] = count + 1;
    }

    public bool Remove(T value, string physicalKey)
    {
        if (!_index.Remove(value, new RangeIndexEntry<T>(value, physicalKey)))
        {
            return false;
        }

        var count = _valueCounts[value] - 1;
        if (count == 0)
        {
            _valueCounts.Remove(value);
        }
        else
        {
            _valueCounts[value] = count;
        }

        return true;
    }

    public int EstimateCount(RangeQuery<T> query)
        => _index.CountInRange(query.ToRadixRange());

    public HashSet<string> Search(RangeQuery<T> query)
    {
        var result = new HashSet<string>(EstimateCount(query), StringComparer.Ordinal);
        _index.VisitInRange(query.ToRadixRange(), entry => result.Add(entry.PhysicalKey));
        return result;
    }

    public void VisitAll(Action<T, string> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        _index.VisitInRange(
            RadixKeyRange.All<T>(),
            entry => visitor(entry.Value, entry.PhysicalKey));
    }

    public void Clear()
    {
        _index.Clear();
        _valueCounts.Clear();
    }

    public void Dispose() => _index.Dispose();
}

internal static class RadixRangeIndexStrategyFactory
{
    public static Func<IRangeIndexStrategy<T>>? CreateFactory<T>()
        where T : notnull
    {
        var codec = TryGetCodec<T>();
        return codec is null ? null : () => new RadixRangeIndexStrategy<T>(codec);
    }

    private static IRadixKeyCodec<T>? TryGetCodec<T>()
        where T : notnull
    {
        if (typeof(T) == typeof(string))
        {
            return Cast<T, string>(RadixKeyCodecs.Utf16Ordinal);
        }

        if (typeof(T) == typeof(byte)) return Cast<T, byte>(RadixKeyCodecs.Byte);
        if (typeof(T) == typeof(sbyte)) return Cast<T, sbyte>(RadixKeyCodecs.SByte);
        if (typeof(T) == typeof(char)) return Cast<T, char>(RadixKeyCodecs.Char);
        if (typeof(T) == typeof(short)) return Cast<T, short>(RadixKeyCodecs.Int16);
        if (typeof(T) == typeof(ushort)) return Cast<T, ushort>(RadixKeyCodecs.UInt16);
        if (typeof(T) == typeof(int)) return Cast<T, int>(RadixKeyCodecs.Int32);
        if (typeof(T) == typeof(uint)) return Cast<T, uint>(RadixKeyCodecs.UInt32);
        if (typeof(T) == typeof(long)) return Cast<T, long>(RadixKeyCodecs.Int64);
        if (typeof(T) == typeof(ulong)) return Cast<T, ulong>(RadixKeyCodecs.UInt64);
        if (typeof(T) == typeof(float)) return Cast<T, float>(SingleRadixKeyCodec.Instance);
        if (typeof(T) == typeof(double)) return Cast<T, double>(DoubleRadixKeyCodec.Instance);
        if (typeof(T) == typeof(decimal)) return Cast<T, decimal>(DecimalRadixKeyCodec.Instance);
        if (typeof(T) == typeof(DateTime)) return Cast<T, DateTime>(DateTimeRadixKeyCodec.Instance);
        if (typeof(T) == typeof(DateTimeOffset)) return Cast<T, DateTimeOffset>(DateTimeOffsetRadixKeyCodec.Instance);
        if (typeof(T) == typeof(TimeSpan)) return Cast<T, TimeSpan>(TimeSpanRadixKeyCodec.Instance);
        return null;
    }

    private static IRadixKeyCodec<TTarget> Cast<TTarget, TSource>(IRadixKeyCodec<TSource> codec)
        where TTarget : notnull
        where TSource : notnull
        => (IRadixKeyCodec<TTarget>)(object)codec;
}

internal sealed class DateTimeRadixKeyCodec : IRadixKeyCodec<DateTime>
{
    public static DateTimeRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(DateTime key) => sizeof(long);

    public int Encode(DateTime key, Span<byte> destination)
        => RadixKeyCodecs.Int64.Encode(key.Ticks, destination);
}

internal sealed class DateTimeOffsetRadixKeyCodec : IRadixKeyCodec<DateTimeOffset>
{
    public static DateTimeOffsetRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(DateTimeOffset key) => sizeof(long);

    public int Encode(DateTimeOffset key, Span<byte> destination)
        => RadixKeyCodecs.Int64.Encode(key.UtcDateTime.Ticks, destination);
}

internal sealed class TimeSpanRadixKeyCodec : IRadixKeyCodec<TimeSpan>
{
    public static TimeSpanRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(TimeSpan key) => sizeof(long);

    public int Encode(TimeSpan key, Span<byte> destination)
        => RadixKeyCodecs.Int64.Encode(key.Ticks, destination);
}

internal sealed class SingleRadixKeyCodec : IRadixKeyCodec<float>
{
    public static SingleRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(float key) => sizeof(float);

    public int Encode(float key, Span<byte> destination)
    {
        var bits = float.IsNaN(key)
            ? 0u
            : ToSortableBits(key == 0f ? 0f : key);
        BinaryPrimitives.WriteUInt32BigEndian(destination, bits);
        return sizeof(float);
    }

    private static uint ToSortableBits(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        return (bits & 0x8000_0000u) == 0
            ? bits ^ 0x8000_0000u
            : ~bits;
    }
}

internal sealed class DoubleRadixKeyCodec : IRadixKeyCodec<double>
{
    public static DoubleRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(double key) => sizeof(double);

    public int Encode(double key, Span<byte> destination)
    {
        var bits = double.IsNaN(key)
            ? 0ul
            : ToSortableBits(key == 0d ? 0d : key);
        BinaryPrimitives.WriteUInt64BigEndian(destination, bits);
        return sizeof(double);
    }

    private static ulong ToSortableBits(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        return (bits & 0x8000_0000_0000_0000ul) == 0
            ? bits ^ 0x8000_0000_0000_0000ul
            : ~bits;
    }
}

internal sealed class DecimalRadixKeyCodec : IRadixKeyCodec<decimal>
{
    private const int EncodedLength = 25;
    private static readonly BigInteger[] PowersOfTen = CreatePowersOfTen();

    public static DecimalRadixKeyCodec Instance { get; } = new();

    public int GetEncodedLength(decimal key) => EncodedLength;

    public int Encode(decimal key, Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("The destination is too short.", nameof(destination));
        }

        Span<int> bits = stackalloc int[4];
        decimal.GetBits(key, bits);
        var coefficient = new BigInteger(unchecked((uint)bits[0]))
            | (new BigInteger(unchecked((uint)bits[1])) << 32)
            | (new BigInteger(unchecked((uint)bits[2])) << 64);
        var scale = (bits[3] >> 16) & 0x7f;
        coefficient *= PowersOfTen[28 - scale];

        destination[..EncodedLength].Clear();
        destination[0] = 1;
        if (coefficient.IsZero)
        {
            return EncodedLength;
        }

        Span<byte> littleEndian = stackalloc byte[24];
        if (!coefficient.TryWriteBytes(
                littleEndian,
                out var bytesWritten,
                isUnsigned: true,
                isBigEndian: false))
        {
            throw new InvalidOperationException("The decimal magnitude exceeded its canonical radix encoding.");
        }

        for (var index = 0; index < bytesWritten; index++)
        {
            destination[EncodedLength - 1 - index] = littleEndian[index];
        }

        var negative = (bits[3] & int.MinValue) != 0;
        if (negative)
        {
            destination[0] = 0;
            for (var index = 1; index < EncodedLength; index++)
            {
                destination[index] = (byte)~destination[index];
            }
        }

        return EncodedLength;
    }

    private static BigInteger[] CreatePowersOfTen()
    {
        var powers = new BigInteger[29];
        powers[0] = BigInteger.One;
        for (var index = 1; index < powers.Length; index++)
        {
            powers[index] = powers[index - 1] * 10;
        }

        return powers;
    }
}
