using System.Diagnostics;
using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Indexes;

[MemoryDiagnoser]
public class QueryPlannerBenchmarks : IDisposable
{
    private Expression<Func<PlannerBenchmarkRecord, bool>> _agePredicate = null!;
    private Expression<Func<PlannerBenchmarkRecord, bool>> _containsPredicate = null!;
    private Expression<Func<PlannerBenchmarkRecord, bool>> _groupPredicate = null!;
    private Expression<Func<PlannerBenchmarkRecord, bool>> _intersectionPredicate = null!;
    private SearchIndexStore<PlannerBenchmarkRecord>? _store;
    private Dictionary<string, PlannerBenchmarkRecord>? _recordsById;
    private PlannerBenchmarkRecord[]? _records;

    [Params(100_000)]
    public int RecordCount { get; set; } = 100_000;

    [GlobalSetup]
    public void Setup()
    {
        DisposeStore();
        var midpoint = RecordCount / 2;
        _groupPredicate = record => record.Group == "A";
        _agePredicate = record => record.Age >= midpoint;
        _intersectionPredicate = record => record.Group == "A" && record.Age >= midpoint;
        _containsPredicate = record => record.Text.Contains("needle", StringComparison.Ordinal);
        _store = new SearchIndexStore<PlannerBenchmarkRecord>(SearchQueryPlannerOptions.StrongObject);
        _records = new PlannerBenchmarkRecord[RecordCount];
        _recordsById = new Dictionary<string, PlannerBenchmarkRecord>(RecordCount, StringComparer.Ordinal);
        for (var index = 0; index < RecordCount; index++)
        {
            var record = CreateRecord(index);
            _records[index] = record;
            _recordsById.Add(record.Id, record);
            _store.Upsert(record.Id, record);
        }
    }

    [GlobalCleanup]
    public void Dispose() => DisposeStore();

    [Benchmark(Baseline = true, Description = "Full scan: Group == A && Age >= midpoint")]
    public int FullScanIntersection()
    {
        var records = _records ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var midpoint = RecordCount / 2;
        var count = 0;
        foreach (var record in records)
        {
            if (record.Group == "A" && record.Age >= midpoint)
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark(Description = "Previous planner: smallest single index plus residual filtering")]
    public int SingleBestIndexThenFilter()
    {
        var store = _store ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var records = _recordsById ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var midpoint = RecordCount / 2;
        var candidates = store.TryEvaluate(_groupPredicate)
            ?? throw new InvalidOperationException("Expected an indexed candidate set.");
        var count = 0;
        foreach (var key in candidates)
        {
            var record = records[key];
            if (record.Age >= midpoint)
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark(Description = "Always intersect every usable index")]
    public int AlwaysIntersectIndexes()
    {
        var store = _store ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var group = store.TryEvaluate(_groupPredicate)
            ?? throw new InvalidOperationException("Expected an indexed group candidate set.");
        var age = store.TryEvaluate(_agePredicate)
            ?? throw new InvalidOperationException("Expected an indexed age candidate set.");
        group.IntersectWith(age);
        return group.Count;
    }

    [Benchmark(Description = "Cost-aware conditional index intersection")]
    public int CostAwareIntersection()
    {
        var store = _store ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var records = _recordsById ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var midpoint = RecordCount / 2;
        var candidates = store.TryEvaluate(_intersectionPredicate)
            ?? throw new InvalidOperationException("Expected a cost-aware candidate set.");
        var count = 0;
        foreach (var key in candidates)
        {
            var record = records[key];
            if (record.Group == "A" && record.Age >= midpoint)
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark(Description = "Full ordinal scan: rare contains")]
    public int FullScanContains()
    {
        var records = _records ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var count = 0;
        foreach (var record in records)
        {
            if (record.Text.Contains("needle", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark(Description = "Adaptive gram access path: rare contains")]
    public int PlannedContains()
    {
        var store = _store ?? throw new InvalidOperationException("Benchmark is not initialized.");
        return store.TryEvaluate(_containsPredicate)?.Count
            ?? throw new InvalidOperationException("Expected a contains candidate set.");
    }

    public static void Validate()
    {
        using var benchmark = new QueryPlannerBenchmarks { RecordCount = 20_000 };
        benchmark.Setup();
        var expectedIntersection = benchmark.FullScanIntersection();
        RequireEqual(expectedIntersection, benchmark.SingleBestIndexThenFilter(), "single-best index");
        RequireEqual(expectedIntersection, benchmark.AlwaysIntersectIndexes(), "always-intersect indexes");
        RequireEqual(expectedIntersection, benchmark.CostAwareIntersection(), "cost-aware intersection");
        RequireEqual(benchmark.FullScanContains(), benchmark.PlannedContains(), "planned contains");

        var store = benchmark._store
            ?? throw new InvalidOperationException("Benchmark is not initialized.");
        var explanation = store.Explain(benchmark._intersectionPredicate);
        if (explanation.Contains("Intersection", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Strong-object planning should avoid an unprofitable intersection.{Environment.NewLine}{explanation}");
        }

        Console.WriteLine("Query planner benchmark adapters validated.");
        Console.WriteLine(explanation);
    }

    public static void RunQuickComparison()
    {
        using var benchmark = new QueryPlannerBenchmarks { RecordCount = 100_000 };
        benchmark.Setup();
        const int iterations = 20;
        Console.WriteLine($"Quick query-planner comparison: {benchmark.RecordCount:N0} records, {iterations} iterations");
        Measure("Full scan intersection", iterations, benchmark.FullScanIntersection);
        Measure("Single best index + filter", iterations, benchmark.SingleBestIndexThenFilter);
        Measure("Always intersect indexes", iterations, benchmark.AlwaysIntersectIndexes);
        Measure("Cost-aware intersection", iterations, benchmark.CostAwareIntersection);
        Measure("Full scan contains", iterations, benchmark.FullScanContains);
        Measure("Adaptive gram contains", iterations, benchmark.PlannedContains);
    }

    private static PlannerBenchmarkRecord CreateRecord(int index)
        => new()
        {
            Id = $"row-{index}",
            Group = index % 2 == 0 ? "A" : "B",
            Age = index,
            Text = index % 997 == 0 ? $"record-{index}-needle" : $"record-{index}"
        };

    private static void Measure(string name, int iterations, Func<int> action)
    {
        _ = action();
        var stopwatch = Stopwatch.StartNew();
        var digest = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            digest ^= action();
        }

        stopwatch.Stop();
        Console.WriteLine(
            $"{name,-30} {stopwatch.Elapsed.TotalMilliseconds / iterations,10:N3} ms/op  digest={digest}");
    }

    private static void RequireEqual(int expected, int actual, string operation)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException(
                $"{operation} returned {actual:N0}; expected {expected:N0}.");
        }
    }

    private void DisposeStore()
    {
        _store?.Dispose();
        _store = null;
        _records = null;
        _recordsById = null;
    }

    [SearchIndex]
    private sealed class PlannerBenchmarkRecord
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public string Id { get; init; } = string.Empty;

        [SearchIndex(SearchIndexMode.Exact)]
        public string Group { get; init; } = string.Empty;

        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
        public int Age { get; init; }

        [SearchIndex(SearchIndexMode.Contains)]
        public string Text { get; init; } = string.Empty;
    }
}
