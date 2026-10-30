using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Idb.Diagnostics;

// Closed vocabularies only: never attach contract names, aliases, paths, keys, identities or exception text.
public enum DatabaseComponent { Unknown, Repository, TcpRepository, TcpClient, TcpHost, File, SegmentCore, Atlas, LocalRemote }
public enum DatabaseOperation { Unknown, Execute, Initialize, Read, Query, Search, Save, SaveBatch, Delete, Transaction, Checkpoint, Compact, Backup, Restore, Verify, Connect, Disconnect, Subscribe, Publish, Refresh }
public enum DatabaseOutcome { Success, Failure, Cancelled, Incomplete }
public enum DatabaseError { None, Cancelled, Denied, Timeout, InvalidInput, Unavailable, Conflict, CorruptData, RemoteRejected, OutcomeUnknown, Unexpected }
public enum DatabaseGauge { Connections, Subscriptions }
/// <summary>Bounded detail beneath the stable broad error category. Never contains exception text.</summary>
public enum DatabaseFailureReason
{
    None, InvalidRequest, IncompatibleManifest, Unauthorized, UnknownStore, Busy, UnsupportedCapability,
    LimitExceeded, StorageFailure, OutcomeUnknown, DeadlineExceeded, ProtocolError, AuthenticationRequired,
    AuthenticationExpired, TokenTimeout, TokenNetwork, TokenTls, TokenCredentials, TokenMetadata,
    TokenHttp, TokenResponse, TokenUnavailable, Tls, ConnectionClosed, Socket, Renewal
}

/// <summary>Data-free completed-operation observation. Bytes counts payload bytes, never record contents.</summary>
public readonly record struct DatabaseObservation(DatabaseComponent Component, DatabaseOperation Operation,
    DatabaseOutcome Outcome, DatabaseError Error, DateTimeOffset StartedAt, TimeSpan Duration,
    ActivityContext ParentContext, long Bytes = 0, bool Log = false)
{
    public bool Recoverable { get; init; }
    public DatabaseFailureReason Reason { get; init; }
    /// <summary>Local connection correlation, never a metric label or an authorization identifier.</summary>
    public Guid ConnectionId { get; init; }
}

/// <summary>Borrowed diagnostic service. Implementations must be nonblocking and bounded; callers isolate exceptions.</summary>
public interface IDatabaseTelemetry
{
    bool IsEnabled { get; }
    void TryRecord(in DatabaseObservation observation);
    void AdjustGauge(DatabaseGauge gauge, long delta);
    /// <summary>Claims a failure for logging without retaining its exception in the observation queue.</summary>
    bool TryClaimFailure(Exception exception) => true;
}

public sealed record DatabaseTelemetryOptions
{
    public bool Enabled { get; init; } = true;
    public int QueueCapacity { get; init; } = 1024;
    public bool Metrics { get; init; } = true;
    public bool Traces { get; init; } = true;
    /// <summary>Repository spans are the default boundary. Enable TCP spans only for additional diagnostics.</summary>
    public bool TcpTraces { get; init; }
    /// <summary>Storage spans are off by default; operation metrics still cover storage.</summary>
    public bool StorageTraces { get; init; }
    public double TraceSampleRatio { get; init; } = 0.01;
    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Warning;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Allocation-free producer token; observers are never invoked by Start. Default represents disabled telemetry.</summary>
public readonly struct DatabaseTelemetryScope
{
    private readonly IDatabaseTelemetry? _telemetry;
    private readonly DatabaseComponent _component;
    private readonly DatabaseOperation _operation;
    private readonly long _started;
    private readonly DateTimeOffset _time;
    private readonly ActivityContext _parent;
    private readonly bool _log;
    private readonly Guid _connectionId;
    private DatabaseTelemetryScope(IDatabaseTelemetry telemetry, DatabaseComponent component, DatabaseOperation operation, bool log)
    {
        _telemetry = telemetry; _component = component; _operation = operation; _log = log;
        _connectionId = default;
        _started = Stopwatch.GetTimestamp(); _time = DateTimeOffset.UtcNow;
        var parent = Activity.Current?.Context ?? default;
        // Never propagate baggage or vendor tracestate into the background diagnostics pipeline.
        _parent = new(parent.TraceId, parent.SpanId, parent.TraceFlags);
    }
    public static DatabaseTelemetryScope Start(IDatabaseTelemetry? telemetry, DatabaseComponent component, DatabaseOperation operation, bool log = false)
    {
        try { return telemetry?.IsEnabled == true ? new(telemetry, component, operation, log) : default; }
        catch { return default; }
    }
    public void Complete(long bytes = 0, DatabaseOutcome outcome = DatabaseOutcome.Success, DatabaseError error = DatabaseError.None)
        => Record(bytes, outcome, error, _log, recoverable: false);
    private DatabaseTelemetryScope(DatabaseTelemetryScope source, Guid connectionId) { this = source; _connectionId = connectionId; }
    public DatabaseTelemetryScope WithConnection(Guid connectionId) => new(this, connectionId);
    public void CompleteWithReason(DatabaseFailureReason reason, DatabaseOutcome outcome, DatabaseError error, long bytes = 0, bool recoverable = false)
        => Record(bytes, outcome, error, _log, recoverable, reason);
    private void Record(long bytes, DatabaseOutcome outcome, DatabaseError error, bool log, bool recoverable,
        DatabaseFailureReason reason = DatabaseFailureReason.None)
    {
        if (_telemetry is null) return;
        try { _telemetry.TryRecord(new(_component, _operation, outcome, error, _time, Stopwatch.GetElapsedTime(_started), _parent, Math.Max(0, bytes), log)
            { Recoverable = recoverable, Reason = reason, ConnectionId = _connectionId }); }
        catch { /* Diagnostic implementations cannot replace database results. */ }
    }
    public void Fail(Exception exception) => Fail(exception, Classify(exception));
    public void Fail(Exception exception, DatabaseError error, bool recoverable = false)
        => FailWithReason(exception, error, DatabaseFailureReason.None, recoverable);
    public void FailWithReason(Exception exception, DatabaseError error, DatabaseFailureReason reason, bool recoverable = false)
    {
        var log = _log && exception is not OperationCanceledException;
        try { if (log && _telemetry is not null) log = _telemetry.TryClaimFailure(exception); }
        catch { log = false; }
        Record(0, exception is OperationCanceledException ? DatabaseOutcome.Cancelled : DatabaseOutcome.Failure, error, log, recoverable, reason);
    }
    public static DatabaseError Classify(Exception exception) => exception switch
    {
        OperationCanceledException => DatabaseError.Cancelled,
        UnauthorizedAccessException or System.Security.Authentication.AuthenticationException => DatabaseError.Denied,
        TimeoutException => DatabaseError.Timeout,
        InvalidDataException => DatabaseError.CorruptData,
        System.Net.Sockets.SocketException or IOException => DatabaseError.Unavailable,
        ArgumentException => DatabaseError.InvalidInput,
        _ => DatabaseError.Unexpected
    };
    public static void Gauge(IDatabaseTelemetry? telemetry, DatabaseGauge gauge, long delta)
    { try { if (telemetry?.IsEnabled == true) telemetry.AdjustGauge(gauge, delta); } catch { } }
}
