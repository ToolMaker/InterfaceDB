using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Idb.Libraries.Implementation.Services.Indexes;

internal static class RepositoryIndexProbeAccessor
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Func<object, object?>>> PropertyAccessorCache = new();

    public static bool TryGetValue(
        object probe,
        IReadOnlyList<string> keyMembers,
        out object? value,
        out int matchedMembers)
    {
        value = null;
        matchedMembers = 0;

        if (probe is null || keyMembers.Count == 0)
        {
            return false;
        }

        object? current = probe;

        for (var i = 0; i < keyMembers.Count; i++)
        {
            var memberName = keyMembers[i];

            if (!TryReadMember(current, memberName, out var next))
            {
                matchedMembers = i;
                value = null;
                return false;
            }

            current = next;

            if (current is null && i < keyMembers.Count - 1)
            {
                matchedMembers = i + 1;
                value = null;
                return false;
            }
        }

        matchedMembers = keyMembers.Count;
        value = current;
        return true;
    }

    public static IReadOnlyDictionary<string, object?> GetMemberSnapshot(object probe)
    {
        if (probe is null)
        {
            return EmptyDictionary.Instance;
        }

        if (probe is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            return CopyDictionary(readOnlyDictionary);
        }

        if (probe is IReadOnlyDictionary<string, object> readOnlyDictionaryNonNullable)
        {
            return readOnlyDictionaryNonNullable.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        if (probe is IDictionary<string, object?> dictionary)
        {
            return new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        if (probe is IDictionary<string, object> dictionaryNonNullable)
        {
            return dictionaryNonNullable.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        if (probe is IDictionary dictionaryGeneric)
        {
            return CopyDictionary(dictionaryGeneric);
        }

        var accessors = PropertyAccessorCache.GetOrAdd(probe.GetType(), BuildAccessorMap);
        if (accessors.Count == 0)
        {
            return EmptyDictionary.Instance;
        }

        var snapshot = new Dictionary<string, object?>(accessors.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, getter) in accessors)
        {
            snapshot[name] = getter(probe);
        }

        return snapshot;
    }

    private static bool TryReadMember(object? source, string memberName, out object? value)
    {
        value = null;

        if (source is null)
        {
            return false;
        }

        if (TryReadFromDictionary(source, memberName, out value))
        {
            return true;
        }

        return TryReadFromObject(source, memberName, out value);
    }

    private static bool TryReadFromDictionary(object source, string memberName, out object? value)
    {
        value = null;

        if (source is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            readOnlyDictionary.TryGetValue(memberName, out value))
        {
            return true;
        }

        if (source is IReadOnlyDictionary<string, object> readOnlyDictionaryNonNullable &&
            readOnlyDictionaryNonNullable.TryGetValue(memberName, out var nonNullableValue))
        {
            value = nonNullableValue;
            return true;
        }

        if (source is IDictionary<string, object?> dictionary &&
            dictionary.TryGetValue(memberName, out value))
        {
            return true;
        }

        if (source is IDictionary<string, object> dictionaryNonNullable &&
            dictionaryNonNullable.TryGetValue(memberName, out var dictionaryNonNullableValue))
        {
            value = dictionaryNonNullableValue;
            return true;
        }

        if (source is IDictionary dictionaryGeneric)
        {
            foreach (DictionaryEntry entry in dictionaryGeneric)
            {
                if (entry.Key is string key && string.Equals(key, memberName, StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadFromObject(object source, string memberName, out object? value)
    {
        var type = source.GetType();
        var accessors = PropertyAccessorCache.GetOrAdd(type, BuildAccessorMap);

        if (accessors.TryGetValue(memberName, out var getter))
        {
            value = getter(source);
            return true;
        }

        value = null;
        return false;
    }

    private static IReadOnlyDictionary<string, Func<object, object?>> BuildAccessorMap(Type type)
    {
        var map = new Dictionary<string, Func<object, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var getter = property.GetMethod;
            if (getter is null)
            {
                continue;
            }

            var instanceParameter = Expression.Parameter(typeof(object), "instance");
            var castInstance = Expression.Convert(instanceParameter, type);
            var call = Expression.Call(castInstance, getter);
            var boxed = Expression.Convert(call, typeof(object));
            var lambda = Expression.Lambda<Func<object, object?>>(boxed, instanceParameter);
            map[property.Name] = lambda.Compile();
        }

        return map;
    }

    private static IReadOnlyDictionary<string, object?> CopyDictionary(IReadOnlyDictionary<string, object?> source)
    {
        if (source.Count == 0)
        {
            return EmptyDictionary.Instance;
        }

        return source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, object?> CopyDictionary(IDictionary dictionary)
    {
        if (dictionary.Count == 0)
        {
            return EmptyDictionary.Instance;
        }

        var snapshot = new Dictionary<string, object?>(dictionary.Count, StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key)
            {
                snapshot[key] = entry.Value;
            }
        }

        return snapshot;
    }

    private sealed class EmptyDictionary : IReadOnlyDictionary<string, object?>
    {
        public static readonly EmptyDictionary Instance = new();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, object?>>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => 0;
        public object? this[string key] => throw new KeyNotFoundException();
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<object?> Values => Array.Empty<object?>();
        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out object? value)
        {
            value = null;
            return false;
        }
    }
}

