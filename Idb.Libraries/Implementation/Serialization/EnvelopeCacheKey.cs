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

        return TrySplitPhysicalKey(physicalKey, out var logicalKey, out _) ? logicalKey : physicalKey;
    }

    public static bool TrySplitPhysicalKey(string physicalKey, out string logicalKey, out int suffix)
    {
        var separator = physicalKey.LastIndexOf('-');
        if (separator > 0 && separator < physicalKey.Length - 1
            && int.TryParse(physicalKey.AsSpan(separator + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out suffix) && suffix >= 0)
        {
            logicalKey = physicalKey[..separator];
            return true;
        }
        logicalKey = physicalKey;
        suffix = -1;
        return false;
    }
}
