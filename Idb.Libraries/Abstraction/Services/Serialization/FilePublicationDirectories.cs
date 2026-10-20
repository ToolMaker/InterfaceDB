using System.Runtime.CompilerServices;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Default directory creation through the publication abstraction.</summary>
internal static class FilePublicationDirectories
{
    private static readonly ConditionalWeakTable<IFilePublication, State> States = new();

    internal static void EnsureDirectory(IFilePublication publication, string directoryPath)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var state = States.GetValue(publication, static _ => new());
        lock (state.Gate)
        {
            // Only inspect pending links on this path. A failed initialization in an
            // unrelated store must not block this store, even for a shared publisher.
            var ancestors = new Stack<string>();
            for (string? path = directory; path is not null; path = Path.GetDirectoryName(path))
                ancestors.Push(path);
            foreach (var path in ancestors)
            {
                if (!state.Pending.Contains(path)) continue;
                if (Directory.Exists(path)) Publish(path);
                else state.Pending.Remove(path); // Removed externally; creation below republishes it.
            }

            var missing = new Stack<string>();
            var current = directory;
            while (!Directory.Exists(current))
            {
                missing.Push(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new IOException("The storage volume must already exist.");
            }
            foreach (var path in missing)
            {
                Directory.CreateDirectory(path);
                state.Pending.Add(path);
                Publish(path);
            }

            void Publish(string path)
            {
                publication.FlushDirectory(path);
                if (Path.GetDirectoryName(path) is { } parent)
                    publication.FlushDirectory(parent);
                // Keep the failed link if either barrier throws, even though mkdir succeeded.
                state.Pending.Remove(path);
            }
        }
    }

    private sealed class State
    {
        internal readonly object Gate = new();
        internal readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    }
}
