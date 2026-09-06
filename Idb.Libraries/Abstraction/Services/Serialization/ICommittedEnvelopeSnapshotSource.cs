using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Optional infrastructure capability: uncached committed payload capture. The caller
/// must coordinate repository-owned mutations; this is not a cross-process snapshot transaction.</summary>
public interface ICommittedEnvelopeSnapshotSource<TContract> where TContract : class, IStoredContract<TContract>
{
    ValueTask<ICommittedEnvelopeSnapshot<TContract>?> CaptureCommittedAsync(
        IObjectEnvelope<TContract> identity, int maximumBytes, CancellationToken cancellationToken = default);
}

/// <summary>Immutable encoded committed state; every materialization must be independent of
/// cached storage objects and all other materializations.</summary>
public interface ICommittedEnvelopeSnapshot<TContract> where TContract : class, IStoredContract<TContract>
{
    int ByteLength { get; }
    TContract Materialize(bool attachEnvelope = true);
}
