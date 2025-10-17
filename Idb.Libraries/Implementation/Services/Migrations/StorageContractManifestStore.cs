using System.Linq;
using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Libraries.Implementation.Services.Migrations;

public sealed class StorageContractManifestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task SaveAsync(IScopeDescriptor scope, IStorageContractManifest manifest, CancellationToken cancellationToken)
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

        await using var stream = new FileStream(file.FullName, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 16 * 1024,
        });

        var payload = SerializableManifest.FromInterface(manifest);
        await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IStorageContractManifest?> LoadAsync(IScopeDescriptor scope, string versionTag, CancellationToken cancellationToken)
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
        var file = new FileInfo(Path.Combine(directory.FullName, versionTag + ".json"));
        if (!file.Exists)
        {
            return null;
        }

        await using var stream = new FileStream(file.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous,
            BufferSize = 16 * 1024,
        });

        var payload = await JsonSerializer.DeserializeAsync<SerializableManifest>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return payload?.ToManifest();
    }

    public async Task<IStorageContractManifest?> LoadLatestAsync(IScopeDescriptor scope, CancellationToken cancellationToken)
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

        var latest = directory
            .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => file.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        await using var stream = new FileStream(latest.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous,
            BufferSize = 16 * 1024,
        });

        var payload = await JsonSerializer.DeserializeAsync<SerializableManifest>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return payload?.ToManifest();
    }

    private static FileInfo EnsureManifestFile(DirectoryInfo scopeDirectory, string versionTag)
    {
        var directory = GetManifestDirectory(scopeDirectory);
        if (!directory.Exists)
        {
            directory.Create();
            directory.Refresh();
        }

        return new FileInfo(Path.Combine(directory.FullName, versionTag + ".json"));
    }

    private static DirectoryInfo GetManifestDirectory(DirectoryInfo scopeDirectory)
    {
        if (scopeDirectory is null)
        {
            throw new ArgumentNullException(nameof(scopeDirectory));
        }

        return new DirectoryInfo(Path.Combine(scopeDirectory.FullName, ".manifests"));
    }
}

internal sealed record SerializableManifest(
    string ContractTypeName,
    string ContractAssemblyName,
    string VersionTag,
    DateTimeOffset CreatedAt,
    SerializableMember[] Members,
    string Hash)
{
    public static SerializableManifest FromInterface(IStorageContractManifest manifest)
        => new(
            manifest.ContractTypeName,
            manifest.ContractAssemblyName,
            manifest.VersionTag,
            manifest.CreatedAt,
            manifest.Members.Select(m => new SerializableMember(m.Name, m.MemberTypeFullName, m.IsCollection, m.IsNullable)).ToArray(),
            manifest.Hash);

    public StorageContractManifest ToManifest()
        => new()
        {
            ContractTypeName = ContractTypeName,
            ContractAssemblyName = ContractAssemblyName,
            VersionTag = VersionTag,
            CreatedAt = CreatedAt,
            Members = Members.Select(m => (IStorageContractMember)new StorageContractMember(m.Name, m.MemberTypeFullName, m.IsCollection, m.IsNullable)).ToArray(),
            Hash = Hash
        };
}

internal sealed record SerializableMember(string Name, string MemberTypeFullName, bool IsCollection, bool IsNullable);
