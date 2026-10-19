using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Idb.Diagnostics;

/// <summary>Bounded best-effort standard .NET telemetry. All listener/logger calls run on one background dispatcher.</summary>
public sealed class DatabaseTelemetry : IDatabaseTelemetry, IAsyncDisposable
{
    public const string SourceName = "InterfaceDB.Operations";
    private readonly DatabaseTelemetryOptions _options;
    private readonly ILogger<DatabaseTelemetry> _logger;
    private readonly Channel<DatabaseObservation> _queue;
    private readonly Task _worker;
    private readonly Meter _meter = new(SourceName, "1.0.0");
    private readonly ActivitySource _source = new(SourceName, "1.0.0");
    private readonly Counter<long> _completed, _bytes;
    private readonly Histogram<double> _duration;
    private long _dropped, _failures, _connections, _subscriptions;
    private int _disposed;
    private readonly ConditionalWeakTable<Exception, object> _loggedFailures = new();
    public bool IsEnabled => _options.Enabled && Volatile.Read(ref _disposed) == 0;
    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public long ExportFailures => Interlocked.Read(ref _failures);
    public Task Completion => _worker;

    public DatabaseTelemetry(DatabaseTelemetryOptions options, ILogger<DatabaseTelemetry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.QueueCapacity is < 1 or > 65536 || !double.IsFinite(options.TraceSampleRatio)
            || options.TraceSampleRatio is < 0 or > 1 || options.ShutdownTimeout < TimeSpan.Zero || options.ShutdownTimeout > TimeSpan.FromSeconds(30)
            || options.MinimumLogLevel is < LogLevel.Warning or > LogLevel.None)
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options; _logger = logger ?? NullLogger<DatabaseTelemetry>.Instance;
        _queue = Channel.CreateBounded<DatabaseObservation>(new BoundedChannelOptions(options.QueueCapacity)
        { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        _completed = _meter.CreateCounter<long>("interfacedb.operation.completed", "{operation}");
        _duration = _meter.CreateHistogram<double>("interfacedb.operation.duration", "s");
        _bytes = _meter.CreateCounter<long>("interfacedb.operation.bytes", "By");
        if (options.Enabled && options.Metrics)
        {
            _meter.CreateObservableGauge("interfacedb.connections.active", () => Interlocked.Read(ref _connections), "{connection}");
            _meter.CreateObservableGauge("interfacedb.subscriptions.active", () => Interlocked.Read(ref _subscriptions), "{subscription}");
            _meter.CreateObservableCounter("interfacedb.telemetry.dropped", () => DroppedRecords, "{record}");
            _meter.CreateObservableCounter("interfacedb.telemetry.export_failures", () => ExportFailures, "{failure}");
            // Runtime/container limits are supplied by the .NET runtime, without walking files or records.
            _meter.CreateObservableGauge("interfacedb.process.managed_memory", () => GC.GetTotalMemory(false), "By");
            _meter.CreateObservableGauge("interfacedb.process.available_memory", () => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, "By");
        }
        // Do not capture request logger scopes, tokens in AsyncLocals, Activity baggage or execution context.
        if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(DispatchAsync);
        else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(DispatchAsync); }
    }
    public void TryRecord(in DatabaseObservation observation)
    {
        if (!IsEnabled) return;
        if (!_queue.Writer.TryWrite(observation)) Interlocked.Increment(ref _dropped);
    }
    public void AdjustGauge(DatabaseGauge gauge, long delta)
    {
        if (!IsEnabled) return;
        if (gauge == DatabaseGauge.Connections) Interlocked.Add(ref _connections, delta);
        else if (gauge == DatabaseGauge.Subscriptions) Interlocked.Add(ref _subscriptions, delta);
    }
    public bool TryClaimFailure(Exception exception)
        => _loggedFailures.TryAdd(exception, new object());
    private bool TraceComponent(DatabaseComponent component) => component switch
    {
        DatabaseComponent.Repository or DatabaseComponent.TcpRepository or DatabaseComponent.LocalRemote => true,
        DatabaseComponent.TcpClient or DatabaseComponent.TcpHost => _options.TcpTraces,
        DatabaseComponent.File or DatabaseComponent.SegmentCore or DatabaseComponent.Atlas => _options.StorageTraces,
        _ => false
    };
    private void Safe(Action action) { try { action(); } catch { Interlocked.Increment(ref _failures); } }
    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var observation in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                try { Publish(observation); } catch { Interlocked.Increment(ref _failures); }
        }
        finally { _source.Dispose(); _meter.Dispose(); }
    }
    private static string Label<T>(T value) where T : struct, Enum => Enum.GetName(value) ?? "Unknown";
    private void Publish(DatabaseObservation observation)
    {
        var component = Label(observation.Component); var operation = Label(observation.Operation);
        var outcome = Label(observation.Outcome); var error = Label(observation.Error);
        var tags = new TagList { { "component", component }, { "operation", operation }, { "outcome", outcome }, { "error.code", error } };
        if (_options.Metrics)
        {
            Safe(() => _completed.Add(1, tags));
            Safe(() => _duration.Record(Math.Max(0, observation.Duration.TotalSeconds), tags));
            if (observation.Bytes > 0) Safe(() => _bytes.Add(observation.Bytes, tags));
        }
        Activity? activity = null;
        if (_options.Traces && TraceComponent(observation.Component)
            && _options.TraceSampleRatio > 0 && (_options.TraceSampleRatio == 1 || Random.Shared.NextDouble() < _options.TraceSampleRatio))
        {
            Safe(() => activity = _source.StartActivity(component + "." + operation,
                observation.Component == DatabaseComponent.TcpClient ? ActivityKind.Client
                    : observation.Component == DatabaseComponent.TcpHost ? ActivityKind.Server : ActivityKind.Internal,
                new ActivityContext(observation.ParentContext.TraceId, observation.ParentContext.SpanId, observation.ParentContext.TraceFlags),
                tags: tags, startTime: observation.StartedAt));
            Safe(() => { activity?.SetEndTime((observation.StartedAt + observation.Duration).UtcDateTime);
                if (observation.Outcome == DatabaseOutcome.Failure) activity?.SetStatus(ActivityStatusCode.Error); });
        }
        var level = observation.Recoverable || observation.Error is DatabaseError.Denied or DatabaseError.InvalidInput or DatabaseError.Conflict
            ? LogLevel.Warning : LogLevel.Error;
        if (observation.Log && observation.Outcome == DatabaseOutcome.Failure && level >= _options.MinimumLogLevel)
            Safe(() => { if (_logger.IsEnabled(level)) _logger.Log(level,
                new EventId(1001, "DatabaseOperation"),
                "Database {Component} {Operation}: {Outcome}, {ErrorCode}, {DurationMs} ms; trace {TraceId}",
                component, operation, outcome, error, observation.Duration.TotalMilliseconds,
                observation.ParentContext.TraceId == default ? null : observation.ParentContext.TraceId.ToHexString()); });
        Safe(() => activity?.Dispose());
        Activity.Current = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* An uncooperative observer never blocks application shutdown. */ }
    }
}

public static class DatabaseTelemetryRegistration
{
    /// <summary>Registers one owned dispatcher. Pass the resolved abstraction through repository/provider/transport options.</summary>
    public static IServiceCollection AddInterfaceDbTelemetry(this IServiceCollection services, DatabaseTelemetryOptions? options = null)
    {
        services.TryAddSingleton<DatabaseTelemetry>(provider => new(options ?? new(), provider.GetService<ILogger<DatabaseTelemetry>>()));
        services.TryAddSingleton<IDatabaseTelemetry>(provider => provider.GetRequiredService<DatabaseTelemetry>());
        return services;
    }
}
