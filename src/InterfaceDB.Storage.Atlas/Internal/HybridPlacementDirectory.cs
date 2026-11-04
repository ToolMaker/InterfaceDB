using System.Buffers;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore.Internal;

internal readonly record struct PlacementRoute(int PrimaryGroup, int[] ReadGroups, int LeafDepth, long Generation);

/// <summary>One immutable, durable prefix ownership map. Hashing only accelerates metadata lookup;
/// it never distributes keys across unrelated physical groups. Full prefix bytes verify hash collisions.</summary>
internal sealed class HybridPlacementDirectory : IPlacementDirectory
{
    internal sealed record Entry(int Id, int ParentId, string PrefixHex, int SplitDepth, bool RequiredFiles = false);
    private sealed record Manifest(int Version, long Generation, Entry[] Entries);
    internal sealed class Snapshot(long generation, Entry[] entries)
    {
        public long Generation { get; } = generation;
        public Entry[] Entries { get; } = entries;
        public byte[][] Prefixes { get; } = entries.Select(e => Convert.FromHexString(e.PrefixHex)).ToArray();
        public int[][] ReadGroups { get; } = entries.Select(e => new[] { e.Id }).ToArray();
        public Dictionary<(int Parent, int Length, ulong Hash), int[]> Lookup { get; } = entries
            .GroupBy(e => (e.ParentId, e.PrefixHex.Length / 2, XxHash3.HashToUInt64(Convert.FromHexString(e.PrefixHex))))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToArray());
    }

    private readonly string _directory;
    private readonly AtlasPartitionOptions _options;
    private readonly IDurableFileSystem _fileSystem;
    private readonly SemaphoreSlim _registration = new(1, 1);
    private Snapshot _snapshot;
    private int _faulted;
    private long _failures;

    private HybridPlacementDirectory(string directory, AtlasPartitionOptions options, IDurableFileSystem fileSystem, Snapshot snapshot)
        => (_directory, _options, _fileSystem, _snapshot) = (directory, options, fileSystem, snapshot);

    public long Generation => Capture().Generation;
    public int KnownGroupCount => Capture().Entries.Length;
    public int SplitCount => Capture().Entries.Count(e => e.SplitDepth != 0);
    public long PublicationFailures => Volatile.Read(ref _failures);
    public Snapshot Capture() => Volatile.Read(ref _snapshot);
    public string GroupDirectoryName(int group) => $"bucket-{group:D8}";

    public static async ValueTask<HybridPlacementDirectory> OpenAsync(string root, string groupRoot,
        AtlasCoreOptions options, IDurableFileSystem fileSystem, CancellationToken token)
    {
        var directory = Path.Combine(root, "hybrid-placement");
        if (!Directory.Exists(directory)) { Directory.CreateDirectory(directory); fileSystem.FlushDirectory(root); }
        var files = Directory.GetFiles(directory, "map-*.idbmap").Order(StringComparer.Ordinal).ToArray();
        Snapshot snapshot;
        if (files.Length == 0)
        {
            if (Directory.EnumerateDirectories(groupRoot, "bucket-*").Any())
                throw new InvalidDataException("Hybrid ownership metadata is missing; refusing to guess physical placement.");
            snapshot = new(1, options.Partitioning.FixedPrefixHex()
                .Select((prefix, id) => new Entry(id, -1, prefix, 0)).ToArray());
        }
        else
        {
            var last = files[^1];
            if (new FileInfo(last).Length > 64L * 1024 * 1024) throw new InvalidDataException("Oversized hybrid ownership map.");
            var payload = DurableMetadata.ValidateAndGetPayload(await File.ReadAllBytesAsync(last, token).ConfigureAwait(false), "hybrid ownership map");
            var manifest = JsonSerializer.Deserialize<Manifest>(payload.Span) ?? throw new InvalidDataException("Missing hybrid map.");
            Validate(manifest, options.Partitioning);
            if (Path.GetFileName(last) != $"map-{manifest.Generation:D20}.idbmap") throw new InvalidDataException("Hybrid map identity mismatch.");
            snapshot = new(manifest.Generation, manifest.Entries);
        }
        var result = new HybridPlacementDirectory(directory, options.Partitioning, fileSystem, snapshot);
        if (files.Length == 0) await result.WriteAsync(snapshot, token).ConfigureAwait(false);
        foreach (var entry in snapshot.Entries.Where(e => e.RequiredFiles))
        {
            var segments = Path.Combine(groupRoot, result.GroupDirectoryName(entry.Id), "segments");
            if (!Directory.Exists(segments) || !Directory.EnumerateFiles(segments, "segment-*.idbag").Any())
                throw new InvalidDataException("A committed Atlas owner lost its segment files.");
        }
        // Only this format's unpublished tail groups can be discarded. Never fall back to an
        // older valid map when the newest map is corrupt: that could discard acknowledged writes.
        foreach (var path in Directory.EnumerateDirectories(groupRoot, "bucket-*"))
        {
            var name = Path.GetFileName(path);
            if (!int.TryParse(name.AsSpan(7), out var id) || id < 0 || name != result.GroupDirectoryName(id))
                throw new InvalidDataException("Invalid hybrid group identity.");
            if (id >= snapshot.Entries.Length) DeleteUnpublishedGroup(groupRoot, path, fileSystem);
        }
        return result;
    }

    private static void Validate(Manifest manifest, AtlasPartitionOptions options)
    {
        // Fixed layouts use v2 so earlier dynamic-only readers fail closed instead of growing them.
        if (manifest.Version != (options.IsFixedLayout ? 2 : 1) || manifest.Generation < 1 || manifest.Entries is null
            || manifest.Entries.Length > options.MaximumPartitions)
            throw new InvalidDataException("Unsupported or oversized hybrid map.");
        if (options.IsFixedLayout && (!manifest.Entries.Select(e => e?.PrefixHex).SequenceEqual(options.FixedPrefixHex())
            || manifest.Entries.Any(e => e is null || e.ParentId != -1 || e.SplitDepth != 0)))
            throw new InvalidDataException("The configured fixed prefixes differ from the durable ownership map.");
        var identities = new HashSet<(int, string)>();
        for (var i = 0; i < manifest.Entries.Length; i++)
        {
            var e = manifest.Entries[i];
            if (e is null || e.Id != i || e.ParentId < -1 || e.ParentId >= i
                || string.IsNullOrEmpty(e.PrefixHex) || e.PrefixHex.Length % 2 != 0
                || e.PrefixHex.Length > options.MaximumPrefixBytes * 2
                || e.PrefixHex.Any(c => !char.IsAsciiHexDigitUpper(c))
                || !identities.Add((e.ParentId, e.PrefixHex)))
                throw new InvalidDataException("Invalid hybrid partition identity.");
            var depth = e.PrefixHex.Length / 2;
            if (e.SplitDepth != 0 && (e.SplitDepth <= depth || e.SplitDepth > options.MaximumPrefixBytes))
                throw new InvalidDataException("Invalid hybrid split depth.");
            if (e.ParentId == -1 && depth > options.InitialPrefixBytes)
                throw new InvalidDataException("Invalid initial prefix.");
            if (e.ParentId >= 0)
            {
                var parent = manifest.Entries[e.ParentId];
                if (parent.SplitDepth == 0 || depth > parent.SplitDepth || depth <= parent.PrefixHex.Length / 2
                    || !e.PrefixHex.StartsWith(parent.PrefixHex, StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid parent/child ownership.");
            }
        }
    }

    public PlacementRoute Route(string key) => Route(key, Capture());
    internal PlacementRoute Route(string key, Snapshot snapshot)
    {
        ThrowIfFaulted();
        Span<byte> bytes = stackalloc byte[512];
        var encoded = EncodePrefix(key, bytes);
        var location = Locate(encoded, snapshot);
        return location.Id >= 0
            ? new(location.Id, snapshot.ReadGroups[location.Id], snapshot.Prefixes[location.Id].Length, snapshot.Generation)
            : new(-1, [], location.Depth, snapshot.Generation);
    }

    public PlacementRoute RouteForWrite(string key)
    {
        var route = Route(key);
        if (route.PrimaryGroup < 0) throw new InvalidOperationException("The key prefix has not been registered.");
        return route;
    }

    public ValueTask<PlacementRoute> GetWriteRouteAsync(string key, CancellationToken token)
    {
        var route = Route(key);
        if (route.PrimaryGroup < 0 && _options.IsFixedLayout)
            throw new AtlasRouteRejectedException("The key has no owner in this fixed Atlas layout; no record was written.");
        return route.PrimaryGroup >= 0 ? ValueTask.FromResult(route) : RegisterAsync(key, token);
    }

    // Called only during Open, after every fixed owner has been activated successfully. Until
    // this durable marker exists a failed first initialization can safely recreate empty groups;
    // no application writes have been admitted. Afterwards missing owner files fail closed.
    internal async ValueTask CompleteFixedInitializationAsync(CancellationToken token)
    {
        var current = Capture();
        if (!_options.IsFixedLayout || current.Entries.All(e => e.RequiredFiles)) return;
        var next = new Snapshot(checked(current.Generation + 1), current.Entries.Select(e => e with { RequiredFiles = true }).ToArray());
        await WriteAsync(next, token).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, next);
    }

    private async ValueTask<PlacementRoute> RegisterAsync(string key, CancellationToken token)
    {
        await _registration.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            var snapshot = Capture();
            var encoded = PrefixBytes(key);
            var location = Locate(encoded, snapshot);
            if (location.Id >= 0) return Route(key, snapshot);
            if (snapshot.Entries.Length >= _options.MaximumPartitions)
                throw new AtlasRouteRejectedException("Hybrid partition limit reached; no record was written.");
            var entry = new Entry(snapshot.Entries.Length, location.Parent, Convert.ToHexString(encoded.AsSpan(0, location.Depth)), 0);
            var next = new Snapshot(checked(snapshot.Generation + 1), [..snapshot.Entries, entry]);
            await WriteAsync(next, token).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, next);
            return Route(key, next);
        }
        finally { _registration.Release(); }
    }

    private (int Id, int Parent, int Depth) Locate(ReadOnlySpan<byte> bytes, Snapshot snapshot)
    {
        var parent = -1;
        var depth = Math.Min(bytes.Length, _options.InitialPrefixBytes);
        while (true)
        {
            var prefix = bytes[..depth];
            var found = -1;
            if (snapshot.Lookup.TryGetValue((parent, depth, XxHash3.HashToUInt64(prefix)), out var candidates))
                foreach (var id in candidates)
                    if (prefix.SequenceEqual(snapshot.Prefixes[id])) { found = id; break; }
            if (found < 0) return (-1, parent, depth);
            var entry = snapshot.Entries[found];
            // A key ending exactly at a routing parent remains owned by that parent.
            if (entry.SplitDepth == 0 || bytes.Length == depth) return (found, parent, depth);
            parent = found;
            depth = Math.Min(bytes.Length, entry.SplitDepth);
        }
    }

    internal byte[] PrefixBytes(string key)
    {
        Span<byte> bytes = stackalloc byte[512];
        return EncodePrefix(key, bytes).ToArray();
    }

    private ReadOnlySpan<byte> EncodePrefix(string key, Span<byte> destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var chars = key.AsSpan(0, Math.Min(key.Length, _options.MaximumPrefixBytes + 1));
        var written = Encoding.UTF8.GetBytes(chars, destination);
        return destination[..Math.Min(written, _options.MaximumPrefixBytes)];
    }

    internal Snapshot PrepareSplit(int parentId, int depth, string[] childPrefixes)
    {
        if (_options.IsFixedLayout) throw new InvalidOperationException("A fixed Atlas layout cannot split.");
        var current = Capture();
        if (current.Entries[parentId].SplitDepth != 0) throw new InvalidOperationException("Partition was already split.");
        if (childPrefixes.Length > _options.MaximumPartitions - current.Entries.Length)
            throw new InvalidOperationException("Hybrid partition limit reached.");
        var entries = new Entry[current.Entries.Length + childPrefixes.Length];
        current.Entries.CopyTo(entries, 0);
        entries[parentId] = entries[parentId] with { SplitDepth = depth };
        for (var i = 0; i < childPrefixes.Length; i++)
            entries[current.Entries.Length + i] = new(current.Entries.Length + i, parentId, childPrefixes[i], 0, true);
        var manifest = new Manifest(1, checked(current.Generation + 1), entries);
        Validate(manifest, _options);
        return new(manifest.Generation, entries);
    }

    // Caller owns store mutation exclusion. All copied child data/checkpoints must be durable.
    internal async ValueTask PublishSplitAsync(Snapshot next)
    {
        if (next.Generation != Capture().Generation + 1) throw new InvalidOperationException("Stale split plan.");
        await WriteAsync(next, CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, next);
    }

    private async ValueTask WriteAsync(Snapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await DurableMetadata.PublishNewAsync(Path.Combine(_directory, $"map-{snapshot.Generation:D20}.idbmap"),
                DurableMetadata.AppendHash(JsonSerializer.SerializeToUtf8Bytes(new Manifest(_options.IsFixedLayout ? 2 : 1, snapshot.Generation, snapshot.Entries))),
                _fileSystem, CancellationToken.None).ConfigureAwait(false);
        }
        catch { Volatile.Write(ref _faulted, 1); Interlocked.Increment(ref _failures); throw; }
        // Obsolete metadata cleanup is not part of the commit. A cleanup failure must not
        // leave callers using the old in-memory map after the new map was durably published.
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directory, "map-*.idbmap"))
                if (string.CompareOrdinal(Path.GetFileName(path), $"map-{snapshot.Generation - 1:D20}.idbmap") < 0)
                    DurableMetadata.TryDelete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void DeleteUnpublishedGroup(string root, string path, IDurableFileSystem fileSystem)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), fullRoot, StringComparison.Ordinal)
            || !Path.GetFileName(fullPath).StartsWith("bucket-", StringComparison.Ordinal)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Unsafe unpublished group path.");
        if (Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories)
            .Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Unpublished group contains a link.");
        Directory.Delete(fullPath, recursive: true);
        fileSystem.FlushDirectory(fullRoot);
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _faulted) != 0) throw new InvalidOperationException("Hybrid publication was uncertain; reopen the store.");
    }
    public void Observe(string key, PlacementRoute route) { }
    public ValueTask FlushMaintenanceAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
