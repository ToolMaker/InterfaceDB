using System.Numerics;

namespace ContainsIndexBenchmarks;

internal readonly record struct MatchDigest(int Count, ulong Sum, ulong Xor)
{
    public static MatchDigest Empty => default;

    public MatchDigest Add(int documentId)
    {
        var mixed = Mix(unchecked((uint)documentId + 0x9E3779B9u));
        return new MatchDigest(
            Count + 1,
            unchecked(Sum + mixed),
            Xor ^ BitOperations.RotateLeft(mixed, documentId & 63));
    }

    public MatchDigest Combine(MatchDigest other) => new(
        Count + other.Count,
        unchecked(Sum + other.Sum),
        Xor ^ other.Xor);

    private static ulong Mix(uint value)
    {
        ulong z = value;
        z = (z ^ (z >> 16)) * 0x7FEB352DUL;
        z = (z ^ (z >> 15)) * 0x846CA68BUL;
        return z ^ (z >> 16);
    }
}

internal readonly record struct QueryStats(MatchDigest Matches, int CandidateCount)
{
    public static QueryStats All(int documentCount)
    {
        var digest = MatchDigest.Empty;
        for (var i = 0; i < documentCount; i++)
        {
            digest = digest.Add(i);
        }

        return new QueryStats(digest, documentCount);
    }
}

internal interface IContainsIndex : IDisposable
{
    string Name { get; }

    bool SupportsIncrementalMutation => false;

    bool SupportsConcurrentQueries => true;

    void Build(string[] values);

    QueryStats Query(string needle);

    void Upsert(int documentId, string value) => throw new NotSupportedException($"{Name} is immutable.");

    void Remove(int documentId) => throw new NotSupportedException($"{Name} is immutable.");
}

internal static class ContainsOracle
{
    public static QueryStats Query(IReadOnlyList<string> values, string needle)
    {
        var digest = MatchDigest.Empty;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(i);
            }
        }

        return new QueryStats(digest, values.Count);
    }
}

internal static class IndexHelpers
{
    public static ulong PackTrigram(ReadOnlySpan<char> value, int offset) =>
        ((ulong)value[offset] << 32) |
        ((ulong)value[offset + 1] << 16) |
        value[offset + 2];

    public static bool ContainsSorted(int[] values, int target)
    {
        var low = 0;
        var high = values.Length - 1;
        while (low <= high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            var current = values[middle];
            if (current == target)
            {
                return true;
            }

            if (current < target)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    public static int[] DistinctPackedTrigrams(string value)
    {
        if (value.Length < 3)
        {
            return [];
        }

        var grams = new ulong[value.Length - 2];
        for (var i = 0; i < grams.Length; i++)
        {
            grams[i] = PackTrigram(value, i);
        }

        Array.Sort(grams);
        var distinctCount = 1;
        for (var i = 1; i < grams.Length; i++)
        {
            if (grams[i] != grams[distinctCount - 1])
            {
                grams[distinctCount++] = grams[i];
            }
        }

        // This helper is only used in diagnostics that need integers, not the hot query path.
        var result = new int[distinctCount * 2];
        for (var i = 0; i < distinctCount; i++)
        {
            result[i * 2] = unchecked((int)(grams[i] >> 32));
            result[(i * 2) + 1] = unchecked((int)grams[i]);
        }

        return result;
    }
}
