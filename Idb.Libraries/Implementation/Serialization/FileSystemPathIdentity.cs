namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Resolves path identity for the configured storage volume rather than assuming that every
/// volume mounted by one operating system has the same case behavior.
/// </summary>
internal static class FileSystemPathIdentity
{
    private const int MaximumCachedRoots = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        Lazy<StringComparer>> Comparers = new(StringComparer.Ordinal);

    internal static StringComparer ResolveComparer(string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            return PlatformDefaultComparer();
        }

        string root;
        try
        {
            root = Path.GetFullPath(storageRoot);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            return PlatformDefaultComparer();
        }

        if (Comparers.TryGetValue(root, out var cached))
        {
            return cached.Value;
        }

        if (Comparers.Count >= MaximumCachedRoots)
        {
            return ProbeComparer(root);
        }

        return Comparers.GetOrAdd(
            root,
            static path => new Lazy<StringComparer>(
                () => ProbeComparer(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static StringComparer ProbeComparer(string root)
    {
        string? probePath = null;
        try
        {
            // Capability discovery must not create a storage directory before the
            // configured publication barrier. A missing directory inherits its parent.
            var probeRoot = root;
            while (!Directory.Exists(probeRoot))
            {
                probeRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(probeRoot));
                if (probeRoot is null) return PlatformDefaultComparer();
            }
            var probeName = ".idb-path-case-probe-a-" + Guid.NewGuid().ToString("N");
            probePath = Path.Combine(probeRoot, probeName);
            var alternatePath = Path.Combine(probeRoot, probeName.ToUpperInvariant());
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 1,
                       FileOptions.None))
            {
                return File.Exists(alternatePath)
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            return PlatformDefaultComparer();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(probePath))
            {
                try
                {
                    File.Delete(probePath);
                }
                catch
                {
                    // A failed capability probe must not prevent provider construction.
                }
            }
        }
    }

    internal static string Normalize(string value, StringComparer comparer) =>
        comparer.Equals("a", "A") ? value.ToUpperInvariant() : value;

    private static StringComparer PlatformDefaultComparer() =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || OperatingSystem.IsIOS()
        || OperatingSystem.IsMacCatalyst()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
