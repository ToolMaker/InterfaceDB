using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace ContainsIndexBenchmarks;

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 1, printSource: true)]
[ShortRunJob]
public class FocusedQueryBenchmarks
{
    private IContainsIndex _scan = null!;
    private IContainsIndex _arena = null!;
    private IContainsIndex _packed = null!;
    private IContainsIndex _adaptive = null!;
    private IContainsIndex _roaring = null!;
    private IContainsIndex _signature256 = null!;
    private IContainsIndex _signature1024 = null!;
    private IContainsIndex _suffix = null!;
    private string _needle = null!;

    [Params("rare-8", "natural-broad")]
    public string QueryShape { get; set; } = null!;

    [GlobalSetup]
    public void Setup()
    {
        var dataset = CorpusFactory.Create(CorpusKind.Random, 100_000, 64);
        _needle = dataset.Queries.Single(query => query.Name == QueryShape).Needle;
        _scan = Build(new ObjectScanIndex(), dataset.Values);
        _arena = Build(new PackedArenaScanIndex(), dataset.Values);
        _packed = Build(new PackedTrigramIndex(), dataset.Values);
        _adaptive = Build(new AdaptiveTrigramIndex(), dataset.Values);
        _roaring = Build(new RoaringAdaptiveTrigramIndex(), dataset.Values);
        _signature256 = Build(new BitSlicedSignatureIndex(256), dataset.Values);
        _signature1024 = Build(new BitSlicedSignatureIndex(1024), dataset.Values);
        _suffix = Build(new NativeSuffixArrayIndex(), dataset.Values);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scan.Dispose();
        _arena.Dispose();
        _packed.Dispose();
        _adaptive.Dispose();
        _roaring.Dispose();
        _signature256.Dispose();
        _signature1024.Dispose();
        _suffix.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long DotNetScan() => Digest(_scan.Query(_needle));

    [Benchmark]
    public long PackedArenaScan() => Digest(_arena.Query(_needle));

    [Benchmark]
    public long PackedTrigram() => Digest(_packed.Query(_needle));

    [Benchmark]
    public long AdaptiveTrigram() => Digest(_adaptive.Query(_needle));

    [Benchmark]
    public long RoaringTrigram() => Digest(_roaring.Query(_needle));

    [Benchmark]
    public long Signature256() => Digest(_signature256.Query(_needle));

    [Benchmark]
    public long Signature1024() => Digest(_signature1024.Query(_needle));

    [Benchmark]
    public long NativeSuffixArray() => Digest(_suffix.Query(_needle));

    private static T Build<T>(T index, string[] values)
        where T : IContainsIndex
    {
        index.Build(values);
        return index;
    }

    private static long Digest(QueryStats result) =>
        unchecked((long)(result.Matches.Sum ^ result.Matches.Xor) + result.CandidateCount);
}

[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 2, printSource: true)]
[ShortRunJob]
public class KeyEncodingBenchmarks
{
    private readonly string _value = "the_quick_brown_fox_jumps_over_the_lazy_dog_0123456789_interface_db";

    [Benchmark(Baseline = true)]
    public int AllocatingStringTrigrams()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= _value.Length - 3; i++)
        {
            set.Add(_value.Substring(i, 3));
        }

        return set.Count;
    }

    [Benchmark]
    public ulong Packed48BitTrigrams()
    {
        ulong digest = 0;
        var value = _value.AsSpan();
        for (var i = 0; i <= value.Length - 3; i++)
        {
            digest ^= IndexHelpers.PackTrigram(value, i);
        }

        return digest;
    }
}
