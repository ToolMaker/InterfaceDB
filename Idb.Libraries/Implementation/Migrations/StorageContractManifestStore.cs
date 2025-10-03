using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Migrations;

namespace Idb.Libraries.Implementation.Migrations
{
    public sealed class StorageContractManifestStore
    {
        private const string ManifestFolderName = ".manifests";
        private const string ManifestFileSuffix = ".manifest.json";
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public async Task SaveAsync(IScopeDescriptor scope, StorageContractManifest manifest, CancellationToken cancellationToken)
        {
            if (scope is null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (manifest is null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var file = EnsureManifestFile(scope.Directory, manifest.VersionTag);

            await using var stream = new FileStream(file.FullName, FileMode.Create, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, manifest, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        public async Task<StorageContractManifest?> LoadAsync(IScopeDescriptor scope, string versionTag, CancellationToken cancellationToken)
        {
            if (scope is null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            if (string.IsNullOrWhiteSpace(versionTag))
            {
                throw new ArgumentException("Version tag cannot be null or whitespace.", nameof(versionTag));
            }

            var directory = GetManifestDirectory(scope.Directory);
            if (!directory.Exists)
            {
                return null;
            }

            var file = new FileInfo(Path.Combine(directory.FullName, versionTag + ManifestFileSuffix));
            if (!file.Exists)
            {
                return null;
            }

            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<StorageContractManifest>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        public StorageContractManifest? LoadLatest(IScopeDescriptor scope)
        {
            if (scope is null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            var directory = GetManifestDirectory(scope.Directory);
            if (!directory.Exists)
            {
                return null;
            }

            var candidate = directory.EnumerateFiles("*" + ManifestFileSuffix, SearchOption.TopDirectoryOnly)
                                     .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                                     .FirstOrDefault();

            if (candidate is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<StorageContractManifest>(File.ReadAllText(candidate.FullName), _jsonOptions);
        }

        private FileInfo EnsureManifestFile(DirectoryInfo scopeDirectory, string versionTag)
        {
            var directory = GetManifestDirectory(scopeDirectory);
            if (!directory.Exists)
            {
                Directory.CreateDirectory(directory.FullName);
                directory.Refresh();
            }

            var path = Path.Combine(directory.FullName, versionTag + ManifestFileSuffix);
            return new FileInfo(path);
        }

        private static DirectoryInfo GetManifestDirectory(DirectoryInfo scopeDirectory)
            => new DirectoryInfo(Path.Combine(scopeDirectory.FullName, ManifestFolderName));
    }
}
