using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

// Optional in-memory ownership state. It is never part of an encoded envelope.
// Custom envelopes without this capability use weak-table fallback storage.
internal interface IEnvelopeRuntimeState
{
    long CommitOrder { get; set; }
}

internal interface IEnvelopeCacheState<TContract> : IEnvelopeRuntimeState
    where TContract : class, IStoredContract<TContract>
{
    ICommittedEnvelopeSnapshot<TContract>? CommittedSnapshot { get; set; }
    TContract? CachedOwner { get; set; }
}
