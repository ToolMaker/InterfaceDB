using System.Linq.Expressions;
using System.Reflection;

namespace Idb.Commercial.Repositories.Peers;

public interface ISequenceAccessor<T>
{
    Expression<Func<T, long?>> Selector { get; }
    long? Get(T entity);
    void Set(T entity, long? sequence);
}

/// <summary>Validates the contract once. Includes inherited interfaces; property names are unrestricted.</summary>
public sealed class AttributedSequenceAccessor<T> : ISequenceAccessor<T>
{
    private readonly PropertyInfo _property;
    public AttributedSequenceAccessor()
    {
        if (!typeof(T).IsInterface) throw new ArgumentException("The storage contract must be an interface.");
        var properties = new[] { typeof(T) }.Concat(typeof(T).GetInterfaces()).SelectMany(t => t.GetProperties())
            .Where(p => p.IsDefined(typeof(SequenceNumberAttribute))).Distinct().ToArray();
        if (properties.Length != 1 || properties[0].PropertyType != typeof(long?)
            || properties[0].GetMethod is null || properties[0].SetMethod is null || properties[0].GetIndexParameters().Length != 0)
            throw new ArgumentException("Declare exactly one readable/writable nullable long [SequenceNumber] property.");
        _property = properties[0];
        var value = Expression.Parameter(typeof(T), "record");
        Selector = Expression.Lambda<Func<T, long?>>(Expression.Property(value, _property), value);
    }
    public Expression<Func<T, long?>> Selector { get; }
    public long? Get(T entity) => (long?)_property.GetValue(entity);
    public void Set(T entity, long? sequence)
    {
        if (sequence is <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        _property.SetValue(entity, sequence);
    }
}
