using System.Security.Cryptography;
using System.Text.Json;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

internal sealed record SegmentCoreBackupManifest
{
    public int ManifestVersion { get; init; } = 1;

    public required SegmentCoreStorageFormatInfo StorageFormat { get; init; }

    public required long CheckpointGeneration { get; init; }

    public required long TransactionWatermark { get; init; }

    public required int Entries { get; init; }

    public required int StripeCount { get; init; }

    public required SegmentCoreTransactionProtocol TransactionProtocol { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required IReadOnlyList<ObjectLogWritePosition> Positions { get; init; }

    public List<SegmentCoreBackupFile> Files { get; init; } = [];
}

internal sealed record SegmentCoreBackupFile(
    string RelativePath,
    long Length,
    string Sha256);

internal static class SegmentCoreBackupFiles
{
    internal const string ManifestFileName = "segment-core-backup.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    public static string CreateStagingDirectory(string finalDirectory, string operation)
    {
        var final = Path.GetFullPath(finalDirectory);
        if (Directory.Exists(final) || File.Exists(final))
        {
            throw new IOException($"Destination '{final}' already exists; SegmentCore never overwrites a backup or restore target.");
        }

        var parent = Path.GetDirectoryName(final)
            ?? throw new IOException($"Destination '{final}' has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(
            parent,
            $".{Path.GetFileName(final)}.{operation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    public static async ValueTask<SegmentCoreBackupFile> CopyPrefixAsync(
        string sourcePath,
        string destinationRoot,
        string relativePath,
        long length,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source) || new FileInfo(source).Length < length)
        {
            throw new IOException($"Backup source '{source}' is missing or shorter than {length:N0} bytes.");
        }

        var destination = ResolveRelativePath(destinationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Backup source '{source}' ended unexpectedly.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        durableFileSystem.FlushFile(output.SafeFileHandle);
        return new SegmentCoreBackupFile(
            NormalizeRelativePath(relativePath),
            length,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    public static SegmentCoreBackupFile DescribeFile(string root, string relativePath)
    {
        var path = ResolveRelativePath(root, relativePath);
        using var stream = File.OpenRead(path);
        return new SegmentCoreBackupFile(
            NormalizeRelativePath(relativePath),
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    public static async ValueTask WriteManifestAsync(
        string root,
        SegmentCoreBackupManifest manifest,
        IDurableFileSystem durableFileSystem,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetFullPath(root), ManifestFileName);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        durableFileSystem.FlushFile(stream.SafeFileHandle);
    }

    public static SegmentCoreBackupManifest ReadAndValidateManifest(string backupRoot)
    {
        var root = Path.GetFullPath(backupRoot);
        var path = Path.Combine(root, ManifestFileName);
        var manifest = JsonSerializer.Deserialize<SegmentCoreBackupManifest>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException($"Backup manifest '{path}' is empty.");
        SegmentCoreStorageFormatManifest.EnsureSupported(manifest.StorageFormat, path);
        if (manifest.ManifestVersion != 1
            || manifest.CheckpointGeneration <= 0
            || manifest.TransactionWatermark < 0
            || manifest.Entries < 0
            || manifest.StripeCount is < 1 or > 64
            || !Enum.IsDefined(manifest.TransactionProtocol)
            || manifest.Positions.Count != manifest.StripeCount)
        {
            throw new SegmentCoreCompatibilityException($"Backup manifest '{path}' is unsupported or invalid.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file.Length < 0
                || file.Sha256.Length != SHA256.HashSizeInBytes * 2
                || !paths.Add(NormalizeRelativePath(file.RelativePath)))
            {
                throw new InvalidDataException($"Backup manifest '{path}' contains an invalid file entry.");
            }

            var source = ResolveRelativePath(root, file.RelativePath);
            if (!File.Exists(source) || new FileInfo(source).Length != file.Length)
            {
                throw new InvalidDataException($"Backup file '{source}' is missing or has the wrong length.");
            }

            using var stream = File.OpenRead(source);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup file '{source}' failed SHA-256 validation.");
            }
        }

        return manifest;
    }

    public static void PublishDirectory(
        string stagingDirectory,
        string finalDirectory,
        IDurableFileSystem durableFileSystem)
    {
        Directory.Move(stagingDirectory, finalDirectory);
        durableFileSystem.FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(finalDirectory))!);
    }

    public static void TryDeleteStaging(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch
        {
            // A failed staging directory is never treated as a published backup or restored store.
        }
    }

    public static string ResolveRelativePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Backup paths must be non-empty relative paths.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup path '{relativePath}' escapes its root.");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
