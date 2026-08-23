using System.Buffers;
using System.Numerics;
using Roaring.Net.CRoaring;

namespace ContainsIndexBenchmarks;

internal sealed class NaiveStringTrigramIndex : IContainsIndex
{
    private readonly Dictionary<string, int[]> _postings = new(StringComparer.Ordinal);
    private string[] _values = [];

    public string Name => "Naive string/HashSet trigram";

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _postings.Clear();
        var builders = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.Ordinal);

        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            unique.Clear();
            var value = values[documentId];
            for (var i = 0; i <= value.Length - 3; i++)
            {
                unique.Add(value.Substring(i, 3));
            }

            foreach (var gram in unique)
            {
                if (!builders.TryGetValue(gram, out var posting))
                {
                    posting = [];
                    builders.Add(gram, posting);
                }

                posting.Add(documentId);
            }
        }

        foreach (var (gram, posting) in builders)
        {
            _postings.Add(gram, posting.ToArray());
        }
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return ContainsOracle.Query(_values, needle);
        }

        var grams = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= needle.Length - 3; i++)
        {
            grams.Add(needle.Substring(i, 3));
        }

        int[]? shortest = null;
        var postingArrays = new List<int[]>(grams.Count);
        foreach (var gram in grams)
        {
            if (!_postings.TryGetValue(gram, out var posting))
            {
                return default;
            }

            postingArrays.Add(posting);
            if (shortest is null || posting.Length < shortest.Length)
            {
                shortest = posting;
            }
        }

        var candidateCount = 0;
        var digest = MatchDigest.Empty;
        foreach (var documentId in shortest!)
        {
            var isCandidate = true;
            foreach (var posting in postingArrays)
            {
                if (!ReferenceEquals(posting, shortest) && !IndexHelpers.ContainsSorted(posting, documentId))
                {
                    isCandidate = false;
                    break;
                }
            }

            if (!isCandidate)
            {
                continue;
            }

            candidateCount++;
            if (_values[documentId].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, candidateCount);
    }

    public void Dispose()
    {
    }
}

internal sealed class PackedTrigramIndex : IContainsIndex
{
    private readonly Dictionary<ulong, int[]> _postings = [];
    private string[] _values = [];

    public string Name => "Packed 48-bit trigram / sorted postings";

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _postings.Clear();
        var builders = new Dictionary<ulong, List<int>>();
        var unique = new HashSet<ulong>();

        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            unique.Clear();
            var value = values[documentId].AsSpan();
            for (var i = 0; i <= value.Length - 3; i++)
            {
                unique.Add(IndexHelpers.PackTrigram(value, i));
            }

            foreach (var gram in unique)
            {
                if (!builders.TryGetValue(gram, out var posting))
                {
                    posting = [];
                    builders.Add(gram, posting);
                }

                posting.Add(documentId);
            }
        }

        foreach (var (gram, posting) in builders)
        {
            _postings.Add(gram, posting.ToArray());
        }
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return ContainsOracle.Query(_values, needle);
        }

        var gramCount = needle.Length - 2;
        ulong[]? rentedGrams = null;
        Span<ulong> grams = gramCount <= 256
            ? stackalloc ulong[gramCount]
            : (rentedGrams = ArrayPool<ulong>.Shared.Rent(gramCount)).AsSpan(0, gramCount);

        try
        {
            for (var i = 0; i < gramCount; i++)
            {
                grams[i] = IndexHelpers.PackTrigram(needle, i);
            }

            grams.Sort();
            var distinctCount = CompactDistinct(grams);
            var postingArrays = ArrayPool<int[]>.Shared.Rent(distinctCount);
            try
            {
                var shortestIndex = 0;
                for (var i = 0; i < distinctCount; i++)
                {
                    if (!_postings.TryGetValue(grams[i], out var posting))
                    {
                        return default;
                    }

                    postingArrays[i] = posting;
                    if (posting.Length < postingArrays[shortestIndex].Length)
                    {
                        shortestIndex = i;
                    }
                }

                var shortest = postingArrays[shortestIndex];
                var candidates = ArrayPool<int>.Shared.Rent(shortest.Length);
                try
                {
                    shortest.CopyTo(candidates, 0);
                    var candidateCount = shortest.Length;
                    for (var i = 0; i < distinctCount && candidateCount > 0; i++)
                    {
                        if (i != shortestIndex)
                        {
                            candidateCount = IntersectSortedInPlace(candidates, candidateCount, postingArrays[i]);
                        }
                    }

                    var digest = MatchDigest.Empty;
                    for (var i = 0; i < candidateCount; i++)
                    {
                        var documentId = candidates[i];
                        if (_values[documentId].Contains(needle, StringComparison.Ordinal))
                        {
                            digest = digest.Add(documentId);
                        }
                    }

                    return new QueryStats(digest, candidateCount);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(candidates);
                }
            }
            finally
            {
                Array.Clear(postingArrays, 0, distinctCount);
                ArrayPool<int[]>.Shared.Return(postingArrays);
            }
        }
        finally
        {
            if (rentedGrams is not null)
            {
                ArrayPool<ulong>.Shared.Return(rentedGrams);
            }
        }
    }

    public void Dispose()
    {
    }

    internal static int CompactDistinct(Span<ulong> sorted)
    {
        if (sorted.IsEmpty)
        {
            return 0;
        }

        var count = 1;
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] != sorted[count - 1])
            {
                sorted[count++] = sorted[i];
            }
        }

        return count;
    }

    internal static int IntersectSortedInPlace(int[] candidates, int candidateCount, int[] posting)
    {
        var candidateIndex = 0;
        var postingIndex = 0;
        var writeIndex = 0;

        while (candidateIndex < candidateCount && postingIndex < posting.Length)
        {
            var candidate = candidates[candidateIndex];
            var postingValue = posting[postingIndex];
            if (candidate == postingValue)
            {
                candidates[writeIndex++] = candidate;
                candidateIndex++;
                postingIndex++;
            }
            else if (candidate < postingValue)
            {
                candidateIndex++;
            }
            else
            {
                postingIndex++;
            }
        }

        return writeIndex;
    }
}

internal sealed class AdaptiveTrigramIndex : IContainsIndex
{
    private sealed class Posting
    {
        public required int Count { get; init; }
        public int[]? Sparse { get; init; }
        public ulong[]? Dense { get; init; }

        public bool Contains(int documentId) => Dense is not null
            ? (Dense[documentId >> 6] & (1UL << (documentId & 63))) != 0
            : IndexHelpers.ContainsSorted(Sparse!, documentId);
    }

    private readonly Dictionary<ulong, Posting> _postings = [];
    private string[] _values = [];

    public string Name => "Packed trigram / adaptive sparse+bitmap";

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _postings.Clear();
        var builders = BuildPostingLists(values);
        var wordCount = (values.Length + 63) >> 6;
        var denseBytes = wordCount * sizeof(ulong);

        foreach (var (gram, ids) in builders)
        {
            if ((ids.Count * sizeof(int)) >= denseBytes)
            {
                var bits = new ulong[wordCount];
                foreach (var documentId in ids)
                {
                    bits[documentId >> 6] |= 1UL << (documentId & 63);
                }

                _postings.Add(gram, new Posting { Count = ids.Count, Dense = bits });
            }
            else
            {
                _postings.Add(gram, new Posting { Count = ids.Count, Sparse = ids.ToArray() });
            }
        }
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return ContainsOracle.Query(_values, needle);
        }

        var grams = GetDistinctGrams(needle, out var distinctCount);
        var postings = ArrayPool<Posting>.Shared.Rent(distinctCount);
        try
        {
            var shortestIndex = 0;
            for (var i = 0; i < distinctCount; i++)
            {
                if (!_postings.TryGetValue(grams[i], out var posting))
                {
                    return default;
                }

                postings[i] = posting;
                if (posting.Count < postings[shortestIndex].Count)
                {
                    shortestIndex = i;
                }
            }

            var shortest = postings[shortestIndex];
            if (shortest.Sparse is not null)
            {
                var candidates = ArrayPool<int>.Shared.Rent(shortest.Count);
                try
                {
                    shortest.Sparse.CopyTo(candidates, 0);
                    var candidateCount = shortest.Count;
                    for (var postingIndex = 0; postingIndex < distinctCount && candidateCount > 0; postingIndex++)
                    {
                        if (postingIndex == shortestIndex)
                        {
                            continue;
                        }

                        var posting = postings[postingIndex];
                        candidateCount = posting.Sparse is not null
                            ? PackedTrigramIndex.IntersectSortedInPlace(candidates, candidateCount, posting.Sparse)
                            : FilterDenseInPlace(candidates, candidateCount, posting.Dense!);
                    }

                    var digest = MatchDigest.Empty;
                    for (var i = 0; i < candidateCount; i++)
                    {
                        var documentId = candidates[i];
                        if (_values[documentId].Contains(needle, StringComparison.Ordinal))
                        {
                            digest = digest.Add(documentId);
                        }
                    }

                    return new QueryStats(digest, candidateCount);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(candidates);
                }
            }

            var candidateWords = ArrayPool<ulong>.Shared.Rent(shortest.Dense!.Length);
            try
            {
                shortest.Dense.CopyTo(candidateWords, 0);
                for (var i = 0; i < distinctCount; i++)
                {
                    if (i == shortestIndex)
                    {
                        continue;
                    }

                    var dense = postings[i].Dense!;
                    for (var wordIndex = 0; wordIndex < dense.Length; wordIndex++)
                    {
                        candidateWords[wordIndex] &= dense[wordIndex];
                    }
                }

                var candidateCount = 0;
                var digest = MatchDigest.Empty;
                for (var wordIndex = 0; wordIndex < shortest.Dense.Length; wordIndex++)
                {
                    var word = candidateWords[wordIndex];
                    candidateCount += BitOperations.PopCount(word);
                    while (word != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount(word);
                        var documentId = (wordIndex << 6) + bit;
                        if (documentId < _values.Length && _values[documentId].Contains(needle, StringComparison.Ordinal))
                        {
                            digest = digest.Add(documentId);
                        }

                        word &= word - 1;
                    }
                }

                return new QueryStats(digest, candidateCount);
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(candidateWords);
            }
        }
        finally
        {
            Array.Clear(postings, 0, distinctCount);
            ArrayPool<Posting>.Shared.Return(postings);
            ArrayPool<ulong>.Shared.Return(grams);
        }
    }

    public void Dispose()
    {
    }

    private static int FilterDenseInPlace(int[] candidates, int candidateCount, ulong[] dense)
    {
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < candidateCount; readIndex++)
        {
            var documentId = candidates[readIndex];
            if ((dense[documentId >> 6] & (1UL << (documentId & 63))) != 0)
            {
                candidates[writeIndex++] = documentId;
            }
        }

        return writeIndex;
    }

    private static Dictionary<ulong, List<int>> BuildPostingLists(string[] values)
    {
        var builders = new Dictionary<ulong, List<int>>();
        var unique = new HashSet<ulong>();
        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            unique.Clear();
            var value = values[documentId].AsSpan();
            for (var i = 0; i <= value.Length - 3; i++)
            {
                unique.Add(IndexHelpers.PackTrigram(value, i));
            }

            foreach (var gram in unique)
            {
                if (!builders.TryGetValue(gram, out var posting))
                {
                    posting = [];
                    builders.Add(gram, posting);
                }

                posting.Add(documentId);
            }
        }

        return builders;
    }

    internal static ulong[] GetDistinctGrams(string needle, out int distinctCount)
    {
        var gramCount = needle.Length - 2;
        var grams = ArrayPool<ulong>.Shared.Rent(gramCount);
        var gramSpan = grams.AsSpan(0, gramCount);

        for (var i = 0; i < gramCount; i++)
        {
            gramSpan[i] = IndexHelpers.PackTrigram(needle, i);
        }

        gramSpan.Sort();
        distinctCount = PackedTrigramIndex.CompactDistinct(gramSpan);
        return grams;
    }
}

internal sealed class RoaringAdaptiveTrigramIndex : IContainsIndex
{
    private sealed class Posting : IDisposable
    {
        public required int Count { get; init; }
        public int[]? Sparse { get; init; }
        public Roaring32Bitmap? Bitmap { get; init; }

        public bool Contains(int documentId) => Bitmap is not null
            ? Bitmap.Contains(unchecked((uint)documentId))
            : IndexHelpers.ContainsSorted(Sparse!, documentId);

        public void Dispose() => Bitmap?.Dispose();
    }

    private readonly Dictionary<ulong, Posting> _postings = [];
    private string[] _values = [];

    public string Name => "Packed trigram / adaptive Roaring";

    public void Build(string[] values)
    {
        DisposePostings();
        _values = (string[])values.Clone();
        var builders = new Dictionary<ulong, List<int>>();
        var unique = new HashSet<ulong>();

        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            unique.Clear();
            var value = values[documentId].AsSpan();
            for (var i = 0; i <= value.Length - 3; i++)
            {
                unique.Add(IndexHelpers.PackTrigram(value, i));
            }

            foreach (var gram in unique)
            {
                if (!builders.TryGetValue(gram, out var posting))
                {
                    posting = [];
                    builders.Add(gram, posting);
                }

                posting.Add(documentId);
            }
        }

        var roaringThreshold = Math.Max(256, values.Length / 128);
        foreach (var (gram, ids) in builders)
        {
            if (ids.Count >= roaringThreshold)
            {
                var bitmap = new Roaring32Bitmap();
                bitmap.AddMany(ids.Select(static id => unchecked((uint)id)).ToArray());
                bitmap.Optimize();
                _postings.Add(gram, new Posting { Count = ids.Count, Bitmap = bitmap });
            }
            else
            {
                _postings.Add(gram, new Posting { Count = ids.Count, Sparse = ids.ToArray() });
            }
        }
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return ContainsOracle.Query(_values, needle);
        }

        var grams = AdaptiveTrigramIndex.GetDistinctGrams(needle, out var distinctCount);
        var postings = ArrayPool<Posting>.Shared.Rent(distinctCount);
        try
        {
            var shortestIndex = 0;
            for (var i = 0; i < distinctCount; i++)
            {
                if (!_postings.TryGetValue(grams[i], out var posting))
                {
                    return default;
                }

                postings[i] = posting;
                if (posting.Count < postings[shortestIndex].Count)
                {
                    shortestIndex = i;
                }
            }

            var shortest = postings[shortestIndex];
            if (shortest.Bitmap is not null && AllBitmaps(postings, distinctCount))
            {
                using var intersection = shortest.Bitmap.Clone();
                for (var i = 0; i < distinctCount; i++)
                {
                    if (i != shortestIndex)
                    {
                        intersection.IAnd(postings[i].Bitmap!);
                    }
                }

                var digest = MatchDigest.Empty;
                foreach (var rawDocumentId in intersection.Values)
                {
                    var documentId = unchecked((int)rawDocumentId);
                    if (_values[documentId].Contains(needle, StringComparison.Ordinal))
                    {
                        digest = digest.Add(documentId);
                    }
                }

                return new QueryStats(digest, checked((int)intersection.Count));
            }

            var candidateCount = 0;
            var sparseDigest = MatchDigest.Empty;
            foreach (var documentId in shortest.Sparse!)
            {
                var candidate = true;
                for (var i = 0; i < distinctCount; i++)
                {
                    if (i != shortestIndex && !postings[i].Contains(documentId))
                    {
                        candidate = false;
                        break;
                    }
                }

                if (!candidate)
                {
                    continue;
                }

                candidateCount++;
                if (_values[documentId].Contains(needle, StringComparison.Ordinal))
                {
                    sparseDigest = sparseDigest.Add(documentId);
                }
            }

            return new QueryStats(sparseDigest, candidateCount);
        }
        finally
        {
            Array.Clear(postings, 0, distinctCount);
            ArrayPool<Posting>.Shared.Return(postings);
            ArrayPool<ulong>.Shared.Return(grams);
        }
    }

    public void Dispose()
    {
        DisposePostings();
        _values = [];
    }

    private static bool AllBitmaps(Posting[] postings, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (postings[i].Bitmap is null)
            {
                return false;
            }
        }

        return true;
    }

    private void DisposePostings()
    {
        foreach (var posting in _postings.Values)
        {
            posting.Dispose();
        }

        _postings.Clear();
    }
}
