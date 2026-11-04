using Idb.Commercial.Repositories;

namespace InterfaceDB.Commercial.Repositories.Tests;

[TestClass]
public sealed class SynchronizationStateTests
{
    [TestMethod]
    public void MonotonicRevisionTimestampGenerator_AdvancesAcrossSameTickAndClockRollback()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var generator = new MonotonicRevisionTimestampGenerator(timeProvider);

        var first = generator.Next(0);
        var second = generator.Next(0);
        timeProvider.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(500);
        var afterRollback = generator.Next(0);

        Assert.AreEqual(1_000_000, first);
        Assert.AreEqual(first + 1, second);
        Assert.AreEqual(second + 1, afterRollback);
    }

    [TestMethod]
    public void MonotonicRevisionTimestampGenerator_RejectsNegativeAndOverflowingObservedValues()
    {
        var generator = new MonotonicRevisionTimestampGenerator(
            new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => generator.Next(-1));
        Assert.ThrowsExactly<OverflowException>(() => generator.Next(long.MaxValue));
    }

    [TestMethod]
    public async Task ChangeHints_AdvanceObservedButNotAppliedCheckpoint()
    {
        var stateStore = new InMemoryResilientSynchronizationStateStore();

        await stateStore.MarkChangedAsync(new RepositoryChangeSignal("dataset", "one", 100, 9));
        await stateStore.MarkStorageKeySynchronizedAsync("dataset", "one", 9);
        var afterHintRefresh = await stateStore.GetAsync("dataset");

        Assert.AreEqual(9, afterHintRefresh.LatestObservedCheckpoint);
        Assert.AreEqual(0, afterHintRefresh.LastAppliedCheckpoint);
        Assert.AreEqual(0, afterHintRefresh.DirtyStorageKeys.Count);

        await stateStore.AdvanceAppliedCheckpointAsync("dataset", 9);
        var afterAppliedPage = await stateStore.GetAsync("dataset");
        Assert.AreEqual(9, afterAppliedPage.LatestObservedCheckpoint);
        Assert.AreEqual(9, afterAppliedPage.LastAppliedCheckpoint);
    }

    [TestMethod]
    public async Task DirtyKeyLimit_CollapsesToBoundedDatasetInvalidation()
    {
        var stateStore = new InMemoryResilientSynchronizationStateStore(maximumDirtyStorageKeys: 2);
        await stateStore.MarkChangedAsync(new RepositoryChangeSignal("dataset", "one", 1, 10));
        await stateStore.MarkChangedAsync(new RepositoryChangeSignal("dataset", "two", 2, 11));
        await stateStore.MarkChangedAsync(new RepositoryChangeSignal("dataset", "three", 3, 12));

        var overflowed = await stateStore.GetAsync("dataset");
        Assert.AreEqual(12, overflowed.LatestObservedCheckpoint);
        Assert.AreEqual(0, overflowed.LastAppliedCheckpoint);
        Assert.AreEqual(12, overflowed.DatasetDirtyThroughCheckpoint);
        Assert.AreEqual(0, overflowed.DirtyStorageKeys.Count);

        await stateStore.AdvanceAppliedCheckpointAsync("dataset", 11);
        var partiallyApplied = await stateStore.GetAsync("dataset");
        Assert.AreEqual(12, partiallyApplied.DatasetDirtyThroughCheckpoint);

        await stateStore.AdvanceAppliedCheckpointAsync("dataset", 12);
        var fullyApplied = await stateStore.GetAsync("dataset");
        Assert.IsFalse(fullyApplied.IsDatasetDirty);
        Assert.AreEqual(12, fullyApplied.LastAppliedCheckpoint);
    }
}
