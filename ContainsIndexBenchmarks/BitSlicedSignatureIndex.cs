using System.Buffers;
using System.Numerics;

namespace ContainsIndexBenchmarks;

internal sealed class BitSlicedSignatureIndex(int signatureBits, int hashesPerGram = 3) : IContainsIndex
{
    private readonly int _signatureBits = ValidateSignatureBits(signatureBits);
    private readonly int _hashesPerGram = Math.Clamp(hashesPerGram, 1, 4);
    private string[] _values = [];
    private ulong[][] _planes = [];
    private ulong[] _active = [];

    public string Name => $"Bit-sliced signature {_signatureBits}-bit/{_hashesPerGram}h";

    public bool SupportsIncrementalMutation => true;

    public void Build(string[] values)
    {
        _values = (string[])values.Clone();
        var wordCount = (values.Length + 63) >> 6;
        _planes = new ulong[_signatureBits][];
        for (var i = 0; i < _planes.Length; i++)
        {
            _planes[i] = new ulong[wordCount];
        }

        _active = new ulong[wordCount];
        for (var documentId = 0; documentId < values.Length; documentId++)
        {
            SetActive(documentId, true);
            AddSignature(documentId, values[documentId]);
        }
    }

    public QueryStats Query(string needle)
    {
        if (needle.Length < 3)
        {
            return Scan(needle);
        }

        var candidates = ArrayPool<ulong>.Shared.Rent(_active.Length);
        try
        {
            _active.CopyTo(candidates, 0);
            var grams = AdaptiveTrigramIndex.GetDistinctGrams(needle, out var gramCount);
            try
            {
                for (var gramIndex = 0; gramIndex < gramCount; gramIndex++)
                {
                    var hash = Mix(grams[gramIndex]);
                    for (var hashIndex = 0; hashIndex < _hashesPerGram; hashIndex++)
                    {
                        var plane = _planes[GetPlane(hash, hashIndex)];
                        for (var wordIndex = 0; wordIndex < _active.Length; wordIndex++)
                        {
                            candidates[wordIndex] &= plane[wordIndex];
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<ulong>.Shared.Return(grams);
            }

            var candidateCount = 0;
            var digest = MatchDigest.Empty;
            for (var wordIndex = 0; wordIndex < _active.Length; wordIndex++)
            {
                var word = candidates[wordIndex];
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
            ArrayPool<ulong>.Shared.Return(candidates);
        }
    }

    public void Upsert(int documentId, string value)
    {
        ClearSignature(documentId);
        _values[documentId] = value;
        SetActive(documentId, true);
        AddSignature(documentId, value);
    }

    public void Remove(int documentId)
    {
        ClearSignature(documentId);
        SetActive(documentId, false);
        _values[documentId] = string.Empty;
    }

    public void Dispose()
    {
    }

    private QueryStats Scan(string needle)
    {
        var candidateCount = 0;
        var digest = MatchDigest.Empty;
        for (var documentId = 0; documentId < _values.Length; documentId++)
        {
            if (!IsActive(documentId))
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

    private void AddSignature(int documentId, string value)
    {
        if (value.Length < 3)
        {
            return;
        }

        var word = documentId >> 6;
        var bit = 1UL << (documentId & 63);
        var unique = new HashSet<ulong>();
        for (var i = 0; i <= value.Length - 3; i++)
        {
            unique.Add(IndexHelpers.PackTrigram(value, i));
        }

        foreach (var gram in unique)
        {
            var hash = Mix(gram);
            for (var hashIndex = 0; hashIndex < _hashesPerGram; hashIndex++)
            {
                _planes[GetPlane(hash, hashIndex)][word] |= bit;
            }
        }
    }

    private void ClearSignature(int documentId)
    {
        var word = documentId >> 6;
        var mask = ~(1UL << (documentId & 63));
        for (var plane = 0; plane < _planes.Length; plane++)
        {
            _planes[plane][word] &= mask;
        }
    }

    private void SetActive(int documentId, bool active)
    {
        var word = documentId >> 6;
        var bit = 1UL << (documentId & 63);
        if (active)
        {
            _active[word] |= bit;
        }
        else
        {
            _active[word] &= ~bit;
        }
    }

    private bool IsActive(int documentId) =>
        (_active[documentId >> 6] & (1UL << (documentId & 63))) != 0;

    private int GetPlane(ulong hash, int hashIndex)
    {
        var shifted = BitOperations.RotateRight(hash, hashIndex * 17);
        return (int)(shifted & unchecked((uint)(_signatureBits - 1)));
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static int ValidateSignatureBits(int value)
    {
        if (value < 64 || !BitOperations.IsPow2(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Signature size must be a power of two and at least 64 bits.");
        }

        return value;
    }
}
