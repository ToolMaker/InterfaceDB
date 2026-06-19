using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Indexes;

internal static class SearchIndexDiscovery
{
    public static IReadOnlyList<SearchIndexDefinition> Discover(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        var definitions = new List<SearchIndexDefinition>();
        DiscoverType(contractType, inheritedIndexed: false, prefix: string.Empty, accessors: Array.Empty<MemberInfo>(), definitions);
        return definitions;
    }

    private static void DiscoverType(
        Type type,
        bool inheritedIndexed,
        string prefix,
        IReadOnlyList<MemberInfo> accessors,
        List<SearchIndexDefinition> definitions)
    {
        var scopeIndexed = ResolveScopeIndexed(type, inheritedIndexed);
        foreach (var member in GetPublicReadableMembers(type))
        {
            if (member.GetCustomAttribute<NotSearchIndexedAttribute>() is not null)
            {
                continue;
            }

            var memberIndex = member.GetCustomAttribute<SearchIndexAttribute>();
            var memberIndexed = memberIndex is not null || scopeIndexed;
            if (!memberIndexed)
            {
                continue;
            }

            var memberType = GetMemberType(member);
            var path = string.IsNullOrWhiteSpace(prefix) ? member.Name : prefix + "." + member.Name;
            var nextAccessors = accessors.Concat(new[] { member }).ToArray();

            if (IsIndexableScalar(memberType))
            {
                var mode = NormalizeMode(memberType, memberIndex?.Mode ?? SearchIndexMode.Default);
                if (mode is not SearchIndexMode.Default)
                {
                    definitions.Add(new SearchIndexDefinition(
                        path,
                        Nullable.GetUnderlyingType(memberType) ?? memberType,
                        mode,
                        CreateGetter(nextAccessors)));
                }

                continue;
            }

            if (memberIndex is not null && IsComplexIndexCandidate(memberType))
            {
                DiscoverType(memberType, inheritedIndexed: true, path, nextAccessors, definitions);
            }
        }
    }

    private static bool ResolveScopeIndexed(MemberInfo type, bool inheritedIndexed)
    {
        if (type.GetCustomAttribute<NotSearchIndexedAttribute>() is not null)
        {
            return false;
        }

        return type.GetCustomAttribute<SearchIndexAttribute>() is not null || inheritedIndexed;
    }

    private static IEnumerable<MemberInfo> GetPublicReadableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (var property in type.GetProperties(flags))
        {
            if (property.GetMethod is not null && property.GetIndexParameters().Length == 0)
            {
                yield return property;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            yield return field;
        }
    }

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException($"Unsupported index member '{member.Name}'.")
        };

    private static object? GetValue(MemberInfo member, object instance)
        => member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => throw new NotSupportedException($"Unsupported index member '{member.Name}'.")
        };

    private static Func<object, object?> CreateGetter(IReadOnlyList<MemberInfo> accessors)
    {
        return instance =>
        {
            object? current = instance;
            foreach (var accessor in accessors)
            {
                if (current is null)
                {
                    return null;
                }

                current = GetValue(accessor, current);
            }

            return current;
        };
    }

    private static bool IsComplexIndexCandidate(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return !IsIndexableScalar(actual)
            && actual != typeof(object)
            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(actual);
    }

    private static bool IsIndexableScalar(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        return actual.IsEnum
            || actual == typeof(string)
            || actual == typeof(bool)
            || actual == typeof(char)
            || actual == typeof(Guid)
            || actual == typeof(DateTime)
            || actual == typeof(DateTimeOffset)
            || actual == typeof(TimeSpan)
            || actual == typeof(byte)
            || actual == typeof(sbyte)
            || actual == typeof(short)
            || actual == typeof(ushort)
            || actual == typeof(int)
            || actual == typeof(uint)
            || actual == typeof(long)
            || actual == typeof(ulong)
            || actual == typeof(float)
            || actual == typeof(double)
            || actual == typeof(decimal);
    }

    private static SearchIndexMode NormalizeMode(Type type, SearchIndexMode requested)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (requested is SearchIndexMode.Default)
        {
            if (actual == typeof(string))
            {
                return SearchIndexMode.Exact | SearchIndexMode.Prefix;
            }

            if (actual == typeof(bool) || actual.IsEnum || actual == typeof(Guid))
            {
                return SearchIndexMode.Exact;
            }

            return SearchIndexMode.Exact | SearchIndexMode.Range;
        }

        var mode = requested;
        if (actual != typeof(string))
        {
            mode &= ~SearchIndexMode.Prefix;
        }

        if (!typeof(IComparable).IsAssignableFrom(actual)
            && !actual.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IComparable<>)))
        {
            mode &= ~SearchIndexMode.Range;
        }

        return mode;
    }
}
