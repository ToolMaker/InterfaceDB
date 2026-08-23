namespace ContainsIndexBenchmarks;

internal sealed class ObjectScanIndex : IContainsIndex
{
    private string[] _values = [];

    public string Name => ".NET string.Contains scan";

    public bool SupportsIncrementalMutation => true;

    public void Build(string[] values) => _values = (string[])values.Clone();

    public QueryStats Query(string needle)
    {
        var digest = MatchDigest.Empty;
        for (var i = 0; i < _values.Length; i++)
        {
            if (_values[i].Contains(needle, StringComparison.Ordinal))
            {
                digest = digest.Add(i);
            }
        }

        return new QueryStats(digest, _values.Length);
    }

    public void Upsert(int documentId, string value) => _values[documentId] = value;

    public void Remove(int documentId) => _values[documentId] = string.Empty;

    public void Dispose()
    {
    }
}

internal sealed class PackedArenaScanIndex : IContainsIndex
{
    private char[] _characters = [];
    private int[] _starts = [];
    private int[] _lengths = [];

    public string Name => "Packed UTF-16 arena scan";

    public void Build(string[] values)
    {
        _starts = new int[values.Length];
        _lengths = new int[values.Length];
        var characterCount = values.Sum(static value => value.Length);
        _characters = new char[characterCount];

        var cursor = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            _starts[i] = cursor;
            _lengths[i] = value.Length;
            value.AsSpan().CopyTo(_characters.AsSpan(cursor));
            cursor += value.Length;
        }
    }

    public QueryStats Query(string needle)
    {
        var digest = MatchDigest.Empty;
        var needleSpan = needle.AsSpan();
        for (var i = 0; i < _starts.Length; i++)
        {
            var value = _characters.AsSpan(_starts[i], _lengths[i]);
            if (value.IndexOf(needleSpan, StringComparison.Ordinal) >= 0)
            {
                digest = digest.Add(i);
            }
        }

        return new QueryStats(digest, _starts.Length);
    }

    public void Dispose()
    {
    }
}
