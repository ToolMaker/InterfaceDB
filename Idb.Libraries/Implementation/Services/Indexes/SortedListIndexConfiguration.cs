using System.Collections.Generic;
using System.Linq;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Libraries.Implementation.Services.Indexes;

internal interface ISortedListIndex
{
    Type ContractType { get; }
    string Name { get; }
    Type KeyType { get; }
    object? Add(object entity);
    IReadOnlyList<object> Snapshot(object? key = null);
    bool TryAs<TContract, TKey>(out SortedList<TKey, List<TContract>> index)
        where TContract : class
        where TKey : notnull;
}

internal interface ISortedListIndexFactory
{
    ISortedListIndex CreateIndex();
}

public sealed class SortedListIndexConfiguration<TContract, TKey> : IRepositoryIndexConfiguration, ISortedListIndexFactory
    where TContract : class
    where TKey : notnull
{
    private static readonly IReadOnlyDictionary<string, object?> EmptySettings = new Dictionary<string, object?>();

    public SortedListIndexConfiguration(
        string name,
        Func<TContract, TKey> keySelector,
        IComparer<TKey>? comparer = null,
        IReadOnlyDictionary<string, object?>? settings = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name cannot be null or whitespace.", nameof(name));
        }

        if (!typeof(TContract).IsInterface)
        {
            throw new InvalidOperationException($"Contract type '{typeof(TContract).FullName}' must be an interface.");
        }

        Name = name.Trim();
        KeySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        Comparer = comparer ?? Comparer<TKey>.Default;
        Settings = settings ?? EmptySettings;
    }

    public Type ContractType => typeof(TContract);
    public string Name { get; }
    public RepositoryIndexKind Kind => RepositoryIndexKind.SortedList;
    public IReadOnlyDictionary<string, object?> Settings { get; }
    internal Type KeyType => typeof(TKey);

    internal Func<TContract, TKey> KeySelector { get; }
    internal IComparer<TKey> Comparer { get; }

    ISortedListIndex ISortedListIndexFactory.CreateIndex()
        => new SortedListIndex<TContract, TKey>(this);
}

internal sealed class SortedListIndex<TContract, TKey> : ISortedListIndex
    where TContract : class
    where TKey : notnull
{
    private readonly SortedListIndexConfiguration<TContract, TKey> _configuration;
    private readonly SortedList<TKey, List<TContract>> _data;
    private readonly object _syncRoot = new();

    public SortedListIndex(SortedListIndexConfiguration<TContract, TKey> configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _data = new SortedList<TKey, List<TContract>>(configuration.Comparer);
    }

    public Type ContractType => typeof(TContract);
    public string Name => _configuration.Name;
    public Type KeyType => _configuration.KeyType;

    public object? Add(object entity)
    {
        if (entity is not TContract typed)
        {
            throw new InvalidOperationException($"Entity of type '{entity?.GetType().FullName ?? "<null>"}' cannot be indexed by '{typeof(TContract).FullName}'.");
        }

        var key = _configuration.KeySelector(typed);

        lock (_syncRoot)
        {
            if (!_data.TryGetValue(key, out var bucket))
            {
                bucket = new List<TContract>();
                _data.Add(key, bucket);
            }

            bucket.Add(typed);
        }

        return key;
    }

    public IReadOnlyList<object> Snapshot(object? key = null)
    {
        lock (_syncRoot)
        {
            if (key is null)
            {
                return _data.Values.SelectMany(list => list.Cast<object>()).ToList();
            }

            if (key is not TKey typedKey)
            {
                return Array.Empty<object>();
            }

            return _data.TryGetValue(typedKey, out var bucket)
                ? bucket.Cast<object>().ToList()
                : Array.Empty<object>();
        }
    }

    public bool TryAs<TRequestedContract, TRequestedKey>(out SortedList<TRequestedKey, List<TRequestedContract>> index)
        where TRequestedContract : class
        where TRequestedKey : notnull
    {
        if (typeof(TRequestedContract) == typeof(TContract) && typeof(TRequestedKey) == typeof(TKey))
        {
            index = (SortedList<TRequestedKey, List<TRequestedContract>>)(object)_data;
            return true;
        }

        index = null!;
        return false;
    }
}
