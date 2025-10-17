using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Services.Indexes;

internal interface ISortedListIndex : IRepositoryIndex
{
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
        Expression<Func<TContract, TKey>> keySelector,
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
        KeySelectorExpression = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        KeySelector = KeySelectorExpression.Compile();
        KeyMembers = ExtractKeyMembers(KeySelectorExpression);
        Comparer = comparer ?? Comparer<TKey>.Default;
        Settings = settings ?? EmptySettings;
    }

    public Type ContractType => typeof(TContract);
    public string Name { get; }
    public IndexTypeEnum Kind => IndexTypeEnum.SortedList;
    public IReadOnlyDictionary<string, object?> Settings { get; }
    internal Type KeyType => typeof(TKey);
    internal Expression<Func<TContract, TKey>> KeySelectorExpression { get; }
    internal Func<TContract, TKey> KeySelector { get; }
    internal IComparer<TKey> Comparer { get; }
    internal IReadOnlyList<string> KeyMembers { get; }

    ISortedListIndex ISortedListIndexFactory.CreateIndex()
        => new SortedListIndex<TContract, TKey>(this);

    private static IReadOnlyList<string> ExtractKeyMembers(Expression<Func<TContract, TKey>> keySelector)
    {
        if (keySelector is null)
        {
            return Array.Empty<string>();
        }

        var members = new List<string>();
        Expression? current = keySelector.Body;

        while (current is not null)
        {
            switch (current)
            {
                case MemberExpression memberExpression:
                    members.Insert(0, memberExpression.Member.Name);
                    current = memberExpression.Expression switch
                    {
                        null => null,
                        ParameterExpression => null,
                        _ => memberExpression.Expression
                    };
                    break;

                case UnaryExpression unaryExpression when unaryExpression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                    current = unaryExpression.Operand;
                    break;

                default:
                    return Array.Empty<string>();
            }
        }

        return members.Count == 0 ? Array.Empty<string>() : members.ToArray();
    }
}

internal sealed class SortedListIndex<TContract, TKey> : ISortedListIndex
    where TContract : class
    where TKey : notnull
{
    private static readonly Func<TContract, string?> IdAccessor = CreateIdAccessor();

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
    public IReadOnlyList<string> KeyMembers => _configuration.KeyMembers;
    public IndexTypeEnum Kind => IndexTypeEnum.SortedList;

    public object? Add(object entity)
    {
        if (entity is not TContract typed)
        {
            throw new InvalidOperationException($"Entity of type '{entity?.GetType().FullName ?? "<null>"}' cannot be indexed by '{typeof(TContract).FullName}'.");
        }

        var id = IdAccessor(typed);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var key = _configuration.KeySelector(typed);

        lock (_syncRoot)
        {
            if (!_data.TryGetValue(key, out var bucket))
            {
                bucket = new List<TContract>();
                _data.Add(key, bucket);
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                var existingIndex = bucket.FindIndex(item => string.Equals(IdAccessor(item), id, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    bucket[existingIndex] = typed;
                }
                else
                {
                    bucket.Add(typed);
                }
            }
            else if (!bucket.Contains(typed))
            {
                bucket.Add(typed);
            }
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

    public bool TryExtractKey(object probe, out object? key, out int matchedMembers)
    {
        if (probe is null)
        {
            key = null;
            matchedMembers = 0;
            return false;
        }

        if (probe is TContract typed)
        {
            key = _configuration.KeySelector(typed);
            matchedMembers = _configuration.KeyMembers.Count;
            return true;
        }

        if (_configuration.KeyMembers.Count == 0)
        {
            key = null;
            matchedMembers = 0;
            return false;
        }

        if (RepositoryIndexProbeAccessor.TryGetValue(
                probe,
                _configuration.KeyMembers,
                out var extracted,
                out matchedMembers))
        {
            key = extracted;
            return true;
        }

        key = null;
        matchedMembers = 0;
        return false;
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

    private static Func<TContract, string?> CreateIdAccessor()
    {
        var property = typeof(TContract)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(string));

        if (property is null)
        {
            return _ => null;
        }

        if (!property.CanRead)
        {
            return _ => null;
        }

        var instance = Expression.Parameter(typeof(TContract), "instance");
        var propertyAccess = Expression.Property(instance, property);
        var convert = Expression.Convert(propertyAccess, typeof(string));
        return Expression.Lambda<Func<TContract, string?>>(convert, instance).Compile();
    }
}

