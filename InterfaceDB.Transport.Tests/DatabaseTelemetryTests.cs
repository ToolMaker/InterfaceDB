using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Idb.Diagnostics;
using Idb.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InterfaceDB.Transport.Tests;

[TestClass, DoNotParallelize]
public sealed class DatabaseTelemetryTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task DefaultTraceBoundaryIsRepositoryAndTcpRequiresOptIn(bool tcp)
    {
        Assert.AreEqual(0.01, new DatabaseTelemetryOptions().TraceSampleRatio);
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatabaseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        await using var telemetry = new DatabaseTelemetry(new() { TraceSampleRatio = 1, TcpTraces = tcp });
        foreach (var component in Enum.GetValues<DatabaseComponent>())
            DatabaseTelemetryScope.Start(telemetry, component, DatabaseOperation.Read).Complete();
        await telemetry.DisposeAsync();
        var expected = new List<string> { "Repository.Read", "TcpRepository.Read", "LocalRemote.Read" };
        if (tcp) expected.AddRange(["TcpClient.Read", "TcpHost.Read"]);
        CollectionAssert.AreEquivalent(expected, spans.Select(span => span.OperationName).ToList());
    }

    [TestMethod]
    public async Task FailureLogsIgnoreSamplingAndExcludeSuccessAndCancellation()
    {
        var logger = new CollectingLogger();
        await using var telemetry = new DatabaseTelemetry(new() { TraceSampleRatio = 0 }, logger);
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.TcpClient, DatabaseOperation.Connect, log: true).Complete();
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Read, log: true).Fail(new OperationCanceledException());
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Query, log: true).Fail(new InvalidOperationException("private"));
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Refresh, log: true)
            .Fail(new IOException("private"), DatabaseError.Unavailable, recoverable: true);
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.TcpHost, DatabaseOperation.Connect, log: true).Fail(new UnauthorizedAccessException("private"));
        await telemetry.DisposeAsync();
        CollectionAssert.AreEqual(new[] { LogLevel.Error, LogLevel.Warning, LogLevel.Warning }, logger.Levels.ToArray());
        Assert.IsFalse(string.Join(';', logger.Messages).Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SameExceptionAcrossStorageAndRepositoryLogsOnceButCountsBothBoundaries()
    {
        long completed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        { if (instrument.Meter.Name == DatabaseTelemetry.SourceName && instrument.Name == "interfacedb.operation.completed") owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref completed, value));
        listener.Start();
        var logger = new CollectingLogger();
        await using var telemetry = new DatabaseTelemetry(new() { Traces = false }, logger);
        var original = new IOException("private");
        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () => await DatabaseTelemetryOperation.ObserveAsync<int>(
            telemetry, DatabaseComponent.Repository, DatabaseOperation.Read,
            () => DatabaseTelemetryOperation.ObserveAsync<int>(telemetry, DatabaseComponent.File, DatabaseOperation.Read,
                () => ValueTask.FromException<int>(original), log: true), log: true));
        await telemetry.DisposeAsync();
        Assert.AreSame(original, actual);
        Assert.AreEqual(1, logger.Messages.Count);
        Assert.AreEqual(2L, completed);
    }

    [TestMethod]
    public async Task LazyQueryFailureIsLoggedAndPreservesOriginalException()
    {
        var original = new IOException("private");
        async IAsyncEnumerable<int> Query()
        { yield return 1; await Task.Yield(); throw original; }
        var logger = new CollectingLogger();
        await using var telemetry = new DatabaseTelemetry(new() { Traces = false }, logger);
        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await foreach (var _ in DatabaseTelemetryOperation.ObserveStream(telemetry, DatabaseComponent.Repository,
                DatabaseOperation.Query, Query())) { }
        });
        await telemetry.DisposeAsync();
        Assert.AreSame(original, actual);
        Assert.AreEqual(1, logger.Messages.Count);
        StringAssert.Contains(logger.Messages.Single(), "Query");
    }

    [TestMethod]
    public async Task MinimumErrorLevelFiltersWarnings()
    {
        var logger = new CollectingLogger();
        await using var telemetry = new DatabaseTelemetry(new() { Traces = false, MinimumLogLevel = LogLevel.Error }, logger);
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Save, log: true).Fail(new ArgumentException());
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Read, log: true).Fail(new IOException());
        await telemetry.DisposeAsync();
        CollectionAssert.AreEqual(new[] { LogLevel.Error }, logger.Levels.ToArray());
    }

    [TestMethod]
    public async Task TransportFailureCategoriesPreserveDeniedAndUnknownCommitOutcomes()
    {
        var telemetry = new CapturingTelemetry();
        foreach (var code in new[] { RemoteErrorCode.Unauthorized, RemoteErrorCode.OutcomeUnknown })
        {
            var original = new RemoteStoreException(code, "private");
            var actual = await Assert.ThrowsExactlyAsync<RemoteStoreException>(async () => await TransportTelemetry.ObserveAsync<int>(
                telemetry, DatabaseComponent.TcpClient, DatabaseOperation.Connect, () => ValueTask.FromException<int>(original)));
            Assert.AreSame(original, actual);
        }
        CollectionAssert.AreEqual(new[] { DatabaseError.Denied, DatabaseError.OutcomeUnknown }, telemetry.Records.Select(record => record.Error).ToArray());
    }

    [TestMethod]
    public async Task NullOptionsKeepArgumentExceptionAndOriginalBinaryEntryPointsRemain()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await TcpEncodedEnvelopeStore.ConnectAsync(null!));
        Assert.IsTrue(typeof(SegmentCoreEncodedEnvelopeStore).GetMethods().Any(method => method.Name == "OpenAsync" && method.GetParameters().Length == 6));
        Assert.IsTrue(typeof(VersionedTcpStore).GetMethods().Any(method => method.Name == "OpenAsync" && method.GetParameters().Length == 9));
    }

    [TestMethod]
    public async Task BlockingExporterDropsBoundedRecordsWithoutBlockingDatabaseOrShutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        { if (instrument.Meter.Name == DatabaseTelemetry.SourceName && instrument.Name == "interfacedb.operation.completed") owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); });
        listener.Start();
        var telemetry = new DatabaseTelemetry(new() { QueueCapacity = 2, Traces = false, ShutdownTimeout = TimeSpan.FromMilliseconds(20) });
        try
        {
            DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Read).Complete();
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 100; i++)
                Assert.AreEqual(42, await DatabaseTelemetryOperation.ObserveAsync(telemetry, DatabaseComponent.Repository,
                    DatabaseOperation.Save, () => ValueTask.FromResult(42)));
            Assert.AreEqual(98L, telemetry.DroppedRecords);
            await telemetry.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(telemetry.Completion.IsCompleted);
        }
        finally { release.Set(); await telemetry.DisposeAsync(); await telemetry.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task ThrowingListenersAndLoggerPreserveResultsAndOriginalExceptions()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        { if (instrument.Meter.Name == DatabaseTelemetry.SourceName) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener"));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("listener"));
        listener.Start();
        using var activities = new ActivityListener { ShouldListenTo = source => source.Name == DatabaseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _ => throw new InvalidOperationException("trace exporter") };
        ActivitySource.AddActivityListener(activities);
        await using var telemetry = new DatabaseTelemetry(new() { TraceSampleRatio = 1 }, new ThrowingLogger());
        Assert.AreEqual(7, await DatabaseTelemetryOperation.ObserveAsync(telemetry, DatabaseComponent.Repository,
            DatabaseOperation.Initialize, () => ValueTask.FromResult(7), log: true));
        var original = new IOException("private-token-record-content");
        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await DatabaseTelemetryOperation.ObserveAsync<int>(telemetry, DatabaseComponent.Repository,
                DatabaseOperation.Save, () => ValueTask.FromException<int>(original), log: true));
        Assert.AreSame(original, actual);
        await telemetry.DisposeAsync();
        Assert.IsTrue(telemetry.ExportFailures >= 4);
    }

    [TestMethod]
    public async Task ClosedLabelsAndCompletedSpansExcludeAmbientSecrets()
    {
        var tags = new ConcurrentQueue<string>();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        { if (instrument.Meter.Name == DatabaseTelemetry.SourceName) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, _, values, _) => { foreach (var value in values) tags.Enqueue(value.Key + "=" + value.Value); });
        listener.SetMeasurementEventCallback<double>((_, _, values, _) => { foreach (var value in values) tags.Enqueue(value.Key + "=" + value.Value); });
        listener.Start();
        using var activities = new ActivityListener { ShouldListenTo = source => source.Name == DatabaseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Enqueue };
        ActivitySource.AddActivityListener(activities);
        var logger = new CollectingLogger();
        using var parent = new Activity("app").SetIdFormat(ActivityIdFormat.W3C).Start();
        parent.TraceStateString = "private=secret"; parent.AddBaggage("child", "private-secret");
        await using var telemetry = new DatabaseTelemetry(new() { TraceSampleRatio = 1 }, logger);
        var scope = DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Save, log: true);
        scope.Fail(new IOException("private-secret"));
        telemetry.TryRecord(new((DatabaseComponent)999, (DatabaseOperation)999, (DatabaseOutcome)999,
            (DatabaseError)999, DateTimeOffset.UtcNow, TimeSpan.Zero, default));
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.SegmentCore, DatabaseOperation.Read).Complete();
        await telemetry.DisposeAsync();
        Assert.AreEqual(1, spans.Count, "Only recognized repository boundaries are traced by default.");
        var child = spans.Single(span => span.OperationName == "Repository.Save");
        Assert.AreEqual(parent.TraceId, child.TraceId); Assert.AreEqual(parent.SpanId, child.ParentSpanId);
        Assert.AreEqual(0, child.Baggage.Count()); Assert.IsTrue(string.IsNullOrEmpty(child.TraceStateString));
        Assert.IsTrue(tags.Contains("component=Unknown"));
        Assert.IsFalse(string.Join(';', tags).Contains("private", StringComparison.Ordinal));
        Assert.IsFalse(string.Join(';', logger.Messages).Contains("private", StringComparison.Ordinal));
        Assert.AreEqual(1, logger.Messages.Count); Assert.IsNull(logger.Exception);
    }

    [TestMethod]
    public async Task DisabledServiceAndFaultyCustomServiceDoNotAffectResults()
    {
        await using var disabled = new DatabaseTelemetry(new() { Enabled = false });
        DatabaseTelemetryScope.Start(disabled, DatabaseComponent.Repository, DatabaseOperation.Read).Complete();
        Assert.IsFalse(disabled.IsEnabled); Assert.AreEqual(0L, disabled.DroppedRecords);
        var faulty = new ThrowingTelemetry();
        Assert.AreEqual(8, await DatabaseTelemetryOperation.ObserveAsync(faulty, DatabaseComponent.Repository,
            DatabaseOperation.Save, () => ValueTask.FromResult(8)));
        DatabaseTelemetryScope.Gauge(faulty, DatabaseGauge.Connections, 1);
    }

    [TestMethod]
    public async Task EnumerationRemainsLazyAndEarlyDisposalIsIncomplete()
    {
        var telemetry = new CapturingTelemetry();
        int moves = 0;
        async IAsyncEnumerable<int> Source()
        { moves++; yield return 1; await Task.Yield(); moves++; yield return 2; }
        var query = DatabaseTelemetryOperation.ObserveStream(telemetry, DatabaseComponent.Repository, DatabaseOperation.Query, Source());
        Assert.AreEqual(0, moves); Assert.AreEqual(0, telemetry.Records.Count);
        await foreach (var _ in query) break;
        Assert.AreEqual(1, moves);
        Assert.AreEqual(DatabaseOutcome.Incomplete, telemetry.Records.Single().Outcome);
        var cancel = new OperationCanceledException("private");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await DatabaseTelemetryOperation.ObserveAsync<int>(
            telemetry, DatabaseComponent.Repository, DatabaseOperation.Save, () => ValueTask.FromException<int>(cancel)));
        Assert.AreEqual(DatabaseOutcome.Cancelled, telemetry.Records.Last().Outcome);
    }

    [TestMethod]
    public async Task DiOwnsOneDispatcherAndDrainsOnAsyncDisposal()
    {
        var services = new ServiceCollection();
        services.AddInterfaceDbTelemetry(new() { Traces = false }); services.AddInterfaceDbTelemetry();
        var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<DatabaseTelemetry>();
        Assert.AreSame(telemetry, provider.GetRequiredService<IDatabaseTelemetry>());
        DatabaseTelemetryScope.Start(telemetry, DatabaseComponent.Repository, DatabaseOperation.Read).Complete();
        await provider.DisposeAsync(); Assert.IsTrue(telemetry.Completion.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task TlsStorageMetricsTrackActualOperationsAndBalanceConnectionGauges()
    {
        using var certs = new TestCertificates();
        var root = Path.Combine(Path.GetTempPath(), "idb-telemetry-" + Guid.NewGuid().ToString("N"));
        var telemetry = new CapturingTelemetry();
        var manifest = new CompatibilityManifest { ApiVersion = "1", StorageEngine = "SegmentCore", StorageFormat = "1.0",
            EnvelopeFormat = "opaque", ContractIdentity = "test", ContractVersion = "1.0", ContractSchemaHash = "schema",
            ImplementationManifestHash = "types", IndexDefinitionHash = "indexes" };
        try
        {
            await using (var backend = await SegmentCoreEncodedEnvelopeStore.OpenAsync(root, telemetry: telemetry))
            await using (var host = await TcpStoreHost.StartAsync(new()
            {
                ListenEndPoint = new(IPAddress.Loopback, 0), ServerCertificate = certs.Server,
                ClientCertificateValidation = certs.ValidateClient, AuthorizeClient = (_, _) => true,
                Stores = [new("private-alias", manifest, backend)], Telemetry = telemetry
            }))
            await using (var client = await TcpEncodedEnvelopeStore.ConnectAsync(new()
            {
                Host = "127.0.0.1", Port = host.LocalEndPoint.Port, TargetHost = "localhost",
                ClientCertificate = certs.Client, ServerCertificateValidation = certs.ValidateServer,
                StoreAlias = "private-alias", Manifest = manifest, Telemetry = telemetry
            }))
            {
                await client.PutAsync("private-record-key", [1, 2, 3]);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await client.GetAsync("private-record-key"));
                await client.DeleteAsync("private-record-key");
                Assert.IsNull(await client.GetAsync("private-record-key"));
            }
            Assert.AreEqual(0L, telemetry.Connections);
            foreach (var component in new[] { DatabaseComponent.TcpClient, DatabaseComponent.TcpHost, DatabaseComponent.SegmentCore })
                Assert.IsTrue(telemetry.Records.Any(record => record.Component == component && record.Operation == DatabaseOperation.Read
                    && record.Outcome == DatabaseOutcome.Success), component.ToString());
            Assert.IsTrue(telemetry.Records.Any(record => record.Component == DatabaseComponent.TcpClient && record.Bytes > 0));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CapturingTelemetry : IDatabaseTelemetry
    {
        public ConcurrentQueue<DatabaseObservation> Records { get; } = new();
        public bool IsEnabled => true;
        public long Connections;
        public void TryRecord(in DatabaseObservation observation) => Records.Enqueue(observation);
        public void AdjustGauge(DatabaseGauge gauge, long delta) { if (gauge == DatabaseGauge.Connections) Interlocked.Add(ref Connections, delta); }
    }
    private sealed class ThrowingTelemetry : IDatabaseTelemetry
    {
        public bool IsEnabled => true;
        public void TryRecord(in DatabaseObservation observation) => throw new InvalidOperationException();
        public void AdjustGauge(DatabaseGauge gauge, long delta) => throw new InvalidOperationException();
    }
    private sealed class ThrowingLogger : ILogger<DatabaseTelemetry>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger");
    }
    private sealed class CollectingLogger : ILogger<DatabaseTelemetry>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<LogLevel> Levels { get; } = new();
        public Exception? Exception;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Exception = exception; Messages.Enqueue(formatter(state, exception)); Levels.Enqueue(level); }
    }
}
