using System.Collections.Concurrent;
using Idb.Diagnostics;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Idb.Storage.AtlasCore.Tests;

public sealed partial class AtlasCoreProviderTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LegacyCallbackFailureIsObservedWithoutLosingOtherSubscribers(bool keepAliveFailure)
    {
        using var root = new ProviderTemporaryDirectory();
        var telemetry = new FollowupCapture();
        var config = new ProviderConfig(root.Path, StoragePolicy.Default with { Telemetry = telemetry, RaiseNotifications = true });
        await using var provider = BuildProvider(config, ProviderOptions());
        var repository = provider.GetRequiredService<IRepository<IAtlasProviderRecord>>();
        await repository.InitializeAsync();
        var envelopes = provider.GetRequiredService<IEnvelopeRepository<IAtlasProviderRecord>>();
        var delivered = 0;
        using var failed = envelopes.SubscribeToStored(
            _ => keepAliveFailure ? ValueTask.CompletedTask : throw new InvalidOperationException("callback"),
            keepAliveFailure ? () => throw new InvalidOperationException("keepalive") : null);
        using var good = envelopes.SubscribeToStored(_ => { delivered++; return ValueTask.CompletedTask; });
        await repository.SaveAsync(new AtlasProviderRecord { Id = "first", Score = 1 });
        await repository.SaveAsync(new AtlasProviderRecord { Id = "second", Score = 2 });
        Assert.IsTrue(failed.IsDisposed);
        Assert.AreEqual(2, delivered);
        Assert.IsTrue(telemetry.Records.Any(r => r.Operation == DatabaseOperation.Publish && r.Log && r.Outcome == DatabaseOutcome.Failure));
        Assert.AreEqual(2, (await repository.QueryFirstByStorageKeyAsync("second"))!.Score);
    }

    private sealed class FollowupCapture : IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public ConcurrentQueue<DatabaseObservation> Records { get; } = new();
        public void TryRecord(in DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(DatabaseGauge gauge, long delta) { }
    }
}
