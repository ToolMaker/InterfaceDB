using System.Diagnostics;
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

internal sealed class SegmentCoreRestoreAdmission
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public SegmentCoreRestoreAdmission(SegmentCoreRestoreOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public SegmentCoreRestoreOptions Options { get; }

    public void ThrowIfExceeded(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_elapsed.Elapsed > Options.MaximumDuration)
        {
            throw new TimeoutException(
                $"SegmentCore restore exceeded its {Options.MaximumDuration} admission duration.");
        }
    }
}

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
        CreateRegularDirectoryTree(parent);
        var staging = Path.Combine(
            parent,
            $".{Path.GetFileName(final)}.{operation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        EnsureRegularDirectory(parent, staging);
        return staging;
    }

    public static async ValueTask<SegmentCoreBackupFile> CopyPrefixAsync(
        string sourceRoot,
        string sourcePath,
        string destinationRoot,
        string relativePath,
        long length,
        IDurableFileSystem durableFileSystem,
        SegmentCoreRestoreAdmission? admission,
        CancellationToken cancellationToken)
    {
        admission?.ThrowIfExceeded(cancellationToken);
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (admission is not null && length > admission.Options.MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"Backup file '{relativePath}' exceeds the configured per-file admission limit.");
        }

        var source = Path.GetFullPath(sourcePath);
        if (GetRegularFileLength(sourceRoot, source) < length)
        {
            throw new IOException($"Backup source '{source}' is missing or shorter than {length:N0} bytes.");
        }

        var destination = ResolveRelativePath(destinationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureRegularDirectory(destinationRoot, Path.GetDirectoryName(destination)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < length)
        {
            throw new IOException($"Backup source '{source}' became shorter than {length:N0} bytes.");
        }

        EnsureRegularFile(sourceRoot, source);
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
            admission?.ThrowIfExceeded(cancellationToken);
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
        var length = GetRegularFileLength(root, path);
        return new SegmentCoreBackupFile(
            NormalizeRelativePath(relativePath),
            length,
            ComputeSha256(root, path, length, admission: null, CancellationToken.None));
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
        => ReadAndValidateManifest(
            backupRoot,
            new SegmentCoreRestoreAdmission(new SegmentCoreRestoreOptions()),
            CancellationToken.None);

    public static SegmentCoreBackupManifest ReadAndValidateManifest(
        string backupRoot,
        SegmentCoreRestoreAdmission admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        admission.ThrowIfExceeded(cancellationToken);
        var root = Path.GetFullPath(backupRoot);
        EnsureRegularDirectoryTree(root);
        var path = Path.Combine(root, ManifestFileName);
        var manifestLength = GetRegularFileLength(root, path);
        if (manifestLength > admission.Options.MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"Backup manifest '{path}' exceeds the configured manifest admission limit.");
        }

        using var manifestStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (manifestStream.Length != manifestLength)
        {
            throw new InvalidDataException($"Backup manifest '{path}' changed while it was opened.");
        }

        var manifest = JsonSerializer.Deserialize<SegmentCoreBackupManifest>(manifestStream, JsonOptions)
            ?? throw new InvalidDataException($"Backup manifest '{path}' is empty.");
        admission.ThrowIfExceeded(cancellationToken);
        if (manifest.StorageFormat is null
            || manifest.Positions is null
            || manifest.Files is null
            || manifest.ManifestVersion != 1
            || manifest.CheckpointGeneration <= 0
            || manifest.TransactionWatermark < 0
            || manifest.Entries < 0
            || manifest.StripeCount is < 1 or > 64
            || !Enum.IsDefined(manifest.TransactionProtocol)
            || manifest.Positions.Count != manifest.StripeCount)
        {
            throw new SegmentCoreCompatibilityException($"Backup manifest '{path}' is unsupported or invalid.");
        }

        SegmentCoreStorageFormatManifest.EnsureSupported(manifest.StorageFormat, path);
        if (manifest.Files.Count > admission.Options.MaximumFiles)
        {
            throw new InvalidDataException(
                $"Backup manifest '{path}' exceeds the configured file-count admission limit.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            admission.ThrowIfExceeded(cancellationToken);
            if (file is null)
            {
                throw new InvalidDataException($"Backup manifest '{path}' contains an invalid file entry.");
            }

            var normalizedPath = NormalizeAndValidateRelativePath(
                file.RelativePath,
                admission.Options.MaximumPathDepth);
            if (!string.Equals(file.RelativePath, normalizedPath, StringComparison.Ordinal)
                || !IsKnownBackupPath(manifest, normalizedPath)
                || file.Length < 0
                || file.Length > admission.Options.MaximumFileBytes
                || file.Sha256 is null
                || file.Sha256.Length != SHA256.HashSizeInBytes * 2
                || !file.Sha256.All(Uri.IsHexDigit)
                || !paths.Add(normalizedPath)
                || totalBytes > admission.Options.MaximumTotalBytes - file.Length)
            {
                throw new InvalidDataException($"Backup manifest '{path}' contains an invalid file entry.");
            }

            totalBytes += file.Length;
        }

        foreach (var file in manifest.Files)
        {
            admission.ThrowIfExceeded(cancellationToken);
            var source = ResolveRelativePath(root, file.RelativePath);
            if (GetRegularFileLength(root, source) != file.Length)
            {
                throw new InvalidDataException($"Backup file '{source}' is missing or has the wrong length.");
            }

            var actual = ComputeSha256(root, source, file.Length, admission, cancellationToken);
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
        var staging = Path.GetFullPath(stagingDirectory);
        var final = Path.GetFullPath(finalDirectory);
        var parent = Path.GetDirectoryName(final)
            ?? throw new IOException($"Destination '{final}' has no parent directory.");
        EnsureRegularDirectoryTree(parent);
        EnsureRegularDirectory(parent, staging);
        Directory.Move(staging, final);
        EnsureRegularDirectory(parent, final);
        durableFileSystem.FlushDirectory(parent);
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

    public static long GetRegularFileLength(string root, string path)
    {
        EnsureRegularFile(root, path);
        var length = new FileInfo(path).Length;
        EnsureRegularFile(root, path);
        return length;
    }

    public static void EnsureRegularDirectory(string root, string directory)
        => EnsureRegularPath(root, directory, expectDirectory: true);

    public static void EnsureRegularDirectoryTree(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var pathRoot = Path.GetPathRoot(fullDirectory)
            ?? throw new InvalidDataException($"Directory '{directory}' has no filesystem root.");
        EnsureEntryIsRegular(pathRoot, expectDirectory: true);
        var relative = Path.GetRelativePath(pathRoot, fullDirectory);
        if (relative == ".")
        {
            return;
        }

        var current = pathRoot;
        foreach (var part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            EnsureEntryIsRegular(current, expectDirectory: true);
        }
    }

    public static void EnsureRegularFile(string root, string file)
        => EnsureRegularPath(root, file, expectDirectory: false);

    private static string ComputeSha256(
        string root,
        string path,
        long expectedLength,
        SegmentCoreRestoreAdmission? admission,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(root, path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
        {
            throw new InvalidDataException($"Backup file '{path}' changed length before validation.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalRead = 0;
        while (true)
        {
            admission?.ThrowIfExceeded(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
            totalRead += read;
        }

        EnsureRegularFile(root, path);
        if (totalRead != expectedLength || stream.Length != expectedLength)
        {
            throw new InvalidDataException($"Backup file '{path}' changed while it was validated.");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsKnownBackupPath(SegmentCoreBackupManifest manifest, string relativePath)
    {
        if (relativePath is ".idb-revision-clock"
            or "storage-format.manifest"
            or "transaction-protocol.manifest"
            or "striped-log.manifest")
        {
            return true;
        }

        if (relativePath == SegmentCoreIndependentFiles.FloorFileName)
        {
            return manifest.StorageFormat.EngineMinor >= 1;
        }

        var checkpointBase = $"checkpoints/checkpoint-{manifest.CheckpointGeneration:D20}";
        if (relativePath == checkpointBase + ".idbidx"
            || relativePath == checkpointBase + ".idbmap")
        {
            return true;
        }

        var expectedTransaction = manifest.TransactionProtocol == SegmentCoreTransactionProtocol.MutationWriteAheadLog
            ? "transactions/mutation-wal.idbwal"
            : "transactions/commit-journal.idbtxn";
        if (relativePath == expectedTransaction)
        {
            return true;
        }

        var parts = relativePath.Split('/');
        if (parts.Length != 4
            || parts[0] != "stripes"
            || !int.TryParse(
                parts[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var stripe)
            || stripe < 0
            || stripe >= manifest.StripeCount
            || parts[1] != stripe.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)
            || parts[2] is not ("segments" or "compacted")
            || parts[2] == "compacted" && manifest.StorageFormat.EngineMinor < 1
            || !ObjectLogFormat.TryParseSegmentId(parts[3], out var segmentId)
            || parts[3] != ObjectLogFormat.SegmentFileName(segmentId))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeAndValidateRelativePath(string path, int maximumDepth)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("Backup paths must be non-empty relative paths.");
        }

        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (parts.Length > maximumDepth
            || parts.Any(static part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw new InvalidDataException($"Backup path '{path}' is not canonical.");
        }

        return normalized;
    }

    private static void EnsureRegularPath(string root, string path, bool expectDirectory)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup path '{path}' escapes its declared root.");
        }

        EnsureEntryIsRegular(fullRoot, expectDirectory: true);
        if (relative == ".")
        {
            if (!expectDirectory)
            {
                throw new InvalidDataException($"Backup path '{path}' is not a regular file.");
            }

            return;
        }

        var parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = fullRoot;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            EnsureEntryIsRegular(current, expectDirectory: index < parts.Length - 1 || expectDirectory);
        }
    }

    private static void EnsureEntryIsRegular(string path, bool expectDirectory)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || isDirectory != expectDirectory)
        {
            throw new InvalidDataException(
                $"Backup path '{path}' must be a regular {(expectDirectory ? "directory" : "file")} and cannot be a link or reparse point.");
        }

        FileSystemInfo info = expectDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidDataException($"Backup path '{path}' cannot be a symbolic link or junction.");
        }
    }

    private static void CreateRegularDirectoryTree(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var pathRoot = Path.GetPathRoot(fullDirectory)
            ?? throw new InvalidDataException($"Directory '{directory}' has no filesystem root.");
        EnsureEntryIsRegular(pathRoot, expectDirectory: true);
        var relative = Path.GetRelativePath(pathRoot, fullDirectory);
        if (relative == ".")
        {
            return;
        }

        var current = pathRoot;
        foreach (var part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
            {
                throw new InvalidDataException($"Backup path '{current}' cannot be a symbolic link or junction.");
            }

            try
            {
                EnsureEntryIsRegular(current, expectDirectory: true);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Directory.CreateDirectory(current);
                EnsureEntryIsRegular(current, expectDirectory: true);
            }
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
