using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Implementation.Indexes;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class SearchIndexUpdateOptimizationTests
{
    [TestMethod]
    public void UpsertTouchesOnlyChangedIndexedValues()
    {
        using var store = new SearchIndexStore<IndexedRecord>();
        var row = new IndexedRecord { Text = "alpha", SecondText = "shared", Number = 1, Status = RecordStatus.Active };
        using (store.BeginBulkLoad())
        {
            store.Upsert("first", row);
            store.Upsert("second", new IndexedRecord { Text = "alpha", SecondText = "shared", Number = 1 });
        }
        var initialMutations = PendingContainsMutations(store);

        row.Text = new string("alpha".ToCharArray());
        row.Unindexed = "new unindexed value";
        store.Upsert("first", row);
        Assert.AreEqual(initialMutations, PendingContainsMutations(store));

        row.Number = 2;
        row.Status = RecordStatus.Deleted;
        store.Upsert("first", row);
        Assert.AreEqual(initialMutations, PendingContainsMutations(store));
        CollectionAssert.AreEquivalent(new[] { "second" }, store.TryEvaluate(item => item.Number == 1)!.ToArray());
        CollectionAssert.AreEquivalent(new[] { "first" }, store.TryEvaluate(item => item.Status == RecordStatus.Deleted)!.ToArray());

        row.Text = "beta";
        store.Upsert("first", row);
        Assert.AreEqual(initialMutations + 2, PendingContainsMutations(store));
        CollectionAssert.AreEquivalent(new[] { "second" }, store.TryEvaluate(item => item.Text == "alpha")!.ToArray());
        CollectionAssert.AreEquivalent(new[] { "first" }, store.TryEvaluate(item => item.Text!.Contains("bet"))!.ToArray());

        row.Text = null;
        store.Upsert("first", row);
        Assert.IsEmpty(store.TryEvaluate(item => item.Text == "beta")!);
        store.Remove("second");
        Assert.IsEmpty(store.TryEvaluate(item => item.Text == "alpha")!);
        Assert.AreEqual(1, store.IndexedObjectCount);
    }

    [TestMethod]
    public void PersistedValueImportAndSnapshotsRetainDefensiveOwnership()
    {
        using var source = new SearchIndexStore<IndexedRecord>();
        using var target = new SearchIndexStore<IndexedRecord>();
        var values = source.ExtractValues(new IndexedRecord { Text = "before", Number = 3 });
        target.UpsertValues("row", values);
        Array.Fill(values, null);
        Assert.HasCount(1, target.TryEvaluate(item => item.Text == "before")!);

        var snapshot = target.GetValuesSnapshot();
        Array.Fill(snapshot["row"], null);
        var single = target.GetValues("row")!;
        Array.Fill(single, null);
        target.Upsert("row", new IndexedRecord { Text = "after", Number = 4 });
        Assert.IsEmpty(target.TryEvaluate(item => item.Text == "before")!);
        Assert.HasCount(1, target.TryEvaluate(item => item.Text == "after")!);
    }

    [TestMethod]
    public void NestedTypedGetterEvaluatesOnceAndHandlesNull()
    {
        using var store = new SearchIndexStore<NestedRecord>();
        var row = new NestedRecord { ChildValue = new Child { Score = 7 } };
        store.Upsert("row", row);
        Assert.AreEqual(1, row.ReadCount);
        Assert.HasCount(1, store.TryEvaluate(item => item.Child!.Score == 7)!);

        row.ChildValue = null;
        store.Upsert("row", row);
        Assert.AreEqual(2, row.ReadCount);
        Assert.IsEmpty(store.TryEvaluate(item => item.Child!.Score == 7)!);
        Assert.AreSame(SearchIndexDiscovery.Discover(typeof(NestedRecord)), SearchIndexDiscovery.Discover(typeof(NestedRecord)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GetterDynamicAndInterpreterPathsPreserveNullableFieldsAndExceptions(bool interpreted)
    {
        var getter = SearchIndexDiscovery.CreateGetter(new MemberInfo[]
        {
            typeof(NullableRecord).GetField(nameof(NullableRecord.Child))!,
            typeof(ChildValue?).GetProperty(nameof(Nullable<ChildValue>.Value))!,
            typeof(ChildValue).GetProperty(nameof(ChildValue.Score))!
        }, interpreted);
        var row = new NullableRecord();
        Assert.IsNull(getter(row));
        row.Child = new ChildValue { Score = 42 };
        Assert.AreEqual(42, getter(row));

        var throwing = SearchIndexDiscovery.CreateGetter(new[]
        {
            typeof(ThrowingRecord).GetProperty(nameof(ThrowingRecord.Value))!
        }, interpreted);
        var exception = Assert.ThrowsExactly<TargetInvocationException>(() => throwing(new ThrowingRecord()));
        Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
    }

    private static int PendingContainsMutations<T>(SearchIndexStore<T> store) where T : class
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var indexes = (Array)typeof(SearchIndexStore<T>).GetField("_indexesByDefinition", flags)!.GetValue(store)!;
        var result = 0;
        foreach (var index in indexes)
        {
            var contains = (AdaptiveContainsSearchIndex?)index!.GetType().GetField("_contains", flags)!.GetValue(index);
            result += contains?.PendingMutationCount ?? 0;
        }

        return result;
    }

    private enum RecordStatus { Active, Deleted }

    private sealed class IndexedRecord
    {
        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Contains)]
        public string? Text { get; set; }
        [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Contains)]
        public string? SecondText { get; set; }
        [SearchIndex]
        public int Number { get; set; }
        [SearchIndex]
        public RecordStatus Status { get; set; }
        public string? Unindexed { get; set; }
    }

    private sealed class NestedRecord
    {
        public Child? ChildValue;
        public int ReadCount;
        [SearchIndex]
        public Child? Child { get { ReadCount++; return ChildValue; } }
    }

    private sealed class Child { public int Score { get; set; } }
    private struct ChildValue { public int Score { get; set; } }
    private sealed class NullableRecord { public ChildValue? Child; }
    private sealed class ThrowingRecord { public int Value => throw new InvalidOperationException("getter"); }
}
