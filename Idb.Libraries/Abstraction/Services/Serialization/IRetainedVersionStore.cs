using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

public enum WriteHistoryRequirement { None, OriginalCommittedVersion }
public enum EnvelopeWriteDisposition { Commit, Reject, NoChange }

public sealed record RetainedVersionCapability(bool Supported, string? Limitation = null);
public sealed record RetainedVersionStatistics(int ActiveSessions, int RetainedRecords, long RetainedBytes,
    long ArchivedBytes, long CopiedBytes, long ReadBytes, long CleanupFailures);

/// <summary>A store-issued identity. It contains no object, prior envelope, or caller-controlled physical address.</summary>
public sealed class RetainedVersionReference
{
    internal RetainedVersionReference(object owner, string key, long revision, Guid recordId)
    { Owner = owner; Key = key; Revision = revision; RecordId = recordId; }
    internal object Owner { get; }
    public string Key { get; }
    public long Revision { get; }
    public Guid RecordId { get; }
}

public sealed class RetainedVersionUnavailableException(string message) : InvalidOperationException(message);

public interface IRevisionEditSession<TContract> : IAsyncDisposable where TContract : class, IStoredContract<TContract>
{
    TContract Working { get; }
    RetainedVersionReference OriginalVersion { get; }
    long ExpectedRevision { get; }
    bool IsCompleted { get; }
    ValueTask<TContract> SaveAsync(CancellationToken cancellationToken = default);
    ValueTask<TContract> ReadOriginalAsync(CancellationToken cancellationToken = default);
    void Renew(TimeSpan duration);
}

/// <summary>Optional provider capability; acquisition selects and retains committed bytes atomically.</summary>
public interface IRetainedVersionStore<TContract> where TContract : class, IStoredContract<TContract>
{
    RetainedVersionCapability RetainedVersions { get; }
    RetainedVersionStatistics RetentionStatistics { get; }
    ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey,
        Func<TContract, CancellationToken, ValueTask<TContract>> saveAsync,
        CancellationToken cancellationToken = default);
}
