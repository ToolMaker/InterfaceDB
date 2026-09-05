using Idb.Libraries.Implementation.Serialization;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class OrderedReadBatchTests
{
    [TestMethod]
    public void DuplicateResolutionPrecedesMissingSlotCompaction()
    {
        var first = new object();
        var second = new object();
        var batch = new OrderedReadBatch<object>(["missing", "a", "b", "a", "missing", "b"], default);
        Assert.HasCount(3, batch.UniqueKeys);
        batch.Set("b", second); // Physical storage can complete out of logical order.
        batch.Set("a", first);

        var result = batch.Complete(default);

        CollectionAssert.AreEqual(new[] { first, second, first, second }, result.ToArray());
    }

    [TestMethod]
    public void ResultsMatchReferenceForMixedDuplicatesMissingKeysAndReversedCompletions()
    {
        var random = new Random(7351);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var keys = Enumerable.Range(0, random.Next(0, 130)).Select(_ => $"key-{random.Next(0, 21)}").ToArray();
            var available = keys.Distinct(StringComparer.Ordinal).Where((_, index) => index % 3 != 0)
                .ToDictionary(key => key, _ => new object(), StringComparer.Ordinal);
            var batch = new OrderedReadBatch<object>(keys, default);
            foreach (var entry in available.Reverse()) { batch.Set(entry.Key, entry.Value); }
            var result = batch.Complete(default);
            var expected = keys.Where(available.ContainsKey).Select(key => available[key]).ToArray();
            CollectionAssert.AreEqual(expected, result.ToArray());
            if (result is ArraySegment<object> segment)
            {
                Assert.IsTrue(segment.Array!.Skip(segment.Count).All(value => value is null),
                    "Compaction must not retain extra object references beyond the returned slice.");
            }
        }
    }

    [TestMethod]
    public void FullyPresentBatchReturnsItsSingleResultArrayAndMissingBatchReturnsSharedEmpty()
    {
        var value = new object();
        var populated = new OrderedReadBatch<object>(["a", "b"], default);
        foreach (var entry in populated.UniqueKeys) { populated.Set(entry.Value, value); }
        Assert.IsInstanceOfType<object[]>(populated.Complete(default));
        var missing = new OrderedReadBatch<object>(["missing", "missing"], default);
        Assert.AreSame(Array.Empty<object>(), missing.Complete(default));
    }

    [TestMethod]
    public void InvalidKeysAndCancellationAreRejectedIncludingEmptyInput()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new OrderedReadBatch<object>(["valid", " "], default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => new OrderedReadBatch<object>([], cancelled.Token));
        var batch = new OrderedReadBatch<object>([], default);
        Assert.ThrowsExactly<OperationCanceledException>(() => batch.Complete(cancelled.Token));
    }
}
