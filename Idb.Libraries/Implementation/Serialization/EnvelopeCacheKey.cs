namespace Idb.Libraries.Implementation.Serialization;

internal static class EnvelopeCacheKey
{
    public static string ToPhysicalKey(string logicalKey, int index)
    {
        if (string.IsNullOrWhiteSpace(logicalKey))
        {
            return logicalKey;
        }

        return logicalKey + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ToLogicalKey(string physicalKey)
    {
        if (string.IsNullOrWhiteSpace(physicalKey))
        {
            return physicalKey;
        }

        var separator = physicalKey.LastIndexOf('-');
        if (separator <= 0 || separator == physicalKey.Length - 1)
        {
            return physicalKey;
        }

        var suffix = physicalKey.AsSpan(separator + 1);
        return int.TryParse(
            suffix,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
            && number >= 0
            ? physicalKey[..separator]
            : physicalKey;
    }
}
