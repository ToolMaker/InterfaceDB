using System.Text;
using Idb.Libraries.Repositories;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Transport.Repositories;

public sealed partial class TcpRepository<TContract> : IRepositoryDeltaSource<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public async ValueTask<RepositoryDelta<TContract>> ReadDeltaAsync(string? cursor,
        IReadOnlyCollection<string> includePhysicalKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(includePhysicalKeys);
        if (includePhysicalKeys.Count > _options.MaximumSnapshotRecords) throw new ArgumentException("Pending key bound exceeded.");
        string nextCursor = "";
        bool snapshot = false;
        var missing = new List<string>();
        return await WithRepositoryAsync(async repository =>
        {
            var records = new List<TContract>();
            await foreach (var entity in repository.QueryManyAsync(_ => true, cancellationToken).ConfigureAwait(false)) records.Add(entity);
            return new RepositoryDelta<TContract>(nextCursor, snapshot, records, missing);
        }, cancellationToken, async (session, token) =>
        {
            var keys = new HashSet<string>(includePhysicalKeys, StringComparer.Ordinal);
            var after = cursor;
            int pages = 0;
            do
            {
                var page = await session.ExecuteAsync(new() { Command = EncodedStoreCommand.Changes,
                    AfterKey = after, PageSize = Math.Min(256, session.Limits.MaximumPageItems) }, token).ConfigureAwait(false);
                nextCursor = Encoding.UTF8.GetString(page.Payload!);
                snapshot = page.Found;
                if (snapshot) return session; // Cursor expired or first connection: bounded full snapshot under this same lease.
                foreach (var record in page.Records!) keys.Add(record.Key);
                if (keys.Count > _options.MaximumSnapshotRecords + 1 || ++pages > 100_000)
                    throw new InvalidDataException("Catch-up exceeds the snapshot bound.");
                if (page.NextAfterKey == after && after is not null) throw new InvalidDataException("Change cursor did not advance.");
                after = page.NextAfterKey;
            } while (after is not null);
            keys.Add(".idb-remote-repository-clock-v1");
            var encoded = new List<EncodedRecord>();
            long bytes = 0;
            foreach (var key in keys)
            {
                var payload = await session.GetAsync(key, token).ConfigureAwait(false);
                if (payload is null) { missing.Add(key); continue; }
                bytes += payload.Length;
                if (bytes > _options.MaximumSnapshotBytes) throw new InvalidDataException("Catch-up byte bound exceeded.");
                encoded.Add(new(key, payload));
            }
            return new SnapshotStore(session, encoded);
        }).ConfigureAwait(false);
    }
    private sealed class SnapshotStore(IEncodedEnvelopeStore session, List<EncodedRecord> records) : IEncodedEnvelopeStore
    {
        public EncodedStoreCapabilities Capabilities => session.Capabilities;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Command != EncodedStoreCommand.Enumerate) return session.ExecuteAsync(request, cancellationToken);
            var all = records.OrderBy(record => record.Key, StringComparer.Ordinal)
                .Where(record => request.AfterKey is null || StringComparer.Ordinal.Compare(record.Key, request.AfterKey) > 0).ToArray();
            var page = all.Take(request.PageSize).ToArray();
            return ValueTask.FromResult(new EncodedStoreResponse { Records = page,
                NextAfterKey = all.Length > page.Length ? page[^1].Key : null });
        }
    }
}