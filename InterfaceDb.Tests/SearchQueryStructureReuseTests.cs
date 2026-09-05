using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Indexes;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchQueryStructureReuseTests
{
    [TestMethod]
    public void ReusedStructureRefreshesBoundsCountsAndResultsAfterMutations()
    {
        using var store = CreateStore();
        var low = 10;
        var high = 20;
        Expression<Func<Row, bool>> query = row => low <= row.Value && row.Value < high;
        var syntax = SearchIndexStore<Row>.GetQueryStructureIdentity(query.Body);
        Assert.AreEqual(10, store.TryEvaluate(query)!.Count);
        low = 30;
        high = 33;
        CollectionAssert.AreEquivalent(new[] { "30", "31", "32" }, store.TryEvaluate(query)!.ToArray());
        Assert.AreEqual(3, store.ExplainPlan(query).EstimatedCandidateCount);
        store.Remove("31");
        store.Upsert("new", new Row { Value = 31 });
        store.Upsert("32", new Row { Value = 99 });
        CollectionAssert.AreEquivalent(new[] { "30", "new" }, store.TryEvaluate(query)!.ToArray());
        Assert.AreSame(syntax, SearchIndexStore<Row>.GetQueryStructureIdentity(query.Body));
        store.Clear();
        Assert.AreEqual(0, store.ExplainPlan(query).EstimatedCandidateCount);
        Assert.AreEqual(0, store.TryEvaluate(query)!.Count);
    }

    [TestMethod]
    public void ReusedSyntaxDoesNotBindAStoreOrPlannerOptions()
    {
        using var first = new SearchIndexStore<Row>(SearchQueryPlannerOptions.StrongObject);
        using var second = new SearchIndexStore<Row>(SearchQueryPlannerOptions.MetadataOnly);
        Expression<Func<Row, bool>> query = row => row.Value >= 1 && row.Value <= 5;
        first.Upsert("first", new Row { Value = 3 });
        second.Upsert("second", new Row { Value = 4 });
        CollectionAssert.AreEqual(new[] { "first" }, first.TryEvaluate(query)!.ToArray());
        CollectionAssert.AreEqual(new[] { "second" }, second.TryEvaluate(query)!.ToArray());
    }

    [TestMethod]
    public void NestedAndOrAndMultipleBoundsRemainEquivalentToFullPredicate()
    {
        using var store = CreateStore();
        var low = 5;
        var high = 30;
        Expression<Func<Row, bool>> query = row =>
            (row.Value >= low && high > row.Value && row.Value >= 12)
            || (row.Value > 40 && row.Value < 44);
        for (var iteration = 0; iteration < 10; iteration++)
        {
            AssertMatches(store, query);
            low += 3;
            high += 4;
        }
    }

    [TestMethod]
    public void ComparisonModeAndUnsupportedBranchesAreNotFrozen()
    {
        using var store = CreateStore();
        var mode = StringComparison.Ordinal;
        Expression<Func<Row, bool>> query = row =>
            row.Text.StartsWith("a", mode) || row.Value == 99;
        Assert.IsNotNull(store.TryEvaluate(query));
        AssertMatches(store, query);
        mode = StringComparison.OrdinalIgnoreCase;
        Assert.IsNull(store.TryEvaluate(query), "An unindexed OR branch must force a full scan.");
        AssertMatches(store, query);
        mode = StringComparison.Ordinal;
        Assert.IsNotNull(store.TryEvaluate(query));
    }

    [TestMethod]
    public void LargeConjunctionUsesBoundedPairCacheFallback()
    {
        using var store = CreateStore();
        var parameter = Expression.Parameter(typeof(Row), "row");
        var value = Expression.Property(parameter, nameof(Row.Value));
        Expression body = Expression.LessThan(value, Expression.Constant(75));
        for (var lower = 0; lower < 70; lower++)
        {
            body = Expression.AndAlso(body, Expression.GreaterThanOrEqual(value, Expression.Constant(lower)));
        }

        var query = Expression.Lambda<Func<Row, bool>>(body, parameter);
        AssertMatches(store, query);
        AssertMatches(store, query);
    }

    [TestMethod]
    public void StaticMemberBoundsAreReadOnEveryExecution()
    {
        using var store = CreateStore();
        Expression<Func<Row, bool>> query = row => row.Value >= StaticBounds.Low && row.Value < StaticBounds.High;
        StaticBounds.Low = 4;
        StaticBounds.High = 7;
        AssertMatches(store, query);
        StaticBounds.Low = 21;
        StaticBounds.High = 26;
        AssertMatches(store, query);
    }

    [TestMethod]
    public void WeakSyntaxCacheDoesNotKeepAnExpressionOrClosureAlive()
    {
        var references = PopulateWeakCache();
        for (var attempt = 0; attempt < 10 && (references.Expression.IsAlive || references.Closure.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.IsFalse(references.Expression.IsAlive);
        Assert.IsFalse(references.Closure.IsAlive);
    }

    [TestMethod]
    public async Task ConcurrentQueriesReuseSyntaxWithoutSharingMutablePlans()
    {
        using var store = CreateStore();
        Expression<Func<Row, bool>> query = row => row.Value >= 10 && row.Value < 30;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 50; iteration++)
            {
                Assert.AreEqual(20, store.TryEvaluate(query)!.Count);
            }
        })));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Expression, WeakReference Closure) PopulateWeakCache()
    {
        var bounds = new Bounds { Low = 1, High = 5 };
        Expression<Func<Row, bool>> query = row => row.Value >= bounds.Low && row.Value < bounds.High;
        using var store = CreateStore();
        _ = store.TryEvaluate(query);
        return (new WeakReference(query.Body), new WeakReference(bounds));
    }

    private static SearchIndexStore<Row> CreateStore()
    {
        var store = new SearchIndexStore<Row>();
        for (var i = 0; i < 100; i++)
        {
            store.Upsert(i.ToString(), Record(i));
        }

        return store;
    }

    private static Row Record(int value) => new() { Value = value, Text = value % 2 == 0 ? "alpha" : "ALPHA" };

    private static void AssertMatches(SearchIndexStore<Row> store, Expression<Func<Row, bool>> query)
    {
        var predicate = query.Compile();
        var candidates = store.TryEvaluate(query);
        var actual = (candidates ?? Enumerable.Range(0, 100).Select(i => i.ToString()).ToHashSet())
            .Where(key => predicate(Record(int.Parse(key)))).Order().ToArray();
        var expected = Enumerable.Range(0, 100).Where(i => predicate(Record(i))).Select(i => i.ToString()).Order().ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    private sealed class Bounds { public int Low; public int High; }
    private static class StaticBounds { public static int Low; public static int High; }

    [SearchIndex]
    private sealed class Row
    {
        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
        public int Value { get; init; }
        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Prefix)]
        public string Text { get; init; } = "";
    }
}
