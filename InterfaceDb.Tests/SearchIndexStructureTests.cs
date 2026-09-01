using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Indexes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RadixForge;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchIndexStructureTests
{
    [TestMethod]
    public void SearchIndexManagerSupportsAsyncDisposal()
    {
        Assert.IsTrue(typeof(IAsyncDisposable).IsAssignableFrom(typeof(SearchIndexManager<>)));
    }

    [TestMethod]
    public void RadixPrefixIndexUsesOrdinalPrefixesAndMaintainsRemovals()
    {
        var index = new RadixPrefixSearchIndex();
        index.Add("Alice", "alice");
        index.Add("Alicia", "alicia");
        index.Add("alice", "lowercase");
        index.Add("Al😀pha", "surrogate");

        CollectionAssert.AreEquivalent(
            new[] { "alice", "alicia" },
            index.Search("Ali").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "lowercase" },
            index.Search("ali").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "surrogate" },
            index.Search("Al😀").ToArray());
        Assert.AreEqual(4, index.EstimateCount(string.Empty));

        index.Remove("Alice", "alice");

        CollectionAssert.AreEquivalent(
            new[] { "alicia" },
            index.Search("Ali").ToArray());
    }

    [TestMethod]
    public void RadixSuffixIndexUsesReversedOrdinalKeysAndMaintainsRemovals()
    {
        using var index = new RadixSuffixSearchIndex();
        index.Add("Anderson", "anderson");
        index.Add("Johnson", "johnson");
        index.Add("ANDERSON", "uppercase");
        index.Add("music😀", "surrogate");
        index.Add("broken\uD800", "unpaired");

        CollectionAssert.AreEquivalent(
            new[] { "anderson", "johnson" },
            index.Search("son").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "uppercase" },
            index.Search("SON").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "surrogate" },
            index.Search("😀").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "unpaired" },
            index.Search("\uD800").ToArray());
        Assert.AreEqual(5, index.EstimateCount(string.Empty));

        index.Remove("Anderson", "anderson");

        CollectionAssert.AreEquivalent(
            new[] { "johnson" },
            index.Search("son").ToArray());
    }

    [TestMethod]
    public void SearchStorePlansOnlyOrdinalPrefixAndSuffixCalls()
    {
        using var store = new SearchIndexStore<IndexedStringRecord>();
        store.Upsert("anderson", new IndexedStringRecord { Value = "Anderson" });
        store.Upsert("johnson", new IndexedStringRecord { Value = "Johnson" });
        store.Upsert("alice", new IndexedStringRecord { Value = "Alice" });

        CollectionAssert.AreEquivalent(
            new[] { "anderson", "johnson" },
            store.TryEvaluate(record => record.Value.EndsWith("son", StringComparison.Ordinal))!.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "alice" },
            store.TryEvaluate(record => record.Value.StartsWith("Ali", StringComparison.Ordinal))!.ToArray());
        Assert.IsNull(store.TryEvaluate(record => record.Value.EndsWith("son")));
        Assert.IsNull(store.TryEvaluate(record => record.Value.EndsWith("SON", StringComparison.OrdinalIgnoreCase)));

        store.Upsert("anderson", new IndexedStringRecord { Value = "Anders" });

        CollectionAssert.AreEquivalent(
            new[] { "johnson" },
            store.TryEvaluate(record => record.Value.EndsWith("son", StringComparison.Ordinal))!.ToArray());
        store.Remove("johnson");
        Assert.AreEqual(
            0,
            store.TryEvaluate(record => record.Value.EndsWith("son", StringComparison.Ordinal))!.Count);
    }

    [TestMethod]
    public void AdaptiveContainsIndexMaintainsOrdinalTextAcrossMutations()
    {
        using var index = new AdaptiveContainsSearchIndex();
        index.Upsert("High-performance in-memory indexes", "first");
        index.Upsert("A compact radix tree", "second");
        index.Upsert("Unicode 😀 search", "unicode");

        CollectionAssert.AreEquivalent(
            new[] { "first" },
            index.Search("memory").ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "unicode" },
            index.Search("😀").ToArray());
        Assert.AreEqual(0, index.EstimateCount("Memory"));

        index.Upsert("Updated searchable text", "first");
        index.Remove("second");
        index.Compact();

        Assert.AreEqual(0, index.Search("memory").Count);
        CollectionAssert.AreEquivalent(
            new[] { "first" },
            index.Search("searchable").ToArray());
        Assert.AreEqual(0, index.PendingMutationCount);
    }

    [TestMethod]
    public void SearchStorePlansOrdinalContainsAndRejectsOtherComparisons()
    {
        using var store = new SearchIndexStore<PlannerRecord>();
        store.Upsert("first", new PlannerRecord
        {
            Id = "first",
            Group = "A",
            Age = 10,
            Text = "High-performance in-memory indexes"
        });
        store.Upsert("second", new PlannerRecord
        {
            Id = "second",
            Group = "B",
            Age = 20,
            Text = "A compact radix tree"
        });

        CollectionAssert.AreEquivalent(
            new[] { "first" },
            store.TryEvaluate(record => record.Text.Contains("memory"))!.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "second" },
            store.TryEvaluate(record => record.Text.Contains("radix", StringComparison.Ordinal))!.ToArray());
        Assert.IsNull(store.TryEvaluate(
            record => record.Text.Contains("RADIX", StringComparison.OrdinalIgnoreCase)));

        var explanation = store.Explain(record => record.Text.Contains("memory"));
        StringAssert.Contains(explanation, "IndexScan");
        StringAssert.Contains(explanation, "Contains index on Text");
    }

    [TestMethod]
    public void QueryPlannerUsesConditionalIntersectionAndResidualFiltering()
    {
        using var store = CreatePlannerStore(4_096, out var records);

        var intersecting = store.Explain(
            record => record.Group == "A" && record.Age >= 2_048);
        StringAssert.Contains(intersecting, "Intersection");
        StringAssert.Contains(intersecting, "Equal index on Group");
        StringAssert.Contains(intersecting, "GreaterThanOrEqual index on Age");
        AssertEquivalentToFullScan(
            store,
            records,
            record => record.Group == "A" && record.Age >= 2_048);

        var selective = store.Explain(
            record => record.Id == "row-1" && record.Age >= 0);
        StringAssert.Contains(selective, "IndexScan");
        Assert.IsFalse(selective.Contains("Intersection", StringComparison.Ordinal));
        StringAssert.Contains(selective, "residual predicates=1");
        AssertEquivalentToFullScan(
            store,
            records,
            record => record.Id == "row-1" && record.Age >= 0);

        var bounded = store.Explain(
            record => record.Age >= 1_000 && record.Age < 1_100);
        StringAssert.Contains(bounded, "Bounded range index on Age");
        AssertEquivalentToFullScan(
            store,
            records,
            record => record.Age >= 1_000 && record.Age < 1_100);
    }

    [TestMethod]
    public void QueryPlannerAdaptsIntersectionToObjectLoadingCost()
    {
        using var metadataStore = CreatePlannerStore(
            4_096,
            SearchQueryPlannerOptions.MetadataOnly,
            out _);
        using var strongStore = CreatePlannerStore(
            4_096,
            SearchQueryPlannerOptions.StrongObject,
            out _);

        var metadataPlan = metadataStore.Explain(
            record => record.Group == "A" && record.Age >= 2_048);
        var strongPlan = strongStore.Explain(
            record => record.Group == "A" && record.Age >= 2_048);

        StringAssert.Contains(metadataPlan, "Intersection");
        Assert.IsFalse(strongPlan.Contains("Intersection", StringComparison.Ordinal));
        StringAssert.Contains(strongPlan, "residual predicates=1");
    }

    [TestMethod]
    public void QueryPlannerUsesUnionAndSafelyFallsBackForUnindexedOrBranches()
    {
        using var store = CreatePlannerStore(1_024, out var records);

        var union = store.Explain(
            record => record.Group == "A" || record.Age >= 1_000);
        StringAssert.Contains(union, "Union");
        AssertEquivalentToFullScan(
            store,
            records,
            record => record.Group == "A" || record.Age >= 1_000);

        var fallback = store.Explain(
            record => record.Group == "A" || record.Text.ToUpperInvariant() == "NEVER");
        StringAssert.Contains(fallback, "FullScan");
        Assert.IsNull(store.TryEvaluate(
            record => record.Group == "A" || record.Text.ToUpperInvariant() == "NEVER"));
    }

    [TestMethod]
    public void NegativeContainsUsesPositiveCandidateOrFallsBackToScan()
    {
        using var store = CreatePlannerStore(1_024, out var records);

        var positiveAnchor = store.Explain(
            record => record.Group == "A" && !record.Text.Contains("needle"));
        StringAssert.Contains(positiveAnchor, "Equal index on Group");
        StringAssert.Contains(positiveAnchor, "residual predicates=1");
        AssertEquivalentToFullScan(
            store,
            records,
            record => record.Group == "A" && !record.Text.Contains("needle"));

        var standalone = store.Explain(record => !record.Text.Contains("needle"));
        StringAssert.Contains(standalone, "FullScan");
        Assert.IsNull(store.TryEvaluate(record => !record.Text.Contains("needle")));
    }

    [TestMethod]
    public void QueryPlannerMatchesFullScanAcrossMixedExpressionShapes()
    {
        using var store = CreatePlannerStore(2_000, out var records);
        Expression<Func<PlannerRecord, bool>>[] predicates =
        [
            record => record.Group == "A" && record.Age >= 500 && record.Age < 1_500,
            record => record.Text.Contains("needle") && record.Age > 100,
            record => record.Text.StartsWith("entry-1", StringComparison.Ordinal)
                || record.Text.EndsWith("needle", StringComparison.Ordinal),
            record => record.Group == "B" && !record.Text.Contains("needle"),
            record => record.Text.ToUpperInvariant().Contains("NEEDLE"),
            record => (record.Group == "A" && record.Age < 50)
                || (record.Group == "B" && record.Text.Contains("needle"))
        ];

        foreach (var predicate in predicates)
        {
            AssertEquivalentToFullScan(store, records, predicate);
        }
    }

    [TestMethod]
    public void SearchStoreSerializesConcurrentMutationsAndQueries()
    {
        using var store = new SearchIndexStore<PlannerRecord>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Parallel.Invoke(
            () =>
            {
                try
                {
                    for (var index = 0; index < 2_000; index++)
                    {
                        store.Upsert($"row-{index}", CreatePlannerRecord(index));
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            },
            () => RunConcurrentQueries(store, failures),
            () => RunConcurrentQueries(store, failures));

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        Assert.AreEqual(2_000, store.IndexedObjectCount);
        AssertEquivalentToFullScan(
            store,
            Enumerable.Range(0, 2_000).Select(CreatePlannerRecord).ToArray(),
            record => record.Group == "A" && record.Text.Contains("needle"));
    }

    [TestMethod]
    public void SortedBucketRangeIndexHonorsEveryBoundaryShape()
    {
        var index = new AdaptiveRangeSearchIndex<int>();
        index.Add(10, "ten-a");
        index.Add(10, "ten-b");
        index.Add(20, "twenty");
        index.Add(30, "thirty");

        AssertKeys(index, SearchComparisonOperator.Equal, 10, "ten-a", "ten-b");
        AssertKeys(index, SearchComparisonOperator.GreaterThan, 20, "thirty");
        AssertKeys(index, SearchComparisonOperator.GreaterThanOrEqual, 20, "twenty", "thirty");
        AssertKeys(index, SearchComparisonOperator.LessThan, 20, "ten-a", "ten-b");
        AssertKeys(index, SearchComparisonOperator.LessThanOrEqual, 20, "ten-a", "ten-b", "twenty");
        Assert.IsFalse(index.UsesRadixStrategy);

        index.Remove(10, "ten-a");
        AssertKeys(index, SearchComparisonOperator.Equal, 10, "ten-b");
    }

    [TestMethod]
    public void AdaptiveRangeIndexMigratesAtMeasuredHysteresisBoundaries()
    {
        var index = new AdaptiveRangeSearchIndex<int>();
        for (var value = 0; value < AdaptiveRangeSearchIndex<int>.RadixMinimumDistinctValues; value++)
        {
            index.Add(value, $"row-{value}");
        }

        Assert.IsTrue(index.UsesRadixStrategy);
        Assert.AreEqual(4_096, index.DistinctValueCount);
        Assert.AreEqual(96, index.EstimateCount(SearchComparisonOperator.GreaterThanOrEqual, 4_000));
        AssertKeys(
            index,
            SearchComparisonOperator.GreaterThan,
            4_090,
            "row-4091",
            "row-4092",
            "row-4093",
            "row-4094",
            "row-4095");

        for (var value = 0; value < 3_072; value++)
        {
            index.Remove(value, $"row-{value}");
        }

        Assert.IsFalse(index.UsesRadixStrategy);
        Assert.AreEqual(1_024, index.DistinctValueCount);
        Assert.AreEqual(1_024, index.EstimateCount(SearchComparisonOperator.GreaterThanOrEqual, 3_072));
    }

    [TestMethod]
    public void InterfaceDbRadixCodecsPreserveClrRangeOrdering()
    {
        AssertRadixOrdering(
            SingleRadixKeyCodec.Instance,
            new[] { float.NaN, float.NegativeInfinity, -10.5f, -0f, 0f, 12.25f, float.PositiveInfinity });
        AssertRadixOrdering(
            DoubleRadixKeyCodec.Instance,
            new[] { double.NaN, double.NegativeInfinity, -10.5d, -0d, 0d, 12.25d, double.PositiveInfinity });
        AssertRadixOrdering(
            DecimalRadixKeyCodec.Instance,
            new[] { decimal.MinValue, -10.50m, -1m, -0.01m, 0m, 0.00m, 0.01m, 1.0m, 10.5m, decimal.MaxValue });
        AssertRadixOrdering(
            DateTimeRadixKeyCodec.Instance,
            new[] { DateTime.MinValue, new DateTime(2000, 1, 1), DateTime.MaxValue });
        AssertRadixOrdering(
            DateTimeOffsetRadixKeyCodec.Instance,
            new[]
            {
                DateTimeOffset.MinValue,
                new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DateTimeOffset.MaxValue
            });
        AssertRadixOrdering(
            TimeSpanRadixKeyCodec.Instance,
            new[] { TimeSpan.MinValue, TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.MaxValue });
    }

    private static void AssertKeys(
        AdaptiveRangeSearchIndex<int> index,
        SearchComparisonOperator comparisonOperator,
        int value,
        params string[] expected)
    {
        var actual = index.Search(comparisonOperator, value);
        Assert.AreEqual(expected.Length, index.EstimateCount(comparisonOperator, value));
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
    }

    private static void AssertRadixOrdering<T>(IRadixKeyCodec<T> codec, IReadOnlyList<T> values)
        where T : notnull
    {
        using var index = new RadixRangeIndexStrategy<T>(codec);
        for (var itemIndex = 0; itemIndex < values.Count; itemIndex++)
        {
            index.Add(values[itemIndex], $"row-{itemIndex}");
        }

        var comparer = Comparer<T>.Default;
        for (var pivotIndex = 0; pivotIndex < values.Count; pivotIndex++)
        {
            var pivot = values[pivotIndex];
            var expected = values
                .Select((value, itemIndex) => (value, itemIndex))
                .Where(item => comparer.Compare(item.value, pivot) >= 0)
                .Select(item => $"row-{item.itemIndex}")
                .ToArray();
            var actual = index.Search(new RangeQuery<T>(
                HasLowerBound: true,
                pivot,
                IncludeLowerBound: true,
                HasUpperBound: false,
                UpperBound: default!,
                IncludeUpperBound: false));

            CollectionAssert.AreEquivalent(expected, actual.ToArray());
        }
    }

    private static SearchIndexStore<PlannerRecord> CreatePlannerStore(
        int count,
        out PlannerRecord[] records)
        => CreatePlannerStore(count, SearchQueryPlannerOptions.MetadataOnly, out records);

    private static SearchIndexStore<PlannerRecord> CreatePlannerStore(
        int count,
        SearchQueryPlannerOptions plannerOptions,
        out PlannerRecord[] records)
    {
        var store = new SearchIndexStore<PlannerRecord>(plannerOptions);
        records = new PlannerRecord[count];
        for (var index = 0; index < count; index++)
        {
            var record = CreatePlannerRecord(index);
            records[index] = record;
            store.Upsert(record.Id, record);
        }

        return store;
    }

    private static PlannerRecord CreatePlannerRecord(int index)
        => new()
        {
            Id = $"row-{index}",
            Group = index % 2 == 0 ? "A" : "B",
            Age = index,
            Text = index % 257 == 0 ? $"entry-{index}-needle" : $"entry-{index}"
        };

    private static void AssertEquivalentToFullScan(
        SearchIndexStore<PlannerRecord> store,
        IReadOnlyList<PlannerRecord> records,
        Expression<Func<PlannerRecord, bool>> predicate)
    {
        var byId = records.ToDictionary(static record => record.Id, StringComparer.Ordinal);
        var compiled = predicate.Compile();
        var candidates = store.TryEvaluate(predicate);
        var actual = (candidates is null ? records : candidates.Select(key => byId[key]))
            .Where(compiled)
            .Select(static record => record.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var expected = records
            .Where(compiled)
            .Select(static record => record.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected, actual, predicate.ToString());
    }

    private static void RunConcurrentQueries(
        SearchIndexStore<PlannerRecord> store,
        System.Collections.Concurrent.ConcurrentQueue<Exception> failures)
    {
        try
        {
            for (var iteration = 0; iteration < 250; iteration++)
            {
                _ = store.TryEvaluate(
                    record => record.Group == "A" && record.Text.Contains("needle"));
            }
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception);
        }
    }

    [SearchIndex]
    private sealed class IndexedStringRecord
    {
        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix)]
        public string Value { get; init; } = string.Empty;
    }

    [SearchIndex]
    private sealed class PlannerRecord
    {
        [SearchIndex(SearchIndexMode.Exact)]
        public string Id { get; init; } = string.Empty;

        [SearchIndex(SearchIndexMode.Exact)]
        public string Group { get; init; } = string.Empty;

        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
        public int Age { get; init; }

        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix | SearchIndexMode.Suffix | SearchIndexMode.Contains)]
        public string Text { get; init; } = string.Empty;
    }
}
