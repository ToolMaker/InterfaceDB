using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Concrete envelope that backs payload metadata with file-system information.
/// The cache key is supplied by the caller (typically via the scope's file-name factory)
/// so storage identity stays consistent across cache and disk. The factory receives both the
/// materialized contract instance and its storage location.
/// </summary>
public sealed class FileObjectEnvelope<TContract> : IObjectEnvelope<TContract>, IEnvelopeCacheState<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private long _commitOrder;
    long IEnvelopeRuntimeState.CommitOrder { get => Volatile.Read(ref _commitOrder); set => Volatile.Write(ref _commitOrder, value); }
    ICommittedEnvelopeSnapshot<TContract>? IEnvelopeCacheState<TContract>.CommittedSnapshot { get; set; }
    TContract? IEnvelopeCacheState<TContract>.CachedOwner { get; set; }

    public FileObjectEnvelope()
    {
    }

    public FileObjectEnvelope(IDbContractConfigDto<TContract> contractConfig)
    {
        VersionTag = contractConfig.ActiveVersion ?? throw new ArgumentNullException($"Activate version in config is not set.");
    }

    public required string EnvelopeCacheKey { get; set; }

    public required string VersionTag { get; set; }

    public long RevisionTimestampUtcMicroseconds { get; set; }

    public TContract? ObjectInstance { get; set; }

    public WeakReference<TContract>? ObjectInstanceWeakReference { get; set; }

    public string? ImplementationTypeName { get; set; }

    public bool CacheObjectInstance { get; set; }

    public IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }
        = new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);

    public byte[]? ExtensionMetadata { get; set; }

    public string? Location { get; set; }

    public StorageObjectState State { get; set; }
}
