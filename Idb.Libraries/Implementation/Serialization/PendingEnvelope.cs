using System;
using System.IO;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Placeholder envelope used when a contract has not yet been persisted.
/// The cache key is generated but contains no location-specific information.
/// </summary>
public sealed class PendingEnvelope<TContract> : IObjectEnvelope<TContract, FileInfo>
    where TContract : class, IRepoDto<TContract, FileInfo>
{
    public PendingEnvelope()
    {
        CacheKey = Guid.NewGuid().ToString("N");
        Location = new FileInfo(Path.Combine(Path.GetTempPath(), CacheKey + ".pending"));
        Payload = Array.Empty<byte>();
    }

    public string CacheKey { get; }

    public string VersionTag { get; set; } = "0.0.0";

    public TContract? ObjectInstance { get; set; }

    public FileInfo Location { get; }

    public byte[] Payload { get; set; }
}
