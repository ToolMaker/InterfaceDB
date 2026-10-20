namespace Idb.Libraries.Implementation.Indexes;

/// <summary>
/// Only ordered, lossless integral promotions can be inverted for an index lookup.
/// Floating-point, narrowing, boxing and user-defined conversions keep their scan semantics.
/// </summary>
internal static class ExactNumericConversion
{
    public static bool IsOrderPreserving(Type source, Type target)
    {
        // Unwrapping Nullable<T> can throw for null, unlike a lifted comparison.
        if (Nullable.GetUnderlyingType(source) is not null && Nullable.GetUnderlyingType(target) is null)
            return false;
        source = Nullable.GetUnderlyingType(source) ?? source;
        target = Nullable.GetUnderlyingType(target) ?? target;
        if (source.IsEnum) source = Enum.GetUnderlyingType(source);
        return TryGetRange(source, out var sourceMin, out var sourceMax)
            && TryGetRange(target, out var targetMin, out var targetMax)
            && targetMin <= sourceMin && targetMax >= sourceMax;
    }

    public static bool TryConvert(object value, Type target, out object converted)
    {
        converted = value;
        if (target.IsInstanceOfType(value)) return true;
        if (!TryGetRange(value.GetType(), out _, out _)
            || !TryGetRange(target, out var minimum, out var maximum)) return false;

        var number = value is char character ? (decimal)character
            : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        if (number < minimum || number > maximum) return false;
        converted = target == typeof(char) ? (object)(char)(ushort)number
            : Convert.ChangeType(number, target, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetRange(Type type, out decimal minimum, out decimal maximum)
    {
        (minimum, maximum) = Type.GetTypeCode(type) switch
        {
            TypeCode.SByte => (sbyte.MinValue, sbyte.MaxValue),
            TypeCode.Byte => (byte.MinValue, byte.MaxValue),
            TypeCode.Int16 => (short.MinValue, short.MaxValue),
            TypeCode.UInt16 => (ushort.MinValue, ushort.MaxValue),
            TypeCode.Char => (char.MinValue, char.MaxValue),
            TypeCode.Int32 => (int.MinValue, int.MaxValue),
            TypeCode.UInt32 => (uint.MinValue, uint.MaxValue),
            TypeCode.Int64 => (long.MinValue, long.MaxValue),
            TypeCode.UInt64 => (ulong.MinValue, ulong.MaxValue),
            _ => (0m, -1m)
        };
        return maximum >= minimum && !type.IsEnum;
    }
}
