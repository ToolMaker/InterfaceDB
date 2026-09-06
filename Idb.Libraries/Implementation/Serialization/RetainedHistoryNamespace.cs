namespace Idb.Libraries.Implementation.Serialization;

/// <summary>History is never a live payload namespace, regardless of configured file extension.</summary>
internal static class RetainedHistoryNamespace
{
    internal const string DirectoryName = ".idb-history";
    internal static bool ContainsRelative(string relative)
        => relative.AsSpan().StartsWith(DirectoryName, StringComparison.OrdinalIgnoreCase)
            && (relative.Length == DirectoryName.Length || relative[DirectoryName.Length] is '/' or '\\');
}
