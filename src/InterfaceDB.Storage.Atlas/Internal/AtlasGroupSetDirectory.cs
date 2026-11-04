using System.Buffers.Binary;
using System.Security.Cryptography;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.AtlasCore.Internal;

internal sealed class AtlasGroupSetDirectory
{
    private const int Version = 1;
    private const int DataPayloadBytes = 24;
    private const int CommitPayloadBytes = 60;
    private static ReadOnlySpan<byte> DataMagic => "IDBAGS01"u8;
    private static ReadOnlySpan<byte> CommitMagic => "IDBAGC01"u8;

    private readonly string _directory;
    private readonly string _setsDirectory;
    private readonly IDurableFileSystem _durableFileSystem;
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private GroupSetSnapshot _current;

    private AtlasGroupSetDirectory(
        string directory,
        string setsDirectory,
        IDurableFileSystem durableFileSystem,
        GroupSetSnapshot current)
    {
        _directory = directory;
        _setsDirectory = setsDirectory;
        _durableFileSystem = durableFileSystem;
        _current = current;
    }

    public long Generation => Volatile.Read(ref _current).Generation;

    public string CurrentRoot => Volatile.Read(ref _current).RootDirectory;

    public static async ValueTask<AtlasGroupSetDirectory> OpenAsync(
        string rootDirectory,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(rootDirectory, "group-sets");
        var setsDirectory = Path.Combine(directory, "sets");
        var existed = Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(setsDirectory);
        if (!existed)
        {
            durableFileSystem.FlushDirectory(rootDirectory);
        }

        var generation = await TryLoadLatestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (generation == 0)
        {
            generation = 1;
            var initialRoot = SetRoot(setsDirectory, generation);
            Directory.CreateDirectory(initialRoot);
            durableFileSystem.FlushDirectory(setsDirectory);
            await PublishGenerationAsync(
                    directory,
                    generation,
                    durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var currentRoot = SetRoot(setsDirectory, generation);
        if (!Directory.Exists(currentRoot))
        {
            throw new InvalidDataException(
                $"AtlasCore group-set generation {generation:N0} has no physical directory.");
        }

        return new AtlasGroupSetDirectory(
            directory,
            setsDirectory,
            durableFileSystem,
            new GroupSetSnapshot(generation, currentRoot));
    }

    public async ValueTask<(long Generation, string RootDirectory)> CreateCandidateAsync(
        CancellationToken cancellationToken)
    {
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = checked(Volatile.Read(ref _current).Generation + 1);
            var root = SetRoot(_setsDirectory, generation);
            if (Directory.Exists(root))
            {
                throw new IOException(
                    $"AtlasCore group-set candidate {generation:N0} already exists.");
            }

            Directory.CreateDirectory(root);
            _durableFileSystem.FlushDirectory(_setsDirectory);
            return (generation, root);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    public async ValueTask PublishAsync(
        long generation,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _current);
            var expectedRoot = SetRoot(_setsDirectory, generation);
            if (generation != current.Generation + 1
                || !string.Equals(
                    Path.GetFullPath(rootDirectory),
                    Path.GetFullPath(expectedRoot),
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(expectedRoot))
            {
                throw new InvalidOperationException("AtlasCore group-set publication is not the next prepared generation.");
            }

            await PublishGenerationAsync(
                    _directory,
                    generation,
                    _durableFileSystem,
                    cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _current, new GroupSetSnapshot(generation, expectedRoot));
            CleanupMetadata(generation);
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    public void CleanupSupersededSets()
    {
        var current = Volatile.Read(ref _current);
        foreach (var directory in Directory.EnumerateDirectories(
                     _setsDirectory,
                     "set-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(current.RootDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _durableFileSystem.FlushDirectory(_setsDirectory);
    }

    public void DiscardCandidate(long generation, string rootDirectory)
    {
        var expected = SetRoot(_setsDirectory, generation);
        if (generation <= Volatile.Read(ref _current).Generation
            || !string.Equals(
                Path.GetFullPath(rootDirectory),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (Directory.Exists(expected))
            {
                Directory.Delete(expected, recursive: true);
                _durableFileSystem.FlushDirectory(_setsDirectory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async ValueTask<long> TryLoadLatestAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var commits = Directory
            .EnumerateFiles(directory, "set-*.commit", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: TryParseGeneration(path, ".commit", out var generation), Generation: generation))
            .Where(static item => item.Parsed)
            .OrderByDescending(static item => item.Generation)
            .ToArray();
        if (commits.Length == 0)
        {
            return 0;
        }

        var latest = commits[0];
        if (new FileInfo(latest.Path).Length != CommitPayloadBytes + DurableMetadata.HashBytes)
        {
            throw new InvalidDataException("AtlasCore group-set commit has an invalid length.");
        }
        var commitBytes = await File.ReadAllBytesAsync(latest.Path, cancellationToken).ConfigureAwait(false);
        var commit = DurableMetadata.ValidateAndGetPayload(
            commitBytes,
            $"AtlasCore group-set commit generation {latest.Generation:N0}");
        if (commit.Length != CommitPayloadBytes
            || !commit.Span[..8].SequenceEqual(CommitMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(commit.Span[8..]) != Version
            || BinaryPrimitives.ReadInt64LittleEndian(commit.Span[12..]) != latest.Generation)
        {
            throw new InvalidDataException("AtlasCore group-set commit is unsupported or inconsistent.");
        }

        var expectedLength = BinaryPrimitives.ReadInt64LittleEndian(commit.Span[20..]);
        var expectedHash = commit.Span.Slice(28, DurableMetadata.HashBytes).ToArray();
        var actualHash = new byte[DurableMetadata.HashBytes];
        foreach (var suffix in new[] { ".a", ".b" })
        {
            var path = DataPath(directory, latest.Generation, suffix);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length != DataPayloadBytes + DurableMetadata.HashBytes)
                {
                    continue;
                }
                var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                SHA256.HashData(data, actualHash);
                if (data.LongLength == expectedLength
                    && CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    return DecodeData(data, latest.Generation);
                }
            }
            catch (IOException)
            {
            }
        }

        throw new InvalidDataException(
            $"Both committed AtlasCore group-set copies for generation {latest.Generation:N0} are unavailable or corrupt.");
    }

    private static async ValueTask PublishGenerationAsync(
        string directory,
        long generation,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var payload = new byte[DataPayloadBytes];
        DataMagic.CopyTo(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), Version);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), generation);
        var data = DurableMetadata.AppendHash(payload);
        await DurableMetadata.PublishNewAsync(
                DataPath(directory, generation, ".a"),
                data,
                durableFileSystem,
                cancellationToken)
            .ConfigureAwait(false);
        await DurableMetadata.PublishNewAsync(
                DataPath(directory, generation, ".b"),
                data,
                durableFileSystem,
                cancellationToken)
            .ConfigureAwait(false);

        var commitPayload = new byte[CommitPayloadBytes];
        CommitMagic.CopyTo(commitPayload);
        BinaryPrimitives.WriteInt32LittleEndian(commitPayload.AsSpan(8), Version);
        BinaryPrimitives.WriteInt64LittleEndian(commitPayload.AsSpan(12), generation);
        BinaryPrimitives.WriteInt64LittleEndian(commitPayload.AsSpan(20), data.LongLength);
        SHA256.HashData(data, commitPayload.AsSpan(28, DurableMetadata.HashBytes));
        await DurableMetadata.PublishNewAsync(
                CommitPath(directory, generation),
                DurableMetadata.AppendHash(commitPayload),
                durableFileSystem,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static long DecodeData(byte[] data, long expectedGeneration)
    {
        var payload = DurableMetadata.ValidateAndGetPayload(
            data,
            $"AtlasCore group-set generation {expectedGeneration:N0}");
        if (payload.Length != DataPayloadBytes
            || !payload.Span[..8].SequenceEqual(DataMagic)
            || BinaryPrimitives.ReadInt32LittleEndian(payload.Span[8..]) != Version
            || BinaryPrimitives.ReadInt64LittleEndian(payload.Span[12..]) != expectedGeneration)
        {
            throw new InvalidDataException("AtlasCore group-set data is unsupported or inconsistent.");
        }

        return expectedGeneration;
    }

    private void CleanupMetadata(long latestGeneration)
    {
        var minimum = latestGeneration - 1;
        foreach (var path in Directory.EnumerateFiles(_directory, "set-*", SearchOption.TopDirectoryOnly))
        {
            if (TryParseGeneration(path, Path.GetExtension(path), out var generation) && generation < minimum)
            {
                DurableMetadata.TryDelete(path);
            }
        }

        _durableFileSystem.FlushDirectory(_directory);
    }

    private static bool TryParseGeneration(string path, string suffix, out long generation)
    {
        generation = 0;
        var name = Path.GetFileName(path);
        if (!name.StartsWith("set-", StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var numeric = name.AsSpan("set-".Length, name.Length - "set-".Length - suffix.Length);
        return long.TryParse(numeric, out generation) && generation > 0;
    }

    private static string SetRoot(string setsDirectory, long generation) =>
        Path.Combine(setsDirectory, $"set-{generation:D20}");

    private static string DataPath(string directory, long generation, string suffix) =>
        Path.Combine(directory, $"set-{generation:D20}{suffix}");

    private static string CommitPath(string directory, long generation) =>
        Path.Combine(directory, $"set-{generation:D20}.commit");

    private sealed record GroupSetSnapshot(long Generation, string RootDirectory);
}
