using MessagePack;

namespace Idb.Commercial.Repositories;

[MessagePackObject(AllowPrivate = true)]
internal sealed record ResilientEnvelopeMetadata(
    [property: Key(0)] byte FormatVersion,
    [property: Key(1)] long RevisionTimestampUtcMicroseconds,
    [property: Key(2)] bool IsTombstone,
    [property: Key(3)] Guid PendingMutationId,
    [property: Key(4)] PendingMutationKind PendingMutationKind,
    [property: Key(5)] Guid LastAppliedMutationId, [property: Key(6)] long BasePeerRevision = -1, [property: Key(7)] string? CacheScope = null, [property: Key(8)] string? RemoteIdentity = null, [property: Key(9)] string? RemoteStorageKey = null)
{
    internal const byte CurrentFormatVersion = 3;

    internal static ResilientEnvelopeMetadata Empty { get; } = new(
        CurrentFormatVersion,
        RevisionTimestampUtcMicroseconds: 0,
        IsTombstone: false,
        PendingMutationId: Guid.Empty,
        PendingMutationKind.None,
        LastAppliedMutationId: Guid.Empty);

    [IgnoreMember]
    internal bool HasPendingMutation => PendingMutationId != Guid.Empty;
}
