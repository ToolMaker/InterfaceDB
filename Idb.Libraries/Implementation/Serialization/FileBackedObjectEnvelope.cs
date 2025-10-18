using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Concrete envelope that backs payload metadata with file-system information.
/// The cache key uses the full file path plus the creation timestamp so the
/// value remains stable across process boundaries yet unique per file instance.
/// </summary>
internal sealed class FileBackedObjectEnvelope<TContract> : IObjectEnvelope<TContract>
    where TContract : class, IRepoDto<TContract>
{
    public FileBackedObjectEnvelope(
        FileInfo file,
        string versionTag,
        Type objectType,
        byte[] payload)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        VersionTag = string.IsNullOrWhiteSpace(versionTag)
            ? throw new ArgumentException("Version tag cannot be null or whitespace.", nameof(versionTag))
            : versionTag;
        ObjectType = objectType ?? throw new ArgumentNullException(nameof(objectType));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));

        CacheKey = $"{File.FullName}|{File.CreationTimeUtc.Ticks}";
    }

    public string CacheKey { get; }

    public string VersionTag { get; }

    public Type ObjectType { get; }

    public FileInfo File { get; }

    public byte[] Payload { get; set; }
}
