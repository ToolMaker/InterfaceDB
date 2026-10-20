using System.Linq.Expressions;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Implementation.Indexes;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchIndexPromotionTests
{
    [SearchIndex]
    private sealed class Row
    {
        public long Long { get; init; }
        public int Int { get; init; }
        public byte Byte { get; init; }
        public short Short { get; init; }
        public double Double { get; init; }
        public char Char { get; init; }
        public ByteEnum Enum { get; init; }
        public int? Nullable { get; init; }
    }
    private enum ByteEnum : byte { One = 1, Two = 2 }

    [TestMethod]
    public void ExactIntegralPromotionsUseIndexesIncludingReversedAndBoundedRanges()
    {
        using var index = new SearchIndexStore<Row>();
        for (var i = 0; i < 200; i++)
            index.Upsert(i.ToString(), new Row { Byte = (byte)i, Short = (short)i, Int = i,
                Char = (char)i, Enum = (ByteEnum)(byte)i, Nullable = i });
        int value = 7;
        long longValue = 7;
        Expression<Func<Row, bool>>[] predicates =
        [
            r => r.Byte == value, r => r.Short == value, r => r.Char == (char)7,
            r => r.Enum == (ByteEnum)7, r => r.Int == longValue, r => r.Nullable == longValue,
            r => value == r.Byte, r => r.Byte > 6 && r.Byte < 8,
            r => 6 < r.Short && 8 > r.Short, r => r.Char >= (char)7 && r.Char <= (char)7
        ];
        foreach (var predicate in predicates)
        {
            Assert.IsTrue(index.ExplainPlan(predicate).UsesIndexes, predicate.ToString());
            CollectionAssert.AreEquivalent(new[] { "7" }, index.TryEvaluate(predicate)!.ToArray(), predicate.ToString());
        }
    }

    [TestMethod]
    public void UnsafeOrUnrepresentableIntegralBoundsKeepScanSemantics()
    {
        using var index = new SearchIndexStore<Row>();
        index.Upsert("row", new Row { Int = 257, Byte = 1, Long = long.MaxValue });
        int high = 256, low = -1;
        Expression<Func<Row, bool>>[] predicates =
        [
            r => r.Byte == high, r => r.Byte < low, r => r.Byte > 1.5,
            r => (byte)r.Int == 1, r => checked((byte)r.Int) == 1,
            r => (double)r.Long == (double)long.MaxValue, r => (object)r.Int == (object)257,
            r => (int)r.Nullable! == 1
        ];
        foreach (var predicate in predicates)
            Assert.IsNull(index.TryEvaluate(predicate), predicate.ToString());
    }

    [SearchIndex]
    private class BaseRow { public int Value { get; init; } }
    private sealed class DerivedRow : BaseRow { public new int Value { get; init; } }

    [TestMethod]
    public void ReceiverCastCannotUseAnIndexForADifferentHiddenProperty()
    {
        using var index = new SearchIndexStore<BaseRow>();
        index.Upsert("row", new DerivedRow { Value = 7 });
        Assert.IsNull(index.TryEvaluate(row => ((DerivedRow)row).Value == 7));
    }

    [TestMethod]
    public void NumericPromotionsNeverExcludeMatchingRowsOrThrowDuringPlanning()
    {
        var rows = new[]
        {
            new Row { Long = 1, Int = 1, Byte = 1, Short = 1, Double = 1, Char = 'A', Enum = ByteEnum.One, Nullable = 1 },
            new Row { Long = 2, Int = 2, Byte = 2, Short = 2, Double = 2, Char = 'B', Enum = ByteEnum.Two },
            new Row { Long = long.MaxValue, Int = int.MaxValue, Byte = byte.MaxValue, Short = short.MaxValue, Double = double.NaN, Char = char.MaxValue }
        };
        using var index = new SearchIndexStore<Row>();
        for (var i = 0; i < rows.Length; i++) index.Upsert(i.ToString(), rows[i]);
        int one = 1;
        long longOne = 1;
        int beyondByte = 256;
        Expression<Func<Row, bool>>[] predicates =
        [
            r => r.Long == one, r => r.Long > one, r => r.Int == longOne,
            r => r.Int > 1.5, r => r.Int < 2.5, r => r.Int > 1.5 && r.Int < 2.5,
            r => r.Byte == one, r => r.Byte > one, r => r.Byte < beyondByte,
            r => r.Short == one, r => r.Double > one, r => r.Char == 'A',
            r => r.Enum == ByteEnum.One, r => r.Nullable == one,
            r => (byte)r.Int == 255, r => (double)r.Long == (double)long.MaxValue,
            r => (object)r.Int == (object)one
        ];
        foreach (var predicate in predicates)
        {
            var compiled = predicate.Compile();
            var expected = rows.Where(compiled).ToArray();
            // Null is the planner's explicit request for a scan, exercised through the
            // real repository in ReviewRegression_SearchVersusScan_TypeConversions.
            var candidates = index.TryEvaluate(predicate);
            var actual = candidates is null ? rows.Where(compiled).ToArray()
                : candidates.Select(key => rows[int.Parse(key)]).Where(compiled).ToArray();
            CollectionAssert.AreEquivalent(expected, actual, predicate.ToString());
        }
    }
}
