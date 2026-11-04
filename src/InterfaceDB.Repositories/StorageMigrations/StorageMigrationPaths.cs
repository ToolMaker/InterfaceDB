using Idb.Libraries.Implementation.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

internal static class StorageMigrationPaths
{
    internal static void ValidateTree(string root, CancellationToken cancellationToken)
    {
        Validate(root);
        var directories = new Stack<DirectoryInfo>();
        directories.Push(new DirectoryInfo(root));
        while (directories.TryPop(out var directory))
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                    throw new ArgumentException("Migration sources must not contain filesystem links.");
                if (entry is DirectoryInfo nested) directories.Push(nested);
            }
    }

    internal static void Validate(string path)
    {
        for (FileSystemInfo? entry = new FileInfo(Path.GetFullPath(path)); entry is not null;
            entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            if ((entry.Exists || entry.LinkTarget is not null) &&
                ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null))
                throw new ArgumentException("Migration paths must not traverse filesystem links.");
    }

    internal static void RequireDisjoint(string first, string second)
    {
        Validate(first); Validate(second);
        var comparison = FileSystemPathIdentity.ResolveComparer(first).Equals("a", "A")
            || FileSystemPathIdentity.ResolveComparer(second).Equals("a", "A")
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)) + Path.DirectorySeparatorChar;
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)) + Path.DirectorySeparatorChar;
        if (a.StartsWith(b, comparison) || b.StartsWith(a, comparison))
            throw new ArgumentException("Migration requires disjoint source, destination and receipt locations.");
    }
}
