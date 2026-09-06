using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Options;
using MessagePack;

namespace Idb.Libraries.Implementation.Repositories;

internal sealed class NotificationComparison<T>
{
    private readonly (PropertyInfo Property, Func<T, object?> Get, IEqualityComparer<object?>? Custom)[] _properties;
    private readonly int _maximumItems;
    private readonly ConditionalWeakTable<Type, object> _validatedTypes = new();

    internal NotificationComparison(RepositoryNotificationOptions options)
    {
        _maximumItems = options.MaximumCollectionItems;
        _properties = typeof(T).GetInterfaces().Append(typeof(T)).SelectMany(type => type.GetProperties())
            .Where(property => property.Name != "Envelope" && !property.IsDefined(typeof(IgnoreMemberAttribute)))
            .GroupBy(property => property.Name, StringComparer.Ordinal).Select(group =>
            {
                if (group.Select(property => property.PropertyType).Distinct().Skip(1).Any())
                    throw new NotSupportedException($"Notification property '{group.Key}' has ambiguous inherited types.");
                return group.First();
            })
            .OrderBy(property => property.Name, StringComparer.Ordinal).Select(property =>
            {
                if (property.GetMethod is null || property.SetMethod is null || property.GetIndexParameters().Length != 0)
                    throw new NotSupportedException($"Notification property '{property.Name}' must be a persisted readable/writable property.");
                options.PropertyComparers.TryGetValue(property.Name, out var comparer);
                if (comparer is null && !Supported(property.PropertyType))
                    throw new NotSupportedException($"Register a value comparer for notification property '{property.Name}'.");
                var entity = Expression.Parameter(typeof(T));
                var get = Expression.Lambda<Func<T, object?>>(
                    Expression.Convert(Expression.Property(entity, property), typeof(object)), entity)
                    .Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
                return (property, get, comparer);
            }).ToArray();
    }

    internal void ValidateRuntime(T entity)
    {
        var type = entity!.GetType();
        // Encrypted proxies reflect a persisted inner contract; comparison goes through its getters.
        if (type.Assembly.IsDynamic) return;
        if (_validatedTypes.TryGetValue(type, out _)) return;
        foreach (var (property, _, _) in _properties)
        {
            var runtime = type.GetProperty(property.Name);
            if (runtime is not null && runtime.IsDefined(typeof(IgnoreMemberAttribute)))
                throw new NotSupportedException($"Notification property '{property.Name}' is ignored by the stored implementation.");
        }
        _validatedTypes.GetValue(type, static _ => new object());
    }

    internal IReadOnlyList<string> Changes(T previous, T current)
    {
        List<string>? changed = null;
        foreach (var (property, get, comparer) in _properties)
        {
            var oldValue = get(previous);
            var newValue = get(current);
            if (!(comparer?.Equals(oldValue, newValue) ?? Equal(oldValue, newValue)))
                (changed ??= []).Add(property.Name);
        }
        return changed is null ? Array.Empty<string>() : changed.AsReadOnly();
    }

    private static bool Supported(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly)) return true;
        if (type.IsArray) return type.GetArrayRank() == 1 && Supported(type.GetElementType()!);
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            && Supported(type.GetGenericArguments()[0]);
    }

    private bool Equal(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is byte[] oldBytes && right is byte[] newBytes)
        {
            if (oldBytes.Length > _maximumItems || newBytes.Length > _maximumItems)
                throw new InvalidOperationException("Notification comparison exceeds the configured collection limit.");
            return oldBytes.AsSpan().SequenceEqual(newBytes);
        }
        if (left is IList oldItems && right is IList newItems)
        {
            if (oldItems.Count > _maximumItems || newItems.Count > _maximumItems)
                throw new InvalidOperationException("Notification comparison exceeds the configured collection limit.");
            if (oldItems.Count != newItems.Count) return false;
            for (var index = 0; index < oldItems.Count; index++)
                if (!Equal(oldItems[index], newItems[index])) return false;
            return true;
        }
        return left.Equals(right);
    }
}

internal sealed class NotificationFilterValidator : ExpressionVisitor
{
    internal static Func<T, bool>? Compile<T>(Expression<Func<T, bool>>? filter)
    {
        if (filter is null) return null;
        new NotificationFilterValidator().Visit(filter);
        return filter.Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Member.Name == "Envelope")
            throw new ArgumentException("Repository notification filters cannot inspect envelopes.");
        return base.VisitMember(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Trusted predicates still use a deliberately small, side-effect-free query vocabulary.
        var type = node.Method.DeclaringType;
        if (type != typeof(string) && type != typeof(Math) && type != typeof(MathF)
            && !(type == typeof(Enumerable) && node.Method.Name == nameof(Enumerable.Contains)))
            throw new ArgumentException("Unsupported notification filter method. Use contract values and pure scalar/string comparisons.");
        return base.VisitMethodCall(node);
    }

    protected override Expression VisitInvocation(InvocationExpression node)
        => throw new ArgumentException("Invoked delegates are not supported in notification filters.");
    protected override Expression VisitBlock(BlockExpression node)
        => throw new ArgumentException("Statement blocks are not supported in notification filters.");

    protected override Expression VisitBinary(BinaryExpression node)
    {
        if (node.NodeType.ToString().Contains("Assign", StringComparison.Ordinal))
            throw new ArgumentException("Assignments are not supported in notification filters.");
        return base.VisitBinary(node);
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.NodeType.ToString().Contains("Assign", StringComparison.Ordinal))
            throw new ArgumentException("Mutations are not supported in notification filters.");
        return base.VisitUnary(node);
    }
}
