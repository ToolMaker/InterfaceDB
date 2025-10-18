using System;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Concrete envelope that backs payload metadata with file-system information.
/// The cache key is supplied by the caller (typically via the scope's file-name factory)
/// so storage identity stays consistent across cache and disk. The factory receives both the
/// materialized contract instance and its storage location.
/// </summary>
internal sealed class FileBackedObjectEnvelope<TContract> : IObjectEnvelope<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    public FileBackedObjectEnvelope(
        FileInfo location,
        string versionTag,
        TContract objectInstance,
        byte[] payload,
        Func<TContract, FileInfo, string> cacheKeyFactory)
    {
        Location = location ?? throw new ArgumentNullException(nameof(location));
        ObjectInstance = objectInstance ?? throw new ArgumentNullException(nameof(objectInstance));
        if (cacheKeyFactory is null)
        {
            throw new ArgumentNullException(nameof(cacheKeyFactory));
        }
        
        VersionTag = string.IsNullOrWhiteSpace(versionTag)
            ? throw new ArgumentException("Version tag cannot be null or whitespace.", nameof(versionTag))
            : versionTag;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));

        CacheKey = cacheKeyFactory(ObjectInstance, Location);
    }

    public string CacheKey { get; }

    public string VersionTag { get; }

    public TContract? ObjectInstance { get; set; }

    public FileInfo Location { get; }

    public byte[] Payload { get; set; }
}
