namespace Idb.Commercial.Repositories.Peers;

/// <summary>Balances distinct record reads across eligible holders, then fans each result out to missing peers.</summary>
public sealed class PeerRangePlanner : IPeerRangePlanner
{
    public IReadOnlyList<PeerTransfer> Plan(IReadOnlyList<PeerRecordDescriptor> authoritative,
        IReadOnlyDictionary<string, IReadOnlyList<PeerRecordDescriptor>> inventories,
        Func<string, PeerRecordDescriptor, bool>? receive = null, Func<string, PeerRecordDescriptor, bool>? provide = null)
    {
        var indexes = inventories.ToDictionary(p => p.Key, p => p.Value.ToDictionary(r => r.Sequence));
        var loads = inventories.Keys.ToDictionary(p => p, _ => 0);
        var result = new List<PeerTransfer>();
        foreach (var record in authoritative.OrderBy(r => r.Sequence))
        {
            var holders = indexes.Where(p => p.Value.TryGetValue(record.Sequence, out var held) && held == record)
                .Select(p => p.Key).ToArray();
            var targets = indexes.Keys.Except(holders).Where(id => receive?.Invoke(id, record) != false).ToArray();
            holders = holders.Where(id => provide?.Invoke(id, record) != false).ToArray();
            if (holders.Length == 0 || targets.Length == 0) continue;
            var source = holders.OrderBy(p => loads[p]).ThenBy(p => p, StringComparer.Ordinal).First();
            loads[source]++;
            result.Add(new(source, record, targets));
        }
        return result;
    }

    public static IReadOnlyList<PeerRange> Compact(IEnumerable<long> sequences)
    {
        var result = new List<PeerRange>();
        foreach (var sequence in sequences.Distinct().Order())
        {
            if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequences));
            if (result.Count > 0 && result[^1].End < long.MaxValue && result[^1].End + 1 == sequence)
                result[^1] = result[^1] with { End = sequence };
            else result.Add(new(sequence, sequence));
        }
        return result;
    }
}
