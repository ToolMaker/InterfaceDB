using BelNytheraSeiche.WaveletMatrix;
using NLibSais;

namespace ContainsIndexBenchmarks;

internal sealed record JoinedCorpus(
    string Text,
    char Separator,
    int[] Starts,
    int[] Lengths,
    int[] Owners);

internal static class JoinedCorpusBuilder
{
    public static JoinedCorpus Create(string[] values)
    {
        var separator = FindSeparator(values);
        var totalLength = values.Sum(static value => value.Length) + Math.Max(0, values.Length - 1);
        var starts = new int[values.Length];
        var lengths = new int[values.Length];
        var owners = new int[totalLength];
        Array.Fill(owners, -1);

        var characters = new char[totalLength];
        var cursor = 0;
        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            var value = values[documentId];
            starts[documentId] = cursor;
            lengths[documentId] = value.Length;
            value.AsSpan().CopyTo(characters.AsSpan(cursor));
            Array.Fill(owners, documentId, cursor, value.Length);
            cursor += value.Length;
            if (documentId + 1 < values.Length)
            {
                characters[cursor++] = separator;
            }
        }

        return new JoinedCorpus(new string(characters), separator, starts, lengths, owners);
    }

    private static char FindSeparator(string[] values)
    {
        var used = new bool[char.MaxValue + 1];
        foreach (var value in values)
        {
            foreach (var character in value)
            {
                used[character] = true;
            }
        }

        // Avoid NUL because the FM-index reserves it as its internal terminator.
        for (var candidate = 1; candidate <= char.MaxValue; candidate++)
        {
            if (!used[candidate])
            {
                return (char)candidate;
            }
        }

        throw new InvalidOperationException("The corpus uses every UTF-16 code unit, so no separator is available.");
    }
}

internal sealed class NativeSuffixArrayIndex(int documentIdBase = 0) : IContainsIndex
{
    private readonly int _documentIdBase = documentIdBase;
    private string[] _values = [];
    private JoinedCorpus? _corpus;
    private int[] _suffixes = [];
    private int[] _seen = [];
    private int _generation;

    public string Name => "NLibSais native suffix array";

    public bool SupportsConcurrentQueries => false;

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _corpus = JoinedCorpusBuilder.Create(values);
        _suffixes = new int[_corpus.Text.Length];
        LibSais.ConstructSuffixArray(_corpus.Text.AsSpan(), _suffixes, Span<int>.Empty);
        _seen = new int[values.Length];
        _generation = 0;
    }

    public QueryStats Query(string needle) => QueryFiltered(needle, null);

    internal QueryStats QueryFiltered(string needle, Func<int, bool>? include)
    {
        if (needle.Length == 0)
        {
            var all = MatchDigest.Empty;
            var candidateCount = 0;
            for (var localId = 0; localId < _values.Length; localId++)
            {
                var documentId = localId + _documentIdBase;
                if (include is null || include(documentId))
                {
                    candidateCount++;
                    all = all.Add(documentId);
                }
            }

            return new QueryStats(all, candidateCount);
        }

        var lower = Bound(needle, upper: false);
        var upper = Bound(needle, upper: true);
        BeginGeneration();

        var candidates = 0;
        var digest = MatchDigest.Empty;
        var corpus = _corpus!;
        for (var rank = lower; rank < upper; rank++)
        {
            var position = _suffixes[rank];
            var localId = corpus.Owners[position];
            if (localId < 0 || _seen[localId] == _generation)
            {
                continue;
            }

            var relativePosition = position - corpus.Starts[localId];
            if (relativePosition < 0 || relativePosition + needle.Length > corpus.Lengths[localId])
            {
                continue;
            }

            _seen[localId] = _generation;
            var documentId = localId + _documentIdBase;
            if (include is not null && !include(documentId))
            {
                continue;
            }

            candidates++;
            if (_values[localId].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, candidates);
    }

    public void Dispose()
    {
    }

    private int Bound(string needle, bool upper)
    {
        var low = 0;
        var high = _suffixes.Length;
        while (low < high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            var comparison = CompareSuffix(_suffixes[middle], needle);
            if (comparison < 0 || (upper && comparison == 0))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int CompareSuffix(int suffixStart, string needle)
    {
        var text = _corpus!.Text.AsSpan(suffixStart);
        var pattern = needle.AsSpan();
        var length = Math.Min(text.Length, pattern.Length);
        for (var i = 0; i < length; i++)
        {
            var difference = text[i] - pattern[i];
            if (difference != 0)
            {
                return difference;
            }
        }

        return text.Length < pattern.Length ? -1 : 0;
    }

    private void BeginGeneration()
    {
        if (_generation == int.MaxValue)
        {
            Array.Clear(_seen);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }
}

internal sealed class CaseOnSuffixArrayIndex : IContainsIndex
{
    private string[] _values = [];
    private JoinedCorpus? _corpus;
    private CaseON.SuffixArray? _index;
    private int[] _seen = [];
    private int _generation;

    public string Name => "CaseON managed suffix array";

    public bool SupportsConcurrentQueries => false;

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _corpus = JoinedCorpusBuilder.Create(values);
        _index = new CaseON.SuffixArray(_corpus.Text);
        _seen = new int[values.Length];
        _generation = 0;
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length == 0)
        {
            return QueryStats.All(_values.Length);
        }

        BeginGeneration();
        var candidates = 0;
        var digest = MatchDigest.Empty;
        foreach (var position in _index!.Occurrences(needle))
        {
            var documentId = _corpus!.Owners[position];
            if (documentId < 0 || _seen[documentId] == _generation)
            {
                continue;
            }

            var relativePosition = position - _corpus.Starts[documentId];
            if (relativePosition < 0 || relativePosition + needle.Length > _corpus.Lengths[documentId])
            {
                continue;
            }

            _seen[documentId] = _generation;
            candidates++;
            if (_values[documentId].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, candidates);
    }

    public void Dispose()
    {
    }

    private void BeginGeneration()
    {
        if (_generation == int.MaxValue)
        {
            Array.Clear(_seen);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }
}

internal sealed class FmContainsIndex(int sampleRate) : IContainsIndex
{
    private readonly int _sampleRate = sampleRate;
    private string[] _values = [];
    private JoinedCorpus? _corpus;
    private FMIndex? _index;
    private int[] _seen = [];
    private int _generation;

    public string Name => _sampleRate == 0
        ? "WaveletMatrix FM-index (full SA)"
        : $"WaveletMatrix FM-index (sample {_sampleRate})";

    public bool SupportsConcurrentQueries => false;

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        _corpus = JoinedCorpusBuilder.Create(values);
        _index = FMIndex.Create(_corpus.Text, _sampleRate);
        _seen = new int[values.Length];
        _generation = 0;
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length == 0)
        {
            return QueryStats.All(_values.Length);
        }

        BeginGeneration();
        var candidates = 0;
        var digest = MatchDigest.Empty;
        foreach (var position in _index!.Locate(needle.AsSpan(), SortOrder.Unordered))
        {
            var documentId = _corpus!.Owners[position];
            if (documentId < 0 || _seen[documentId] == _generation)
            {
                continue;
            }

            var relativePosition = position - _corpus.Starts[documentId];
            if (relativePosition < 0 || relativePosition + needle.Length > _corpus.Lengths[documentId])
            {
                continue;
            }

            _seen[documentId] = _generation;
            candidates++;
            if (_values[documentId].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, candidates);
    }

    public void Dispose()
    {
    }

    private void BeginGeneration()
    {
        if (_generation == int.MaxValue)
        {
            Array.Clear(_seen);
            _generation = 1;
        }
        else
        {
            _generation++;
        }
    }
}

internal sealed class SegmentedSuffixArrayIndex(int segmentSize = 4096) : IContainsIndex
{
    private readonly int _segmentSize = segmentSize;
    private readonly List<NativeSuffixArrayIndex> _segments = [];
    private readonly Dictionary<int, string> _overrides = [];
    private readonly HashSet<int> _removed = [];
    private string[] _values = [];

    public string Name => $"Segmented NLibSais SA + delta ({_segmentSize})";

    public bool SupportsIncrementalMutation => true;

    public bool SupportsConcurrentQueries => false;

    public void Build(string[] values)
    {
        DisposeSegments();
        _values = (string[])values.Clone();
        _overrides.Clear();
        _removed.Clear();

        for (var start = 0; start < values.Length; start += _segmentSize)
        {
            var length = Math.Min(_segmentSize, values.Length - start);
            var segmentValues = new string[length];
            Array.Copy(values, start, segmentValues, 0, length);
            var segment = new NativeSuffixArrayIndex(start);
            segment.Build(segmentValues);
            _segments.Add(segment);
        }
    }

    public QueryStats Query(string needle)
    {
        var digest = MatchDigest.Empty;
        var candidateCount = 0;
        foreach (var segment in _segments)
        {
            var result = segment.QueryFiltered(needle, IsInBase);
            digest = digest.Combine(result.Matches);
            candidateCount += result.CandidateCount;
        }

        foreach (var (documentId, value) in _overrides)
        {
            candidateCount++;
            if (value.Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(documentId);
            }
        }

        return new QueryStats(digest, candidateCount);
    }

    public void Upsert(int documentId, string value)
    {
        _removed.Remove(documentId);
        _overrides[documentId] = value;
        _values[documentId] = value;
    }

    public void Remove(int documentId)
    {
        _overrides.Remove(documentId);
        _removed.Add(documentId);
        _values[documentId] = string.Empty;
    }

    public void Compact()
    {
        Build(_values);
    }

    public void Dispose()
    {
        DisposeSegments();
    }

    private bool IsInBase(int documentId) => !_removed.Contains(documentId) && !_overrides.ContainsKey(documentId);

    private void DisposeSegments()
    {
        foreach (var segment in _segments)
        {
            segment.Dispose();
        }

        _segments.Clear();
    }
}
