using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Implementation.Indexes;

internal static class SearchIndexDiscovery
{
    private static readonly ConditionalWeakTable<Type, Lazy<IReadOnlyList<SearchIndexDefinition>>> Definitions = new();

    public static IReadOnlyList<SearchIndexDefinition> Discover(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return Definitions.GetValue(contractType, static type => new(() =>
        {
            var definitions = new List<SearchIndexDefinition>();
            DiscoverType(type, inheritedIndexed: false, prefix: string.Empty, accessors: Array.Empty<MemberInfo>(), definitions);
            return definitions.ToArray();
        })).Value;
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
                    var valueType = Nullable.GetUnderlyingType(memberType) ?? memberType;
                    definitions.Add(new SearchIndexDefinition(
                        path,
                        valueType.IsEnum ? Enum.GetUnderlyingType(valueType) : valueType,
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

    internal static Func<object, object?> CreateGetter(
        IReadOnlyList<MemberInfo> accessors,
        bool? preferInterpretation = null)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var body = ReadMember(Expression.Convert(instance, accessors[0].DeclaringType!), 0);
        return Expression.Lambda<Func<object, object?>>(body, instance)
            .Compile(preferInterpretation ?? !RuntimeFeature.IsDynamicCodeSupported);

        Expression ReadMember(Expression current, int ordinal)
        {
            if (ordinal == accessors.Count)
            {
                return Expression.Convert(current, typeof(object));
            }

            // Evaluate each getter once, including nullable parents and getters with side effects.
            var value = Expression.Variable(current.Type, "parent" + ordinal);
            Expression member = Expression.MakeMemberAccess(value, accessors[ordinal]);
            if (accessors[ordinal] is PropertyInfo)
            {
                // Match PropertyInfo.GetValue's exception contract on the previous reflection path.
                var exception = Expression.Parameter(typeof(Exception), "exception");
                member = Expression.TryCatch(member, Expression.Catch(exception,
                    Expression.Throw(Expression.New(
                        typeof(TargetInvocationException).GetConstructor(new[] { typeof(Exception) })!,
                        exception), member.Type)));
            }

            var next = ReadMember(member, ordinal + 1);
            if (!value.Type.IsValueType || Nullable.GetUnderlyingType(value.Type) is not null)
            {
                next = Expression.Condition(
                    value.Type.IsValueType
                        ? Expression.Not(Expression.Property(value, "HasValue"))
                        : Expression.ReferenceEqual(value, Expression.Constant(null, value.Type)),
                    Expression.Constant(null, typeof(object)),
                    next);
            }

            return Expression.Block(new[] { value }, Expression.Assign(value, current), next);
        }
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
            mode &= ~(SearchIndexMode.Prefix | SearchIndexMode.Suffix | SearchIndexMode.Contains);
        }

        if (!typeof(IComparable).IsAssignableFrom(actual)
            && !actual.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IComparable<>)))
        {
            mode &= ~SearchIndexMode.Range;
        }

        return mode;
    }
}
