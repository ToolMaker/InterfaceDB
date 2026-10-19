using System.Diagnostics;
using System.Diagnostics.Metrics;
using Idb.Diagnostics;
using Microsoft.Extensions.Logging;

internal sealed class BenchmarkTelemetry : IAsyncDisposable, ILogger<DatabaseTelemetry>
{
    private readonly MeterListener _metrics = new();
    private readonly ActivityListener _traces;
    private readonly DatabaseTelemetry? _owned;
    private long _measurements, _spans, _logs;
    public IDatabaseTelemetry? Service => _owned;
    public string Mode { get; }
    public BenchmarkTelemetry()
    {
        Mode = Environment.GetEnvironmentVariable("INTERFACEDB_BENCH_TELEMETRY") ?? "disabled";
        if (Mode is not ("disabled" or "metrics" or "recommended" or "full")) throw new ArgumentException("Unknown benchmark telemetry mode.");
        _metrics.InstrumentPublished = (instrument, listener) =>
        { if (instrument.Meter.Name == DatabaseTelemetry.SourceName) listener.EnableMeasurementEvents(instrument); };
        _metrics.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref _measurements));
        _metrics.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref _measurements));
        _traces = new ActivityListener { ShouldListenTo = source => source.Name == DatabaseTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _ => Interlocked.Increment(ref _spans) };
        if (Mode != "disabled")
        {
            _metrics.Start(); ActivitySource.AddActivityListener(_traces);
            _owned = new(new() { QueueCapacity = 65536, Traces = Mode != "metrics",
                TraceSampleRatio = Mode == "recommended" ? 0.01 : 1, TcpTraces = Mode == "full" }, this);
        }
    }
    public object Statistics => new { Mode, Measurements = _measurements, Spans = _spans, Logs = _logs,
        Dropped = _owned?.DroppedRecords ?? 0, ExportFailures = _owned?.ExportFailures ?? 0 };
    public async ValueTask DisposeAsync()
    { if (_owned is not null) await _owned.DisposeAsync(); _traces.Dispose(); _metrics.Dispose(); }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => Mode is "full" or "recommended" && logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { if (IsEnabled(logLevel)) { _ = formatter(state, exception); Interlocked.Increment(ref _logs); } }
}
