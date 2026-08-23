using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DeltaQ.SuffixSorting.SAIS;
using Ganss.Text;
using NLibSais;

namespace ContainsIndexBenchmarks;

internal sealed record IndexDescriptor(
    string Id,
    Func<IContainsIndex> Create,
    long MaximumCharacters = long.MaxValue,
    int MaximumDocuments = int.MaxValue);

internal sealed record BenchmarkRow(
    string Dataset,
    int Documents,
    long Characters,
    string Index,
    string Query,
    int NeedleLength,
    int Matches,
    int Candidates,
    double SelectivityPercent,
    double FalsePositivePercent,
    double BuildMilliseconds,
    double ManagedMiB,
    double PrivateMiB,
    double ColdMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double AllocatedBytesPerQuery,
    string Status);

internal sealed record MutationRow(
    string Dataset,
    string Index,
    string Operation,
    int ItemCount,
    double TotalMilliseconds,
    double MicrosecondsPerItem,
    double PostMutationQueryMicroseconds,
    string Status);

internal sealed record ConcurrencyRow(
    string Dataset,
    string Index,
    int Workers,
    int QueryCount,
    double SingleThreadQueriesPerSecond,
    double ParallelQueriesPerSecond,
    double Scaling,
    string Status);

internal sealed record ConstructionRow(
    string Dataset,
    string Builder,
    int InputUnits,
    double Milliseconds,
    double ManagedMiB,
    string Status);

internal sealed record BatchRow(
    string Dataset,
    string Method,
    int Patterns,
    long DocumentPatternMatches,
    double BuildMilliseconds,
    double SearchMilliseconds,
    string Status);

internal sealed class BenchmarkReport
{
    public List<BenchmarkRow> Queries { get; } = [];
    public List<MutationRow> Mutations { get; } = [];
    public List<ConcurrencyRow> Concurrency { get; } = [];
    public List<ConstructionRow> Construction { get; } = [];
    public List<BatchRow> Batch { get; } = [];
    public List<string> Notes { get; } = [];
}

internal static class IndexCatalog
{
    public static readonly IndexDescriptor ObjectScan = new("scan", static () => new ObjectScanIndex());
    public static readonly IndexDescriptor ArenaScan = new("arena", static () => new PackedArenaScanIndex());
    public static readonly IndexDescriptor NaiveTrigram = new(
        "naive-gram",
        static () => new NaiveStringTrigramIndex(),
        MaximumCharacters: 3_000_000);
    public static readonly IndexDescriptor PackedTrigram = new("packed-gram", static () => new PackedTrigramIndex());
    public static readonly IndexDescriptor AdaptiveTrigram = new("adaptive-gram", static () => new AdaptiveTrigramIndex());
    public static readonly IndexDescriptor RoaringTrigram = new(
        "roaring-gram",
        static () => new RoaringAdaptiveTrigramIndex(),
        MaximumCharacters: 20_000_000);
    public static readonly IndexDescriptor Signature256 = new("signature-256", static () => new BitSlicedSignatureIndex(256));
    public static readonly IndexDescriptor Signature1024 = new("signature-1024", static () => new BitSlicedSignatureIndex(1024));
    public static readonly IndexDescriptor NativeSuffix = new(
        "native-sa",
        static () => new NativeSuffixArrayIndex(),
        MaximumCharacters: 12_000_000);
    public static readonly IndexDescriptor SegmentedSuffix = new(
        "segmented-sa",
        static () => new SegmentedSuffixArrayIndex(),
        MaximumCharacters: 12_000_000);
    public static readonly IndexDescriptor CaseOnSuffix = new(
        "caseon-sa",
        static () => new CaseOnSuffixArrayIndex(),
        MaximumCharacters: 1_500_000);
    public static readonly IndexDescriptor FmFull = new(
        "fm-full",
        static () => new FmContainsIndex(0),
        MaximumCharacters: 3_000_000);
    public static readonly IndexDescriptor FmSampled = new(
        "fm-sampled",
        static () => new FmContainsIndex(32),
        MaximumCharacters: 3_000_000);
    public static readonly IndexDescriptor Lifti = new(
        "lifti",
        static () => new LiftiWildcardIndex(),
        MaximumCharacters: 12_000_000);
    public static readonly IndexDescriptor Lucene = new(
        "lucene",
        static () => new LuceneNGramIndex(),
        MaximumCharacters: 20_000_000);

    public static IReadOnlyList<IndexDescriptor> All { get; } =
    [
        ObjectScan,
        ArenaScan,
        NaiveTrigram,
        PackedTrigram,
        AdaptiveTrigram,
        RoaringTrigram,
        Signature256,
        Signature1024,
        NativeSuffix,
        SegmentedSuffix,
        CaseOnSuffix,
        FmFull,
        FmSampled,
        Lifti,
        Lucene
    ];

    public static IReadOnlyList<IndexDescriptor> Core { get; } =
    [
        ObjectScan,
        ArenaScan,
        NaiveTrigram,
        PackedTrigram,
        AdaptiveTrigram,
        RoaringTrigram,
        Signature256,
        Signature1024
    ];

    public static IReadOnlyList<IndexDescriptor> Scale { get; } =
    [
        ObjectScan,
        ArenaScan,
        PackedTrigram,
        AdaptiveTrigram,
        RoaringTrigram,
        Signature256,
        Signature1024,
        NativeSuffix,
        SegmentedSuffix,
        Lifti,
        Lucene
    ];

    public static IReadOnlyList<IndexDescriptor> Million { get; } =
    [
        ObjectScan,
        ArenaScan,
        PackedTrigram,
        AdaptiveTrigram,
        Signature256,
        Signature1024
    ];
}

internal static class ValidationSuite
{
    public static int Run(TextWriter output)
    {
        var dataset = CorpusFactory.CreateValidation();
        var failures = 0;
        output.WriteLine($"Validating {IndexCatalog.All.Count} implementations over {dataset.Values.Length:N0} mixed records...");

        foreach (var descriptor in IndexCatalog.All)
        {
            using var index = descriptor.Create();
            try
            {
                index.Build(dataset.Values);
                foreach (var query in dataset.Queries)
                {
                    var expected = ContainsOracle.Query(dataset.Values, query.Needle);
                    var actual = index.Query(query.Needle);
                    if (actual.Matches != expected.Matches || actual.CandidateCount < actual.Matches.Count)
                    {
                        failures++;
                        output.WriteLine($"  FAIL {index.Name}: {query.Name}; expected {expected.Matches}, actual {actual}");
                        goto NextIndex;
                    }
                }

                if (index.SupportsIncrementalMutation)
                {
                    var changed = (string[])dataset.Values.Clone();
                    changed[13] = "mutation-target-\uD83D\uDE80-QzUnique";
                    index.Upsert(13, changed[13]);
                    foreach (var needle in new[] { "mutation-target", "\uD83D\uDE80-Qz", "QzUnique" })
                    {
                        var expected = ContainsOracle.Query(changed, needle);
                        var actual = index.Query(needle);
                        if (actual.Matches != expected.Matches)
                        {
                            failures++;
                            output.WriteLine($"  FAIL {index.Name}: mutation query {needle}; expected {expected.Matches}, actual {actual}");
                            goto NextIndex;
                        }
                    }
                }

                output.WriteLine($"  PASS {index.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                output.WriteLine($"  FAIL {index.Name}: {exception.GetType().Name}: {exception.Message}");
            }

        NextIndex:
            ;
        }

        output.WriteLine(failures == 0
            ? "All implementations matched the ordinal oracle."
            : $"Validation completed with {failures} failure(s).");
        return failures;
    }
}

internal static class BenchmarkSuite
{
    private const string MutationMarker = "__MUTATION_NEEDLE_7xQ9__";
    private static readonly byte[] CacheBuffer = new byte[64 * 1024 * 1024];
    [ThreadStatic]
    private static long _sink;

    public static BenchmarkReport RunMatrix(TextWriter output, bool includeMillion = true)
    {
        var report = new BenchmarkReport();
        output.WriteLine(SystemDescription());

        RunDataset(
            CorpusFactory.Create(CorpusKind.Random, 10_000, 64),
            IndexCatalog.All,
            report,
            output,
            includeMutations: false,
            includeConcurrency: false);

        foreach (var kind in new[]
                 {
                     CorpusKind.NamesAndEmails,
                     CorpusKind.Urls,
                     CorpusKind.Logs,
                     CorpusKind.Repetitive,
                     CorpusKind.Unicode
                 })
        {
            var length = kind switch
            {
                CorpusKind.Logs => 256,
                CorpusKind.Urls => 128,
                CorpusKind.Repetitive => 128,
                _ => 72
            };
            RunDataset(
                CorpusFactory.Create(kind, 10_000, length),
                IndexCatalog.Core,
                report,
                output,
                includeMutations: false,
                includeConcurrency: false);
        }

        var scaleDataset = CorpusFactory.Create(CorpusKind.Random, 100_000, 64);
        RunDataset(
            scaleDataset,
            IndexCatalog.Scale,
            report,
            output,
            includeMutations: true,
            includeConcurrency: true);

        RunDataset(
            CorpusFactory.Create(CorpusKind.Repetitive, 100_000, 128),
            IndexCatalog.Core,
            report,
            output,
            includeMutations: false,
            includeConcurrency: false);

        RunDataset(
            CorpusFactory.Create(CorpusKind.Random, 10_000, 2_048),
            IndexCatalog.Core,
            report,
            output,
            includeMutations: false,
            includeConcurrency: false);

        if (includeMillion)
        {
            RunDataset(
                CorpusFactory.Create(CorpusKind.Random, 1_000_000, 64),
                IndexCatalog.Million,
                report,
                output,
                includeMutations: false,
                includeConcurrency: false);
        }

        RunConstructionBackends(CorpusFactory.Create(CorpusKind.Random, 20_000, 64), report, output);
        RunAhoCorasickBatch(scaleDataset, report, output);
        return report;
    }

    public static void WriteReport(BenchmarkReport report)
    {
        var resultsDirectory = Path.Combine(ProjectDirectory(), "results");
        Directory.CreateDirectory(resultsDirectory);
        WriteQueriesCsv(Path.Combine(resultsDirectory, "contains-query-results.csv"), report.Queries);
        WriteMutationsCsv(Path.Combine(resultsDirectory, "contains-mutation-results.csv"), report.Mutations);
        WriteConcurrencyCsv(Path.Combine(resultsDirectory, "contains-concurrency-results.csv"), report.Concurrency);
        WriteConstructionCsv(Path.Combine(resultsDirectory, "suffix-construction-results.csv"), report.Construction);
        WriteBatchCsv(Path.Combine(resultsDirectory, "multi-pattern-results.csv"), report.Batch);
        File.WriteAllText(
            Path.Combine(resultsDirectory, "RESULTS.md"),
            ReportWriter.ToMarkdown(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void RunDataset(
        CorpusDataset dataset,
        IReadOnlyList<IndexDescriptor> descriptors,
        BenchmarkReport report,
        TextWriter output,
        bool includeMutations,
        bool includeConcurrency)
    {
        output.WriteLine();
        output.WriteLine($"Dataset {dataset.Name}: {dataset.Values.Length:N0} documents, {dataset.CharacterCount:N0} UTF-16 characters");
        var expectedByQuery = dataset.Queries.ToDictionary(
            static query => query.Name,
            query => ContainsOracle.Query(dataset.Values, query.Needle));

        foreach (var descriptor in descriptors)
        {
            if (dataset.CharacterCount > descriptor.MaximumCharacters || dataset.Values.Length > descriptor.MaximumDocuments)
            {
                output.WriteLine($"  SKIP {descriptor.Id} (configured scale guard)");
                continue;
            }

            ForceGc();
            var process = Process.GetCurrentProcess();
            var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
            process.Refresh();
            var privateBefore = process.PrivateMemorySize64;
            IContainsIndex? index = null;
            try
            {
                index = descriptor.Create();
                var buildWatch = Stopwatch.StartNew();
                index.Build(dataset.Values);
                buildWatch.Stop();

                ForceGc();
                var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
                process.Refresh();
                var privateAfter = process.PrivateMemorySize64;
                var managedMiB = Math.Max(0, managedAfter - managedBefore) / 1048576d;
                var privateMiB = Math.Max(0, privateAfter - privateBefore) / 1048576d;
                output.WriteLine($"  {index.Name}: build {buildWatch.Elapsed.TotalMilliseconds:N1} ms, managed Δ {managedMiB:N1} MiB, private Δ {privateMiB:N1} MiB");

                var valid = true;
                foreach (var query in dataset.Queries)
                {
                    var actual = index.Query(query.Needle);
                    var expected = expectedByQuery[query.Name];
                    if (actual.Matches != expected.Matches)
                    {
                        valid = false;
                        var message = $"Incorrect: expected {expected.Matches}, actual {actual.Matches}";
                        output.WriteLine($"    FAIL {query.Name}: {message}");
                        report.Queries.Add(new BenchmarkRow(
                            dataset.Name,
                            dataset.Values.Length,
                            dataset.CharacterCount,
                            index.Name,
                            query.Name,
                            query.Needle.Length,
                            expected.Matches.Count,
                            actual.CandidateCount,
                            Percent(expected.Matches.Count, dataset.Values.Length),
                            FalsePositivePercent(actual),
                            buildWatch.Elapsed.TotalMilliseconds,
                            managedMiB,
                            privateMiB,
                            0,
                            0,
                            0,
                            0,
                            message));
                        break;
                    }
                }

                if (!valid)
                {
                    continue;
                }

                foreach (var query in dataset.Queries)
                {
                    var measurement = MeasureQuery(index, query.Needle);
                    var expected = expectedByQuery[query.Name];
                    report.Queries.Add(new BenchmarkRow(
                        dataset.Name,
                        dataset.Values.Length,
                        dataset.CharacterCount,
                        index.Name,
                        query.Name,
                        query.Needle.Length,
                        expected.Matches.Count,
                        measurement.Result.CandidateCount,
                        Percent(expected.Matches.Count, dataset.Values.Length),
                        FalsePositivePercent(measurement.Result),
                        buildWatch.Elapsed.TotalMilliseconds,
                        managedMiB,
                        privateMiB,
                        measurement.ColdMicroseconds,
                        measurement.P50Microseconds,
                        measurement.P95Microseconds,
                        measurement.AllocatedBytes,
                        "OK"));
                }

                if (includeConcurrency && index.SupportsConcurrentQueries)
                {
                    report.Concurrency.Add(MeasureConcurrency(dataset, index));
                }

                if (includeMutations && ShouldMeasureMutation(descriptor))
                {
                    report.Mutations.AddRange(MeasureMutations(dataset, descriptor, index));
                }
            }
            catch (Exception exception)
            {
                var status = $"{exception.GetType().Name}: {exception.Message}";
                output.WriteLine($"    ERROR {status}");
                report.Queries.Add(new BenchmarkRow(
                    dataset.Name,
                    dataset.Values.Length,
                    dataset.CharacterCount,
                    descriptor.Id,
                    "build",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    status));
            }
            finally
            {
                index?.Dispose();
                ForceGc();
            }
        }
    }

    private static bool ShouldMeasureMutation(IndexDescriptor descriptor) => descriptor.Id is
        "scan" or "packed-gram" or "adaptive-gram" or "signature-256" or "signature-1024" or
        "native-sa" or "segmented-sa" or "lifti" or "lucene";

    private static IReadOnlyList<MutationRow> MeasureMutations(
        CorpusDataset dataset,
        IndexDescriptor descriptor,
        IContainsIndex index)
    {
        var rows = new List<MutationRow>();
        var changed = (string[])dataset.Values.Clone();
        var itemCount = Math.Min(1_000, Math.Max(1, dataset.Values.Length / 20));
        var ids = new int[itemCount];
        for (var i = 0; i < itemCount; i++)
        {
            ids[i] = (17 + (i * Math.Max(1, dataset.Values.Length / itemCount))) % dataset.Values.Length;
            changed[ids[i]] += MutationMarker;
        }

        var operation = index.SupportsIncrementalMutation ? "incremental-upsert" : "full-rebuild-after-upserts";
        var watch = Stopwatch.StartNew();
        if (index.SupportsIncrementalMutation)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                index.Upsert(ids[i], changed[ids[i]]);
            }
        }
        else
        {
            index.Build(changed);
        }

        watch.Stop();
        var queryMeasurement = MeasureQuery(index, MutationMarker);
        var expected = ContainsOracle.Query(changed, MutationMarker);
        var status = queryMeasurement.Result.Matches == expected.Matches ? "OK" : "Incorrect after mutation";
        rows.Add(new MutationRow(
            dataset.Name,
            index.Name,
            operation,
            itemCount,
            watch.Elapsed.TotalMilliseconds,
            watch.Elapsed.TotalMilliseconds * 1000d / itemCount,
            queryMeasurement.P50Microseconds,
            status));

        if (index is SegmentedSuffixArrayIndex segmented)
        {
            var compactWatch = Stopwatch.StartNew();
            segmented.Compact();
            compactWatch.Stop();
            var compactQuery = MeasureQuery(segmented, MutationMarker);
            rows.Add(new MutationRow(
                dataset.Name,
                index.Name,
                "segment-compaction",
                dataset.Values.Length,
                compactWatch.Elapsed.TotalMilliseconds,
                compactWatch.Elapsed.TotalMilliseconds * 1000d / dataset.Values.Length,
                compactQuery.P50Microseconds,
                compactQuery.Result.Matches == expected.Matches ? "OK" : "Incorrect after compaction"));
        }

        return rows;
    }

    private static ConcurrencyRow MeasureConcurrency(CorpusDataset dataset, IContainsIndex index)
    {
        var needle = dataset.Queries.Single(static query => query.Name == "rare-8").Needle;
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var probeStart = Stopwatch.GetTimestamp();
        Consume(index.Query(needle));
        var probeSeconds = Math.Max(Stopwatch.GetElapsedTime(probeStart).TotalSeconds, 0.000001);
        var desiredTotal = Math.Clamp((int)Math.Ceiling(0.25 / probeSeconds), workers, 256);
        var queriesPerWorker = Math.Max(1, desiredTotal / workers);
        var totalQueries = workers * queriesPerWorker;

        var singleWatch = Stopwatch.StartNew();
        for (var i = 0; i < totalQueries; i++)
        {
            Consume(index.Query(needle));
        }

        singleWatch.Stop();
        var parallelWatch = Stopwatch.StartNew();
        Parallel.For(0, workers, _ =>
        {
            for (var query = 0; query < queriesPerWorker; query++)
            {
                Consume(index.Query(needle));
            }
        });
        parallelWatch.Stop();

        var singleQps = totalQueries / singleWatch.Elapsed.TotalSeconds;
        var parallelQps = totalQueries / parallelWatch.Elapsed.TotalSeconds;
        return new ConcurrencyRow(
            dataset.Name,
            index.Name,
            workers,
            totalQueries,
            singleQps,
            parallelQps,
            parallelQps / singleQps,
            "OK");
    }

    private static QueryMeasurement MeasureQuery(IContainsIndex index, string needle)
    {
        var probeStart = Stopwatch.GetTimestamp();
        var result = index.Query(needle);
        var probeSeconds = Math.Max(Stopwatch.GetElapsedTime(probeStart).TotalSeconds, 0.0000001);
        Consume(result);

        FlushCaches();
        var coldStart = Stopwatch.GetTimestamp();
        Consume(index.Query(needle));
        var coldElapsed = Stopwatch.GetElapsedTime(coldStart);

        var iterations = Math.Clamp((int)Math.Ceiling(0.008 / probeSeconds), 1, 20_000);
        var samples = new double[probeSeconds >= 0.05 ? 3 : 5];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var start = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                Consume(index.Query(needle));
            }

            samples[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Consume(index.Query(needle));
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)iterations;
        Array.Sort(samples);
        return new QueryMeasurement(
            result,
            coldElapsed.TotalMicroseconds,
            samples[samples.Length / 2],
            samples[(int)Math.Ceiling(samples.Length * 0.95) - 1],
            allocated);
    }

    private static void RunConstructionBackends(CorpusDataset dataset, BenchmarkReport report, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("Suffix-construction backend comparison");
        var joined = JoinedCorpusBuilder.Create(dataset.Values).Text;
        var utf8 = Encoding.UTF8.GetBytes(joined);

        Measure("NLibSais UTF-16 native", joined.Length, () =>
        {
            var suffixes = new int[joined.Length];
            LibSais.ConstructSuffixArray(joined.AsSpan(), suffixes, Span<int>.Empty);
            ValidateSuffixSamples(joined, suffixes);
        });

        Measure("DeltaQ SAIS UTF-8", utf8.Length, () =>
        {
            var suffixes = new int[utf8.Length];
            new SAIS().Sort(utf8, suffixes);
            ValidateSuffixSamples(utf8, suffixes);
        });

        Measure("DeltaQ GoSAIS UTF-8", utf8.Length, () =>
        {
            var suffixes = new int[utf8.Length];
            new GoSAIS().Sort(utf8, suffixes);
            ValidateSuffixSamples(utf8, suffixes);
        });

        Measure("CaseON prefix-doubling UTF-16", joined.Length, () =>
        {
            var suffixes = new CaseON.SuffixArray(joined).Suffixes;
            ValidateSuffixSamples(joined, suffixes);
        });

        void Measure(string name, int units, Action action)
        {
            ForceGc();
            var before = GC.GetTotalMemory(forceFullCollection: true);
            try
            {
                var watch = Stopwatch.StartNew();
                action();
                watch.Stop();
                var after = GC.GetTotalMemory(forceFullCollection: false);
                report.Construction.Add(new ConstructionRow(
                    dataset.Name,
                    name,
                    units,
                    watch.Elapsed.TotalMilliseconds,
                    Math.Max(0, after - before) / 1048576d,
                    "OK"));
                output.WriteLine($"  {name}: {watch.Elapsed.TotalMilliseconds:N1} ms");
            }
            catch (Exception exception)
            {
                var status = $"{exception.GetType().Name}: {exception.Message}";
                report.Construction.Add(new ConstructionRow(dataset.Name, name, units, 0, 0, status));
                output.WriteLine($"  {name}: {status}");
            }
        }
    }

    private static void RunAhoCorasickBatch(CorpusDataset dataset, BenchmarkReport report, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("Multi-pattern batch comparison");
        foreach (var patternCount in new[] { 8, 64, 512, 1_024 })
        {
            var patterns = BuildBatchPatterns(dataset.Values, patternCount);

            var baselineWatch = Stopwatch.StartNew();
            long expected = 0;
            foreach (var pattern in patterns)
            {
                foreach (var value in dataset.Values)
                {
                    if (value.Contains(pattern, StringComparison.Ordinal))
                    {
                        expected++;
                    }
                }
            }

            baselineWatch.Stop();
            report.Batch.Add(new BatchRow(dataset.Name, ".NET repeated scans", patterns.Length, expected, 0, baselineWatch.Elapsed.TotalMilliseconds, "OK"));

            var buildWatch = Stopwatch.StartNew();
            var automaton = new AhoCorasick(patterns);
            buildWatch.Stop();
            var patternIds = patterns.Select((pattern, id) => (pattern, id)).ToDictionary(static pair => pair.pattern, static pair => pair.id, StringComparer.Ordinal);
            var stamps = new int[patterns.Length];
            var generation = 0;
            long actual = 0;
            var searchWatch = Stopwatch.StartNew();
            foreach (var value in dataset.Values)
            {
                generation++;
                foreach (var match in automaton.Search(value))
                {
                    var patternId = patternIds[match.Word];
                    if (stamps[patternId] != generation)
                    {
                        stamps[patternId] = generation;
                        actual++;
                    }
                }
            }

            searchWatch.Stop();
            var status = actual == expected ? "OK" : $"Incorrect: expected {expected}, actual {actual}";
            report.Batch.Add(new BatchRow(
                dataset.Name,
                "AhoCorasick NuGet",
                patterns.Length,
                actual,
                buildWatch.Elapsed.TotalMilliseconds,
                searchWatch.Elapsed.TotalMilliseconds,
                status));
            output.WriteLine(
                $"  {patterns.Length,4} patterns: scans {baselineWatch.Elapsed.TotalMilliseconds,8:N1} ms; " +
                $"Aho build {buildWatch.Elapsed.TotalMilliseconds,6:N1} ms + search {searchWatch.Elapsed.TotalMilliseconds,8:N1} ms ({status})");
        }
    }

    private static string[] BuildBatchPatterns(string[] values, int count)
    {
        var patterns = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; patterns.Count < count && i < values.Length * 2; i++)
        {
            var value = values[(i * 1543) % values.Length];
            if (value.Length < 8)
            {
                continue;
            }

            var start = (i * 17) % (value.Length - 7);
            patterns.Add(value.Substring(start, 8));
        }

        return patterns.ToArray();
    }

    private static void ValidateSuffixSamples(string text, IReadOnlyList<int> suffixes)
    {
        if (suffixes.Count != text.Length)
        {
            throw new InvalidOperationException("Suffix count differs from input length.");
        }

        var step = Math.Max(1, suffixes.Count / 128);
        for (var i = step; i < suffixes.Count; i += step)
        {
            var previous = suffixes[i - 1];
            var current = suffixes[i];
            if (text.AsSpan(previous).SequenceCompareTo(text.AsSpan(current)) > 0)
            {
                throw new InvalidOperationException("Suffix output is not sorted.");
            }
        }
    }

    private static void ValidateSuffixSamples(byte[] text, IReadOnlyList<int> suffixes)
    {
        if (suffixes.Count != text.Length)
        {
            throw new InvalidOperationException("Suffix count differs from input length.");
        }

        var step = Math.Max(1, suffixes.Count / 128);
        for (var i = step; i < suffixes.Count; i += step)
        {
            var previous = suffixes[i - 1];
            var current = suffixes[i];
            if (text.AsSpan(previous).SequenceCompareTo(text.AsSpan(current)) > 0)
            {
                throw new InvalidOperationException("Suffix output is not sorted.");
            }
        }
    }

    private static double FalsePositivePercent(QueryStats result) => result.CandidateCount == 0
        ? 0
        : 100d * (result.CandidateCount - result.Matches.Count) / result.CandidateCount;

    private static double Percent(long numerator, long denominator) => denominator == 0
        ? 0
        : 100d * numerator / denominator;

    private static void FlushCaches()
    {
        long sum = 0;
        for (var i = 0; i < CacheBuffer.Length; i += 64)
        {
            CacheBuffer[i]++;
            sum += CacheBuffer[i];
        }

        _sink = unchecked(_sink + sum);
    }

    private static void Consume(QueryStats result) =>
        _sink = unchecked(_sink + (long)(result.Matches.Sum ^ result.Matches.Xor) + result.CandidateCount);

    private static void ForceGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string ProjectDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static string SystemDescription() =>
        $"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; " +
        $"logical CPUs={Environment.ProcessorCount}; Vector128={System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated}; " +
        $"Vector256={System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated}; Vector512={System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated}";

    private static void WriteQueriesCsv(string path, IEnumerable<BenchmarkRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Dataset,Documents,Characters,Index,Query,NeedleLength,Matches,Candidates,SelectivityPercent,FalsePositivePercent,BuildMilliseconds,ManagedMiB,PrivateMiB,ColdMicroseconds,P50Microseconds,P95Microseconds,AllocatedBytesPerQuery,Status");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',',
                Csv(row.Dataset),
                row.Documents,
                row.Characters,
                Csv(row.Index),
                Csv(row.Query),
                row.NeedleLength,
                row.Matches,
                row.Candidates,
                F(row.SelectivityPercent),
                F(row.FalsePositivePercent),
                F(row.BuildMilliseconds),
                F(row.ManagedMiB),
                F(row.PrivateMiB),
                F(row.ColdMicroseconds),
                F(row.P50Microseconds),
                F(row.P95Microseconds),
                F(row.AllocatedBytesPerQuery),
                Csv(row.Status)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteMutationsCsv(string path, IEnumerable<MutationRow> rows)
    {
        var builder = new StringBuilder("Dataset,Index,Operation,ItemCount,TotalMilliseconds,MicrosecondsPerItem,PostMutationQueryMicroseconds,Status\n");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Csv(row.Dataset), Csv(row.Index), Csv(row.Operation), row.ItemCount, F(row.TotalMilliseconds), F(row.MicrosecondsPerItem), F(row.PostMutationQueryMicroseconds), Csv(row.Status)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteConcurrencyCsv(string path, IEnumerable<ConcurrencyRow> rows)
    {
        var builder = new StringBuilder("Dataset,Index,Workers,QueryCount,SingleThreadQps,ParallelQps,Scaling,Status\n");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Csv(row.Dataset), Csv(row.Index), row.Workers, row.QueryCount, F(row.SingleThreadQueriesPerSecond), F(row.ParallelQueriesPerSecond), F(row.Scaling), Csv(row.Status)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteConstructionCsv(string path, IEnumerable<ConstructionRow> rows)
    {
        var builder = new StringBuilder("Dataset,Builder,InputUnits,Milliseconds,ManagedMiB,Status\n");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Csv(row.Dataset), Csv(row.Builder), row.InputUnits, F(row.Milliseconds), F(row.ManagedMiB), Csv(row.Status)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void WriteBatchCsv(string path, IEnumerable<BatchRow> rows)
    {
        var builder = new StringBuilder("Dataset,Method,Patterns,DocumentPatternMatches,BuildMilliseconds,SearchMilliseconds,Status\n");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', Csv(row.Dataset), Csv(row.Method), row.Patterns, row.DocumentPatternMatches, F(row.BuildMilliseconds), F(row.SearchMilliseconds), Csv(row.Status)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private readonly record struct QueryMeasurement(
        QueryStats Result,
        double ColdMicroseconds,
        double P50Microseconds,
        double P95Microseconds,
        double AllocatedBytes);
}

internal static class ReportWriter
{
    public static string ToMarkdown(BenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Contains-index benchmark results");
        builder.AppendLine();
        builder.AppendLine($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} on {RuntimeInformation.FrameworkDescription}.");
        builder.AppendLine();
        builder.AppendLine("All query results are verified against ordinal, case-sensitive `string.Contains`. Query latency includes candidate verification and ID digesting, but excludes InterfaceDB physical-key materialization. P50/P95 are warm measurements; cold latency follows a 64 MiB cache-thrashing pass. Memory deltas are process observations and should be treated as approximate, especially for native libraries.");
        builder.AppendLine();

        AppendWinnerTables(builder, report);
        AppendMutationTable(builder, report.Mutations);
        AppendConcurrencyTable(builder, report.Concurrency);
        AppendConstructionTable(builder, report.Construction);
        AppendBatchTable(builder, report.Batch);
        AppendFailures(builder, report.Queries);
        return builder.ToString();
    }

    private static void AppendWinnerTables(StringBuilder builder, BenchmarkReport report)
    {
        builder.AppendLine("## Fastest warm query by dataset and query");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Query | Matches | Winner | P50 µs | Runner-up | P50 µs |");
        builder.AppendLine("|---|---:|---:|---|---:|---|---:|");
        foreach (var group in report.Queries
                     .Where(static row => row.Status == "OK")
                     .GroupBy(static row => (row.Dataset, row.Query)))
        {
            var ordered = group.OrderBy(static row => row.P50Microseconds).ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var first = ordered[0];
            var second = ordered.Length > 1 ? ordered[1] : first;
            builder.AppendLine($"| {Escape(first.Dataset)} | {Escape(first.Query)} | {first.Matches:N0} | {Escape(first.Index)} | {first.P50Microseconds:N2} | {Escape(second.Index)} | {second.P50Microseconds:N2} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Build and retained-memory snapshot");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Index | Build ms | Managed Δ MiB | Private Δ MiB |");
        builder.AppendLine("|---|---|---:|---:|---:|");
        foreach (var row in report.Queries
                     .Where(static row => row.Status == "OK")
                     .GroupBy(static row => (row.Dataset, row.Index))
                     .Select(static group => group.First())
                     .OrderBy(static row => row.Dataset)
                     .ThenBy(static row => row.BuildMilliseconds))
        {
            builder.AppendLine($"| {Escape(row.Dataset)} | {Escape(row.Index)} | {row.BuildMilliseconds:N1} | {row.ManagedMiB:N1} | {row.PrivateMiB:N1} |");
        }

        builder.AppendLine();
    }

    private static void AppendMutationTable(StringBuilder builder, IReadOnlyList<MutationRow> rows)
    {
        builder.AppendLine("## Mutation and rebuild cost");
        builder.AppendLine();
        builder.AppendLine("| Index | Operation | Items | Total ms | µs/item | Post-query µs | Status |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var row in rows)
        {
            builder.AppendLine($"| {Escape(row.Index)} | {Escape(row.Operation)} | {row.ItemCount:N0} | {row.TotalMilliseconds:N2} | {row.MicrosecondsPerItem:N2} | {row.PostMutationQueryMicroseconds:N2} | {Escape(row.Status)} |");
        }

        builder.AppendLine();
    }

    private static void AppendConcurrencyTable(StringBuilder builder, IReadOnlyList<ConcurrencyRow> rows)
    {
        builder.AppendLine("## Read concurrency");
        builder.AppendLine();
        builder.AppendLine("| Index | Workers | Single QPS | Parallel QPS | Scaling |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var row in rows.OrderByDescending(static row => row.ParallelQueriesPerSecond))
        {
            builder.AppendLine($"| {Escape(row.Index)} | {row.Workers} | {row.SingleThreadQueriesPerSecond:N0} | {row.ParallelQueriesPerSecond:N0} | {row.Scaling:N2}× |");
        }

        builder.AppendLine();
    }

    private static void AppendConstructionTable(StringBuilder builder, IReadOnlyList<ConstructionRow> rows)
    {
        builder.AppendLine("## Suffix-array construction backends");
        builder.AppendLine();
        builder.AppendLine("| Builder | Input units | Build ms | Managed Δ MiB | Status |");
        builder.AppendLine("|---|---:|---:|---:|---|");
        foreach (var row in rows.OrderBy(static row => row.Milliseconds))
        {
            builder.AppendLine($"| {Escape(row.Builder)} | {row.InputUnits:N0} | {row.Milliseconds:N1} | {row.ManagedMiB:N1} | {Escape(row.Status)} |");
        }

        builder.AppendLine();
    }

    private static void AppendBatchTable(StringBuilder builder, IReadOnlyList<BatchRow> rows)
    {
        builder.AppendLine("## Multi-pattern batch search");
        builder.AppendLine();
        builder.AppendLine("| Method | Patterns | Build ms | Search ms | Status |");
        builder.AppendLine("|---|---:|---:|---:|---|");
        foreach (var row in rows)
        {
            builder.AppendLine($"| {Escape(row.Method)} | {row.Patterns} | {row.BuildMilliseconds:N2} | {row.SearchMilliseconds:N2} | {Escape(row.Status)} |");
        }

        builder.AppendLine();
    }

    private static void AppendFailures(StringBuilder builder, IReadOnlyList<BenchmarkRow> rows)
    {
        var failures = rows.Where(static row => row.Status != "OK").ToArray();
        if (failures.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Unsupported or incorrect cases");
        builder.AppendLine();
        foreach (var row in failures)
        {
            builder.AppendLine($"- {row.Dataset} / {row.Index}: {row.Status}");
        }
    }

    private static string Escape(string value) => value.Replace("|", "\\|");
}
