using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Commercial.Repositories;

public sealed partial class ResilientRepository<TContract> where TContract : class, IStoredContract<TContract>
{
    private string? _compositionCursor, _nextCompositionCursor;
    private bool _cursorLoaded;
    private string CompositionCursorScope => _policy.DatasetId + "|" + CompositionOptions!.CacheScope + "|" + _compositionIdentity;
    private async ValueTask<Dictionary<string, TContract>> ReadRemoteCompositionAsync(Dictionary<string, TContract> local, CancellationToken token)
    {
        if (_peer is not IRepositoryDeltaSource<TContract> source)
            return await CompositionSnapshotAsync(_peer, false, token).ConfigureAwait(false);
        if (!_cursorLoaded)
        {
            _compositionCursor = CompositionOptions!.CursorStore is { } store
                ? await store.ReadAsync(CompositionCursorScope, token).ConfigureAwait(false) : null;
            _cursorLoaded = true;
        }
        var pendingKeys = local.Where(pair => ReadMetadata(pair.Value).HasPendingMutation)
            .Select(pair => ReadMetadata(pair.Value).RemoteStorageKey ?? pair.Key).ToArray();
        var delta = await source.ReadDeltaAsync(_compositionCursor, pendingKeys, token).ConfigureAwait(false);
        var remote = new Dictionary<string, TContract>(StringComparer.Ordinal);
        if (!delta.IsSnapshot)
        {
            foreach (var pair in local.Where(pair => !ReadMetadata(pair.Value).HasPendingMutation))
            {
                var copy = CreateApplicationCopy(pair.Value, pair.Key);
                var metadata = ReadMetadata(pair.Value);
                copy.Envelope!.RevisionTimestampUtcMicroseconds = metadata.BasePeerRevision;
                copy.Envelope.ExtensionMetadata = ResilientEnvelopeMetadataCodec.Encode(metadata with
                    { CacheScope = null, RemoteIdentity = null, BasePeerRevision = 0 });
                remote.Add(pair.Key, copy);
            }
            foreach (var physical in delta.DeletedPhysicalKeys)
                foreach (var pair in local.Where(pair => (ReadMetadata(pair.Value).RemoteStorageKey ?? pair.Key) == physical))
                    remote.Remove(pair.Key);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in delta.Records)
        {
            var key = GetRequiredStorageKey(entity);
            if (!seen.Add(key) || entity.Envelope is null || entity.Envelope.RevisionTimestampUtcMicroseconds <= 0
                || ReadMetadata(entity).HasPendingMutation)
                throw new InvalidDataException("Invalid remote delta.");
            remote[key] = entity;
        }
        if (remote.Count > _policy.MaximumSynchronizationScanItems) throw new InvalidDataException("Remote delta exceeds the record bound.");
        _nextCompositionCursor = delta.Cursor;
        return remote;
    }
}
