namespace Idb.Storage.AtlasCore.Tests;

using Idb.Storage.AtlasCore.Internal;

[TestClass]
public sealed class TransactionAdmissionTests
{
    [TestMethod]
    public void TransactionAdmissionRejectsPayloadAbovePhysicalFrameWithoutAllocatingIt()
    {
        const string key = "oversized-physical-frame";
        var payloadLength = AtlasGroupLogFormat.MaximumFrameBytes;
        var walEntryLength = AtlasTransactionLog.CalculateWalEntryLength(key, payloadLength);
        Assert.IsTrue(
            walEntryLength < new AtlasCoreOptions().MaximumTransactionLogRecordBytes,
            "The fixture must fit below the default transaction-record cap.");

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            AtlasTransactionLog.CalculateTransactionEntryLength(
                AtlasGroupRecordKind.Put,
                key,
                payloadLength));
    }

    [TestMethod]
    public async Task ParticipantAggregateAdmissionRejectsBeforeWritesAndStoreRemainsUsable()
    {
        using var root = new HybridPartitionTests.Temp();
        var options = HybridPartitionTests.Options() with
        {
            TransactionProtocol = AtlasCoreTransactionProtocol.ParticipantCommitJournal,
            MaximumTransactionLogRecordBytes = 1024 * 1024
        };
        await using var store = await AtlasCoreStore.OpenAsync(root.Path, options);
        await using var transaction = await store.BeginTransactionAsync();
        await transaction.SaveAsync("first", new byte[600 * 1024]);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => transaction.SaveAsync("second", new byte[600 * 1024]).AsTask());
        Assert.AreEqual(1, transaction.PendingMutationCount);
        Assert.AreEqual(AtlasCoreTransactionState.Active, transaction.State);
        await transaction.RollbackAsync();

        var bypassedStaging = new[]
        {
            new AtlasCorePendingMutation(
                "bypassed-first",
                AtlasGroupRecordKind.Put,
                new byte[600 * 1024]),
            new AtlasCorePendingMutation(
                "bypassed-second",
                AtlasGroupRecordKind.Put,
                new byte[600 * 1024])
        };
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            store.CommitTransactionAsync(
                Guid.NewGuid(),
                bypassedStaging,
                CancellationToken.None).AsTask());

        Assert.IsNull(await store.GetAsync("first"));
        Assert.IsNull(await store.GetAsync("second"));
        Assert.IsNull(await store.GetAsync("bypassed-first"));
        Assert.IsNull(await store.GetAsync("bypassed-second"));
        await store.SaveAsync("after", new byte[] { 42 });
        CollectionAssert.AreEqual(new byte[] { 42 }, await store.GetAsync("after"));
    }
}
