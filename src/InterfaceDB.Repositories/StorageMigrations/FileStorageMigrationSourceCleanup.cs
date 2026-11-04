using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Deletes only captured, unchanged File payloads. Never recursively deletes a source tree.</summary>
public sealed class FileStorageMigrationSourceCleanup<T>(string sourceRoot, IStorageMigrationSource<T> source,
    ICommittedEnvelopeInventory<T> inventory, ICommittedEnvelopeSnapshotSource<T> snapshots,
    IFilePublication publication) : IStorageMigrationSourceCleanup<T> where T : class, IStoredContract<T>
{
    private readonly string _root = Path.GetFullPath(sourceRoot);

    public async ValueTask<IReadOnlyList<StorageMigrationCleanupRecord>> CaptureAsync(
        IReadOnlyDictionary<string, string> sourceFingerprints, CancellationToken cancellationToken)
    {
        var records = new List<StorageMigrationCleanupRecord>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var identity in inventory.EnumerateCommittedIdentitiesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!keys.Add(identity.EnvelopeCacheKey) || !sourceFingerprints.TryGetValue(identity.EnvelopeCacheKey, out var hash))
                throw new InvalidDataException("Cleanup inventory differs from the verified source inventory.");
            var path = ValidatePath(identity.Location as string ?? throw new InvalidDataException("File cleanup requires physical source locations."));
            records.Add(new(identity.EnvelopeCacheKey, hash, Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/')));
        }
        if (keys.Count != sourceFingerprints.Count) throw new InvalidDataException("Cleanup inventory is incomplete.");
        return records;
    }

    public async ValueTask<StorageMigrationCleanupResult> ExecuteAsync(IReadOnlyList<StorageMigrationCleanupRecord> records,
        IStorageMigrationFingerprint<T> fingerprint, int maximumRecordBytes, int maximumRecords,
        CancellationToken cancellationToken)
    {
        await using var lease = await source.AcquireSourceLeaseAsync(cancellationToken).ConfigureAwait(false);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var uniquePaths = new HashSet<string>(FileSystemPathIdentity.ResolveComparer(_root));
        foreach (var record in records)
        {
            if (Path.IsPathRooted(record.Locator)) throw new InvalidDataException("Cleanup locators must be relative to their source.");
            var path = ValidatePath(Path.Combine(_root, record.Locator.Replace('/', Path.DirectorySeparatorChar)));
            if (!paths.TryAdd(record.Key, path) || !uniquePaths.Add(path)) throw new InvalidDataException("Duplicate cleanup identity.");
        }
        var identities = new Dictionary<string, IObjectEnvelope<T>>(StringComparer.Ordinal);
        await foreach (var identity in inventory.EnumerateCommittedIdentitiesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (identities.Count >= maximumRecords) throw new InvalidDataException("Cleanup source exceeds MaximumRecords.");
            ValidatePath(identity.Location as string ?? throw new InvalidDataException("File cleanup requires a physical location."));
            if (!identities.TryAdd(identity.EnvelopeCacheKey, identity)) throw new InvalidDataException("Duplicate source cleanup key.");
        }
        var deleted = 0;
        var retained = identities.Keys.Count(key => !paths.ContainsKey(key));
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[record.Key];
            ValidatePath(path);
            if (!identities.TryGetValue(record.Key, out var identity))
            {
                if (File.Exists(path)) { retained++; continue; }
                // A prior deletion may have succeeded before its directory barrier failed.
                var directory = Path.GetDirectoryName(path)!;
                if (Directory.Exists(directory)) publication.FlushDirectory(directory);
                else { retained++; } // An unavailable source subtree is not proof of cleanup.
                continue;
            }
            if (!uniquePaths.Comparer.Equals(path, Path.GetFullPath((string)identity.Location!))) { retained++; continue; }
            try
            {
                var snapshot = await snapshots.CaptureCommittedAsync(identity, maximumRecordBytes, cancellationToken).ConfigureAwait(false);
                if (snapshot is null) { retained++; continue; }
                var entity = snapshot.Materialize();
                var envelope = entity.Envelope ?? throw new InvalidDataException("Cleanup snapshot has no envelope.");
                var current = new StorageMigrationRecord<T>(envelope.EnvelopeCacheKey, envelope.VersionTag,
                    envelope.RevisionTimestampUtcMicroseconds, envelope.ExtensionMetadata?.ToArray(), () => snapshot.Materialize(false));
                if (current.Key != record.Key || fingerprint.Compute(current) != record.Fingerprint) { retained++; continue; }
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or AccessViolationException))
            {
                // Unreadable or undecryptable original data is retained, never guessed to match.
                retained++; continue;
            }
            publication.Delete(path);
            if (File.Exists(path)) throw new IOException("Source cleanup did not remove the verified record.");
            deleted++;
        }
        return new(deleted, retained);
    }

    private string ValidatePath(string path)
    {
        var resolved = Path.GetFullPath(path);
        var comparison = FileSystemPathIdentity.ResolveComparer(_root).Equals("a", "A") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Cleanup path is outside its source directory.");
        StorageMigrationPaths.Validate(resolved);
        return resolved;
    }
}
