using System.Globalization;
using System.Linq.Expressions;
using Idb.Libraries.Implementation.Indexes;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchPredicateCompilerTests
{
    [TestMethod]
    public void FreshExpressionsReuseStructureWithoutSharingConstants()
    {
        var before = SearchPredicateCompiler<LiteralRecord>.TemplateCompilationCount;
        var first = SearchPredicateCompiler<LiteralRecord>.Compile(CreateLiteralPredicate<LiteralRecord>(3));
        var second = SearchPredicateCompiler<LiteralRecord>.Compile(CreateLiteralPredicate<LiteralRecord>(7));

        Assert.IsTrue(first(new LiteralRecord { Value = 4 }));
        Assert.IsFalse(second(new LiteralRecord { Value = 4 }));
        Assert.AreEqual(before + 1, SearchPredicateCompiler<LiteralRecord>.TemplateCompilationCount);
    }

    [TestMethod]
    public void CapturedInstancesStayIndependentAndObserveLaterFieldChanges()
    {
        var first = new Threshold { Value = 3 };
        var second = new Threshold { Value = 7 };
        var firstPredicate = SearchPredicateCompiler<QueryRecord>.Compile(CreateCapturedPredicate(first));
        var secondPredicate = SearchPredicateCompiler<QueryRecord>.Compile(CreateCapturedPredicate(second));
        var record = new QueryRecord { Value = 5 };

        Assert.IsTrue(firstPredicate(record));
        Assert.IsFalse(secondPredicate(record));
        first.Value = 9;
        second.Value = 2;
        Assert.IsFalse(firstPredicate(record));
        Assert.IsTrue(secondPredicate(record));
        Assert.AreEqual(2, first.ReadCount);
        Assert.AreEqual(2, second.ReadCount);
    }

    [TestMethod]
    public void SameExpressionReturnsSameBoundPredicate()
    {
        Expression<Func<QueryRecord, bool>> expression = row => row.Value > 5;
        Assert.AreSame(SearchPredicateCompiler<QueryRecord>.Compile(expression),
            SearchPredicateCompiler<QueryRecord>.Compile(expression));
    }

    [TestMethod]
    public void CapturedCollectionChangesAndReferenceEqualityAreNotFrozen()
    {
        var selected = new List<int> { 1 };
        var expected = new object();
        Expression<Func<QueryRecord, bool>> expression = row =>
            selected.Contains(row.Value) && ReferenceEquals(row.Reference, expected);
        var predicate = SearchPredicateCompiler<QueryRecord>.Compile(expression);
        var row = new QueryRecord { Value = 2, Reference = expected };
        Assert.IsFalse(predicate(row));
        selected.Add(2);
        Assert.IsTrue(predicate(row));
        expected = new object();
        Assert.IsFalse(predicate(row));
    }

    [TestMethod]
    public void MethodOverloadsOperatorsAndNullBehaviorRemainDistinct()
    {
        var ordinal = SearchPredicateCompiler<QueryRecord>.Compile(row => row.Text!.StartsWith("a", StringComparison.Ordinal));
        var ignoreCase = SearchPredicateCompiler<QueryRecord>.Compile(row => row.Text!.StartsWith("a", StringComparison.OrdinalIgnoreCase));
        var equal = SearchPredicateCompiler<QueryRecord>.Compile(row => row.Value == 3);
        var different = SearchPredicateCompiler<QueryRecord>.Compile(row => row.Value != 3);
        var row = new QueryRecord { Value = 3, Text = "Alpha" };
        Assert.IsFalse(ordinal(row));
        Assert.IsTrue(ignoreCase(row));
        Assert.IsTrue(equal(row));
        Assert.IsFalse(different(row));
        Assert.ThrowsExactly<NullReferenceException>(() => ordinal(new QueryRecord()));
    }

    [TestMethod]
    public void CurrentCultureIsReadAtExecutionTime()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            Expression<Func<QueryRecord, bool>> expression = row => row.Text!.ToUpper() == "I";
            var reference = expression.Compile();
            var predicate = SearchPredicateCompiler<QueryRecord>.Compile(expression);
            var row = new QueryRecord { Text = "i" };
            foreach (var name in new[] { "en-US", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Assert.AreEqual(reference(row), predicate(row), name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void NestedLambdaAndQuotedExpressionFallbackPreservesScope()
    {
        var threshold = 4;
        Expression<Func<QueryRecord, bool>> nested = row => row.Numbers.Any(value => value > threshold);
        Expression<Func<QueryRecord, bool>> quoted = row => row.Numbers.AsQueryable().Any(value => value == threshold);
        var predicate = SearchPredicateCompiler<QueryRecord>.Compile(nested);
        var quotedPredicate = SearchPredicateCompiler<QueryRecord>.Compile(quoted);
        var row = new QueryRecord { Numbers = new[] { 5, 6 } };
        Assert.IsTrue(predicate(row));
        Assert.IsFalse(quotedPredicate(row));
        threshold = 6;
        Assert.IsFalse(predicate(row));
        Assert.IsTrue(quotedPredicate(row));
    }

    [TestMethod]
    public void NullableConversionsConditionalAndStaticMembersMatchOrdinaryCompilation()
    {
        Expression<Func<QueryRecord, bool>> expression = row =>
            (row.Optional.HasValue ? (long)row.Optional.Value : 17L) > DateTime.UnixEpoch.Second;
        var reference = expression.Compile();
        var actual = SearchPredicateCompiler<QueryRecord>.Compile(expression);
        foreach (var optional in new int?[] { null, -1, 0, 15 })
        {
            var row = new QueryRecord { Optional = optional };
            Assert.AreEqual(reference(row), actual(row));
        }
    }

    [TestMethod]
    public void TemplateAdmissionIsBoundedAndEvictionDoesNotBreakExistingPredicates()
    {
        var retained = new List<Func<BoundedRecord, bool>>();
        for (var shape = 0; shape < 512; shape++)
        {
            var row = Expression.Parameter(typeof(BoundedRecord), "row");
            Expression value = Expression.Property(row, nameof(BoundedRecord.Value));
            for (var bit = 0; bit < 9; bit++)
            {
                value = ((shape >> bit) & 1) == 0
                    ? Expression.Add(value, Expression.Constant(1))
                    : Expression.Subtract(value, Expression.Constant(1));
            }

            retained.Add(SearchPredicateCompiler<BoundedRecord>.Compile(
                Expression.Lambda<Func<BoundedRecord, bool>>(
                    Expression.GreaterThan(value, Expression.Constant(0)), row)));
        }

        Assert.AreEqual(SearchPredicateCompiler<BoundedRecord>.MaximumTemplates,
            SearchPredicateCompiler<BoundedRecord>.CachedTemplateCount);
        Assert.IsTrue(retained[0](new BoundedRecord()));
        Assert.IsFalse(retained[^1](new BoundedRecord()));
    }

    [TestMethod]
    public void ConcurrentFreshQueriesCannotShareTheirArguments()
    {
        Parallel.For(0, 100, threshold =>
        {
            var predicate = SearchPredicateCompiler<ConcurrentRecord>.Compile(
                CreateLiteralPredicate<ConcurrentRecord>(threshold));
            Assert.IsTrue(predicate(new ConcurrentRecord { Value = threshold + 1 }));
            Assert.IsFalse(predicate(new ConcurrentRecord { Value = threshold }));
        });
        Assert.AreEqual(1, SearchPredicateCompiler<ConcurrentRecord>.TemplateCompilationCount);
    }

    private static Expression<Func<T, bool>> CreateLiteralPredicate<T>(int value) where T : QueryRecord
    {
        var row = Expression.Parameter(typeof(T), "row");
        return Expression.Lambda<Func<T, bool>>(
            Expression.GreaterThan(Expression.Property(row, nameof(QueryRecord.Value)), Expression.Constant(value)), row);
    }

    private static Expression<Func<QueryRecord, bool>> CreateCapturedPredicate(Threshold threshold)
        => row => row.Value > threshold.Current;

    private sealed class Threshold
    {
        public int Value;
        public int ReadCount;
        public int Current { get { ReadCount++; return Value; } }
    }

    private class QueryRecord
    {
        public int Value { get; init; }
        public int? Optional { get; init; }
        public string? Text { get; init; }
        public object? Reference { get; init; }
        public int[] Numbers { get; init; } = [];
    }

    private sealed class LiteralRecord : QueryRecord;
    private sealed class ConcurrentRecord : QueryRecord;
    private sealed class BoundedRecord : QueryRecord;
}
