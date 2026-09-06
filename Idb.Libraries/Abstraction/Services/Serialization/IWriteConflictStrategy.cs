using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Runs under the provider's entity write guard. Current objects are isolated, lazily materialized snapshots.</summary>
public interface IWriteConflictStrategy<TContract> where TContract : class, IStoredContract<TContract>
{
    WriteHistoryRequirement HistoryRequirement => WriteHistoryRequirement.None;
    ValueTask<EnvelopeWriteDecision<TContract>> DecideAsync(EnvelopeWriteConflict<TContract> context,
        CancellationToken cancellationToken = default);
}

public record EnvelopeWriteConflict<TContract>(string Key, long ExpectedRevision, long? CurrentRevision,
    bool IsInsert, IObjectEnvelope<TContract> Candidate, Func<CancellationToken, ValueTask<TContract?>> ReadCurrentAsync)
    where TContract : class, IStoredContract<TContract>
{
    public virtual RetainedVersionReference? OriginalVersion => null;
    public virtual bool IsDelete => false;
    public virtual ValueTask<TContract> ReadOriginalAsync(CancellationToken token = default)
        => throw new RetainedVersionUnavailableException("This write has no retained original version.");
    public virtual ValueTask<TContract> ReadIncomingAsync(CancellationToken token = default)
        => throw new NotSupportedException("This provider has no isolated candidate materializer.");
    public virtual ValueTask<IObjectEnvelope<TContract>> PrepareResolvedAsync(TContract resolved, CancellationToken token = default)
        => throw new NotSupportedException("This provider cannot stage a resolved candidate.");
}

/// <summary>A future resolver may return a replacement candidate; null accepts the supplied candidate.</summary>
public record EnvelopeWriteDecision<TContract>(bool Accepted, IObjectEnvelope<TContract>? ResolvedCandidate = null)
    where TContract : class, IStoredContract<TContract>
{
    public virtual EnvelopeWriteDisposition Disposition => Accepted ? EnvelopeWriteDisposition.Commit : EnvelopeWriteDisposition.Reject;
    public virtual string? Reason => null;
    public virtual IReadOnlyList<string> Properties => Array.Empty<string>();
}

public sealed record EnvelopeWriteRejected<TContract> : EnvelopeWriteDecision<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public EnvelopeWriteRejected(string reason, IReadOnlyList<string>? properties = null) : base(false)
    { Reason = reason; Properties = Array.AsReadOnly(properties?.ToArray() ?? []); }
    public override string Reason { get; }
    public override IReadOnlyList<string> Properties { get; }
}

public sealed record EnvelopeWriteNoChange<TContract>(IObjectEnvelope<TContract> Current)
    : EnvelopeWriteDecision<TContract>(true, Current) where TContract : class, IStoredContract<TContract>
{ public override EnvelopeWriteDisposition Disposition => EnvelopeWriteDisposition.NoChange; }

public sealed class WriteConflictException<TContract> : InvalidOperationException
    where TContract : class, IStoredContract<TContract>
{
    public string Key { get; }
    public long ExpectedRevision { get; }
    public long? CurrentRevision { get; }
    public string? Reason { get; }
    public IReadOnlyList<string> Properties { get; }
    private readonly Func<CancellationToken, ValueTask<TContract?>> _readCurrent;

    public WriteConflictException(EnvelopeWriteConflict<TContract> context) : this(context, null) { }

    public WriteConflictException(EnvelopeWriteConflict<TContract> context, EnvelopeWriteDecision<TContract>? decision)
        : base($"Write conflict for '{context.Key}': expected revision {context.ExpectedRevision}, committed revision {context.CurrentRevision?.ToString() ?? "missing"}.")
    {
        Key = context.Key; ExpectedRevision = context.ExpectedRevision; CurrentRevision = context.CurrentRevision;
        _readCurrent = context.ReadCurrentAsync;
        Reason = decision?.Reason; Properties = decision?.Properties ?? Array.Empty<string>();
    }

    /// <summary>Reads a fresh isolated committed object. It may be newer than the revision at rejection.</summary>
    public ValueTask<TContract?> ReadCurrentAsync(CancellationToken cancellationToken = default) => _readCurrent(cancellationToken);
}
