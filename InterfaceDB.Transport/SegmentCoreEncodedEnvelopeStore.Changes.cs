using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Idb.Transport;

public sealed partial class SegmentCoreEncodedEnvelopeStore
{
    private const string FeedPrefix = ScopedEncodedStore.ChangeJournalPrefix;
    /// <summary>Maximum retained mutation keys per authenticated partition. Expired cursors require a snapshot.</summary>
    public int RetainedChangeCount { get; private init; } = 2048;
    private sealed record Change(long Sequence, string Key);
    private sealed record Journal(Guid Epoch, long Sequence, Change[] Entries);
    private static string Cursor(Journal journal, long sequence) => $"{journal.Epoch:N}:{sequence.ToString(CultureInfo.InvariantCulture)}";
    private async ValueTask<Journal> ReadJournalAsync(string partition, CancellationToken token)
    {
        var bytes = await _store.GetAsync(FeedPrefix + partition, token).ConfigureAwait(false);
        if (bytes is null) return new(Guid.Empty, 0, []);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Change journal exceeds its bound.");
        var journal = JsonSerializer.Deserialize<Journal>(bytes) ?? throw new InvalidDataException("Invalid change journal.");
        if (journal.Sequence < 0 || journal.Entries is null || journal.Entries.Length > 100_000)
            throw new InvalidDataException("Invalid change journal.");
        long previous = 0;
        foreach (var change in journal.Entries)
        {
            if (change.Sequence <= previous || change.Sequence > journal.Sequence || string.IsNullOrEmpty(change.Key)
                || !change.Key.StartsWith(partition, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid change journal order or partition.");
            previous = change.Sequence;
        }
        return journal;
    }

    private async ValueTask<EncodedStoreResponse> ApplyTrackedBatchAsync(EncodedMutation[] mutations, string partition, CancellationToken token)
    {
        if (RetainedChangeCount is < 1 or > 100_000) throw new InvalidOperationException("Invalid change retention.");
        ValidateMutationCapacity(mutations, partition);
        var journal = await ReadJournalAsync(partition, token).ConfigureAwait(false);
        long sequence = journal.Sequence;
        var entries = journal.Entries.ToList();
        foreach (var mutation in mutations) entries.Add(new(checked(++sequence), mutation.Key));
        if (entries.Count > RetainedChangeCount) entries.RemoveRange(0, entries.Count - RetainedChangeCount);
        byte[] encoded;
        do
        {
            encoded = JsonSerializer.SerializeToUtf8Bytes(new Journal(
                journal.Epoch == Guid.Empty ? Guid.NewGuid() : journal.Epoch, sequence, entries.ToArray()));
            if (encoded.Length <= 16 * 1024 * 1024) break;
            entries.RemoveRange(0, Math.Max(1, entries.Count / 2));
        } while (true);
        // Payload and sequence history commit in one engine transaction. Uncertain acknowledgement
        // cannot leave committed data absent from catch-up history.
        await using var transaction = await _store.BeginTransactionAsync(token).ConfigureAwait(false);
        int affected = 0;
        foreach (var mutation in mutations)
        {
            if (mutation.Payload is not null) { await transaction.SaveAsync(mutation.Key, mutation.Payload, token).ConfigureAwait(false); affected++; }
            else if (await transaction.DeleteAsync(mutation.Key, token).ConfigureAwait(false)) affected++;
        }
        await transaction.SaveAsync(FeedPrefix + partition, encoded, token).ConfigureAwait(false);
        try
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            _partitionIndex.ApplyCommitted(mutations);
        }
        catch (Exception exception)
        {
            // Only a commit attempt or failed derived-state update can make committed keys
            // disagree with this index. Validation/read failures must not evict other tenants.
            _partitionIndex.Invalidate();
            if (exception is EncodedStoreRequestRejectedException)
                throw new IOException("A derived-state update failed after a commit attempt.", exception);
            throw;
        }
        return new() { AffectedCount = affected };
    }

    private void ValidateMutationCapacity(EncodedMutation[] mutations, string partition)
    {
        var snapshot = _store.GetKeysSnapshot();
        var feedCount = snapshot.Count(key => key.StartsWith(FeedPrefix, StringComparison.Ordinal));
        if (!snapshot.Contains(FeedPrefix + partition, StringComparer.Ordinal)
            && feedCount >= _maximumEnumerationKeys)
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.LimitExceeded,
                "The mutation would exceed the configured change-partition capacity.");
        var keys = snapshot.Where(key => !key.StartsWith(FeedPrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var originalGlobalCount = keys.Count;
        var originalPartitionCount = partition.Length == 0
            ? 0 : keys.Count(key => key.StartsWith(partition, StringComparison.Ordinal));
        var partitionCount = originalPartitionCount;

        foreach (var mutation in mutations)
        {
            if (partition.Length != 0 && !mutation.Key.StartsWith(partition, StringComparison.Ordinal))
                throw new ArgumentException("A partitioned mutation escaped its authenticated partition.");
            if (mutation.Payload is not null)
            {
                if (keys.Add(mutation.Key) && partition.Length != 0) partitionCount++;
            }
            else if (keys.Remove(mutation.Key) && partition.Length != 0) partitionCount--;
        }

        if (keys.Count > _maximumEnumerationKeys && keys.Count > originalGlobalCount)
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.LimitExceeded,
                "The mutation would exceed the configured store key capacity.");
        if (partition.Length != 0 && partitionCount > _maximumPartitionKeys
            && partitionCount > originalPartitionCount)
            throw new EncodedStoreRequestRejectedException(RemoteErrorCode.LimitExceeded,
                "The mutation would exceed the configured partition key capacity.");
    }

    internal HashSet<string> GetChangePartitions() => _store.GetKeysSnapshot()
        .Where(key => key.StartsWith(FeedPrefix, StringComparison.Ordinal))
        .Select(key => key[FeedPrefix.Length..])
        .ToHashSet(StringComparer.Ordinal);

    private async ValueTask<EncodedStoreResponse> ReadChangesAsync(EncodedStoreRequest request, CancellationToken token)
    {
        var journal = await ReadJournalAsync(request.Partition, token).ConfigureAwait(false);
        long after = 0;
        bool reset = request.AfterKey is null;
        if (request.AfterKey is { } cursor)
        {
            var pieces = cursor.Split(':');
            if (pieces.Length != 2 || !Guid.TryParseExact(pieces[0], "N", out var epoch)
                || !long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out after))
                throw new ArgumentException("Invalid change cursor.");
            reset = epoch != journal.Epoch || after > journal.Sequence
                || (journal.Entries.Length > 0 && after < journal.Entries[0].Sequence - 1);
        }
        var high = Cursor(journal, journal.Sequence);
        if (reset) return new() { Found = true, Records = [], Payload = Encoding.UTF8.GetBytes(high) };
        var selected = new List<Change>();
        long responseBytes = FrameOverheadReserve;
        foreach (var entry in journal.Entries.Where(entry => entry.Sequence > after))
        {
            var bytes = Encoding.UTF8.GetByteCount(entry.Key) + RecordOverheadReserve;
            if (selected.Count >= request.PageSize || responseBytes + bytes > _limits.MaximumFrameBytes) break;
            selected.Add(entry); responseBytes += bytes;
        }
        var page = selected.ToArray();
        return new()
        {
            Records = page.Select(entry => new EncodedRecord(entry.Key, [])).ToArray(),
            Payload = Encoding.UTF8.GetBytes(high),
            NextAfterKey = page.Length > 0 && page[^1].Sequence < journal.Sequence ? Cursor(journal, page[^1].Sequence) : null
        };
    }
}
