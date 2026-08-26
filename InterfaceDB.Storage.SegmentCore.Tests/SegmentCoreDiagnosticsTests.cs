using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCoreDiagnosticsTests
{
    [TestMethod]
    public async Task OpenCheckpointAndIntegrity_EmitStandardMeterMeasurements()
    {
        using var root = new DiagnosticsTemporaryDirectory();
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    "InterfaceDB.Storage.SegmentCore",
                    StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, FindTag(tags, "stage"), value)));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(
                instrument.Name,
                FindTag(tags, "operation") ?? FindTag(tags, "component"),
                value)));
        listener.Start();

        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, Options()))
        {
            await store.SaveAsync("diagnostic", new byte[] { 1, 2, 3, 4 });
            _ = await store.CheckpointAsync();
            var integrity = await store.VerifyIntegrityAsync();
            Assert.IsTrue(integrity.IsValid, string.Join(Environment.NewLine, integrity.Failures));
        }

        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.open.stage.duration"
            && item.Dimension == "storage_format_manifest"
            && item.Value >= 0));
        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.open.stage.duration"
            && item.Dimension == "transaction_wal"
            && item.Value >= 0));
        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.operation.count"
            && item.Dimension == "open"
            && item.Value == 1));
        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.operation.count"
            && item.Dimension == "checkpoint"
            && item.Value == 1));
        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.operation.count"
            && item.Dimension == "integrity_verify"
            && item.Value == 1));
        Assert.IsTrue(measurements.Any(static item =>
            item.Instrument == "interfacedb.segmentcore.storage.bytes"
            && item.Dimension == "checkpoint"
            && item.Value > 0));
    }

    private static string? FindTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string name)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private static SegmentCoreOptions Options() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 2,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    private readonly record struct Measurement(string Instrument, string? Dimension, double Value);

    private sealed class DiagnosticsTemporaryDirectory : IDisposable
    {
        public DiagnosticsTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterfaceDbSegmentCoreDiagnosticsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide a diagnostic assertion failure.
            }
        }
    }
}
