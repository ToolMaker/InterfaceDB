using System.Collections.Concurrent;
using Idb.Diagnostics;
using Idb.Storage.AtlasCore;

[TestClass]
public sealed class AtlasTelemetryTests
{
    [TestMethod]
    public async Task AtlasStorageEmitsObservationsAndPreservesReopenValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-telemetry-" + Guid.NewGuid().ToString("N"));
        var telemetry = new Capture();
        try
        {
            await using (var store = await AtlasCoreStore.OpenAsync(root, new() { Telemetry = telemetry }))
            {
                await store.SaveAsync("private-key", new byte[] { 4, 5 });
                CollectionAssert.AreEqual(new byte[] { 4, 5 }, await store.GetAsync("private-key"));
                await store.CheckpointAsync();
            }
            await using (var reopened = await AtlasCoreStore.OpenAsync(root))
                CollectionAssert.AreEqual(new byte[] { 4, 5 }, await reopened.GetAsync("private-key"));
            foreach (var operation in new[] { DatabaseOperation.Initialize, DatabaseOperation.Save, DatabaseOperation.Read, DatabaseOperation.Checkpoint })
                Assert.IsTrue(telemetry.Records.Any(value => value.Component == DatabaseComponent.Atlas && value.Operation == operation
                    && value.Outcome == DatabaseOutcome.Success));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Capture : IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public ConcurrentQueue<DatabaseObservation> Records { get; } = new();
        public void TryRecord(in DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(DatabaseGauge gauge, long delta) { }
    }
}
