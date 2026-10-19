using System.Collections.Concurrent;
using Idb.Diagnostics;
using Idb.Libraries.Abstractions.Enums;

namespace InterfaceDb.Tests;

[TestClass]
public sealed class RepositoryTelemetryTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task QueryAndBackgroundCallbackFailuresAreObservedWithoutUndoingSavedData(bool segment)
    {
        var telemetry = new Capture();
        await using var fixture = new RepositoryNotificationTests.Fixture(segment, telemetry: telemetry);
        using var subscriber = fixture.Notifications.SubscribeCreated(_ => throw new InvalidOperationException("private"));
        await fixture.Repository.SaveAsync(new RepositoryNotificationTests.Record { Id = "one", Value = 17 });
        Assert.AreEqual(Idb.Libraries.Abstractions.Services.Repository.RepositorySubscriptionEndReason.CallbackFailed,
            await subscriber.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(17, (await fixture.Repository.QuerySingleAsync(_ => true)).Value);
        var original = new InvalidOperationException("private-query");
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in fixture.Repository.QueryManyAsync(_ => throw original)) { }
        });
        Assert.AreSame(original, actual);
        foreach (var operation in new[] { DatabaseOperation.Publish, DatabaseOperation.Query })
            Assert.IsTrue(telemetry.Records.Any(record => record.Operation == operation
                && record.Outcome == DatabaseOutcome.Failure && record.Log), operation.ToString());
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task FileAndSegmentRepositoryUseTheConfiguredTelemetryWithoutChangingDurableValues(bool segment)
    {
        var telemetry = new Capture();
        await using var fixture = new RepositoryNotificationTests.Fixture(segment, StorageObjectCacheMode.MetadataOnly,
            enabled: false, durable: true, telemetry: telemetry);
        await fixture.Repository.InitializeAsync();
        var saved = await fixture.Repository.SaveAsync(new RepositoryNotificationTests.Record { Id = "private", Value = 17 });
        Assert.AreEqual(17, (await fixture.Repository.QuerySingleAsync(_ => true)).Value);
        Assert.IsTrue(await fixture.Repository.DeleteAsync(saved));
        Assert.IsTrue(telemetry.Records.Any(record => record.Component == DatabaseComponent.Repository && record.Operation == DatabaseOperation.Save));
        var component = segment ? DatabaseComponent.SegmentCore : DatabaseComponent.File;
        Assert.IsTrue(telemetry.Records.Any(record => record.Component == component && record.Operation == DatabaseOperation.Read));
        Assert.IsTrue(telemetry.Records.All(record => record.Outcome is DatabaseOutcome.Success or DatabaseOutcome.Incomplete));
    }
    private sealed class Capture : IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public ConcurrentQueue<DatabaseObservation> Records { get; } = new();
        public void TryRecord(in DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(DatabaseGauge gauge, long delta) { }
    }
}
