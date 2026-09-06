using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Libraries.Implementation.Maintenance;

/// <summary>Coalesces maintenance requests and evaluates policy outside engine locks. Does not turn
/// a blocking engine into a concurrent one. Probe/execute delegates must honor cancellation and
/// return only after releasing provider locks. Callers must not invoke RunAsync under those locks.</summary>
public sealed class CompactionScheduler : IMaintenanceNotifications, IDisposable, IAsyncDisposable
{
    private readonly CompactionScheduleOptions _options;
    private readonly Func<CancellationToken, ValueTask<CompactionEstimate>> _probe;
    private readonly Func<CancellationToken, ValueTask<CompactionWorkResult>> _execute;
    private readonly MaintenanceNotificationHub _notifications;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycle = new();
    private long _lastCompletion, _lastAttempt;
    private long _observationFailures;
    private bool _attempted, _running, _disposed;
    private int _directOperations;
    private TaskCompletionSource? _drained;

    public CompactionScheduler(Func<CancellationToken, ValueTask<CompactionEstimate>> probe,
        Func<CancellationToken, ValueTask<CompactionWorkResult>> execute, CompactionScheduleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(probe); ArgumentNullException.ThrowIfNull(execute);
        _options = options ?? new(); _options.Validate();
        _probe = probe; _execute = execute; _notifications = new(_options.Notifications);
        _lastCompletion = _options.TimeProvider.GetTimestamp();
    }

    public IMaintenanceSubscription SubscribeMaintenance(Func<MaintenanceNotification, CancellationToken, ValueTask> handler,
        CancellationToken lifetime = default) => _notifications.SubscribeMaintenance(handler, lifetime);

    /// <summary>Invalid summaries returned by an execution adapter are suppressed rather than changing
    /// a completed operation into a failure. The adapter's original result is still returned to its caller.</summary>
    public long ObservationFailureCount => Interlocked.Read(ref _observationFailures);

    /// <summary>Concurrent requests coalesce. Automatic probes are rate-bounded even when a custom
    /// policy, failed probe or busy application keeps deferring work. Explicit manual requests bypass
    /// probe throttling, not provider safety. Exceptions remain visible to this caller, with a safe summary.</summary>
    public async ValueTask<CompactionRunResult> RunAsync(bool manual = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan sinceAttempt, sinceCompletion;
        var started = _options.TimeProvider.GetTimestamp();
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running || _directOperations != 0) return new(CompactionDecision.Defer(CompactionDeferReason.AlreadyRunning));
            sinceAttempt = _attempted ? _options.TimeProvider.GetElapsedTime(_lastAttempt, started) : TimeSpan.MaxValue;
            if (!manual && sinceAttempt < _options.MinimumProbeInterval)
                return new(CompactionDecision.Defer(CompactionDeferReason.MinimumInterval));
            sinceCompletion = _options.TimeProvider.GetElapsedTime(_lastCompletion, started);
            _running = true; _attempted = true; _lastAttempt = started;
        }

        MaintenanceNotification? notification = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linked.Token;
        var id = Guid.NewGuid();
        try
        {
            var estimate = await _probe(token).ConfigureAwait(false);
            var context = new CompactionPolicyContext(estimate.PhysicalBytes, estimate.ReclaimableBytes,
                sinceCompletion, sinceAttempt, _options.ActivitySource?.Activity ?? MaintenanceActivity.Unknown,
                estimate.ForegroundOutstanding, manual, _options.ActivitySource?.MaintenanceWindowOpen ?? false,
                estimate.HasWorkingSpace);
            var decision = _options.Policy.Evaluate(context);
            token.ThrowIfCancellationRequested();
            if (!decision.ShouldRun)
            {
                if (_notifications.HasSubscribers)
                    notification = new(id, MaintenanceEventKind.CompactionDeferred,
                        _options.TimeProvider.GetElapsedTime(started), ReasonCode: decision.Reason.ToString());
                return new(decision);
            }

            var result = await _execute(token).ConfigureAwait(false);
            if (!result.Deferred) lock (_lifecycle) _lastCompletion = _options.TimeProvider.GetTimestamp();
            // Do not check cancellation after a successfully committed engine result.
            if (_notifications.HasSubscribers)
                notification = new(id, result.Deferred ? MaintenanceEventKind.CompactionDeferred : MaintenanceEventKind.CompactionCompleted,
                    _options.TimeProvider.GetElapsedTime(started), result.RecordsCopied, result.BytesCopied,
                    result.BytesReclaimed, result.RetainedBytes, result.ReasonCode, result.IsPartial);
            return new(decision, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_notifications.HasSubscribers)
                notification = new(id, MaintenanceEventKind.CompactionCancelled,
                    _options.TimeProvider.GetElapsedTime(started), ReasonCode: "cancelled");
            throw;
        }
        catch
        {
            if (_notifications.HasSubscribers)
                notification = new(id, MaintenanceEventKind.CompactionFailed,
                    _options.TimeProvider.GetElapsedTime(started), ReasonCode: "maintenance-failed");
            throw;
        }
        finally
        {
            if (notification is { } summary)
            {
                try { _notifications.Publish(summary); }
                catch (ArgumentOutOfRangeException) { Interlocked.Increment(ref _observationFailures); }
            }
            lock (_lifecycle) { _running = false; SignalDrained(); }
        }
    }

    /// <summary>Observe a provider's existing direct/manual API without changing its return type or
    /// consulting policy. Engine gates still serialize concurrent direct operations. New scheduled
    /// requests coalesce while direct work is admitted. The summary mapper runs after engine locks
    /// are released; observation failure never turns a completed engine operation into a failed one.</summary>
    public async ValueTask<TResult> ObserveAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> execute,
        Func<TResult, CompactionWorkResult> summarize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute); ArgumentNullException.ThrowIfNull(summarize);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycle) { ObjectDisposedException.ThrowIf(_disposed, this); _directOperations++; }
        var started = _options.TimeProvider.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var id = Guid.NewGuid();
        MaintenanceNotification? notification = null;
        try
        {
            var result = await execute(linked.Token).ConfigureAwait(false);
            try
            {
                var work = summarize(result);
                if (!work.Deferred) lock (_lifecycle) _lastCompletion = _options.TimeProvider.GetTimestamp();
                if (_notifications.HasSubscribers)
                    notification = new(id, work.Deferred ? MaintenanceEventKind.CompactionDeferred : MaintenanceEventKind.CompactionCompleted,
                        _options.TimeProvider.GetElapsedTime(started), work.RecordsCopied, work.BytesCopied,
                        work.BytesReclaimed, work.RetainedBytes, work.ReasonCode, work.IsPartial);
            }
            catch { Interlocked.Increment(ref _observationFailures); }
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (_notifications.HasSubscribers) notification = new(id, MaintenanceEventKind.CompactionCancelled,
                _options.TimeProvider.GetElapsedTime(started), ReasonCode: "cancelled");
            throw;
        }
        catch
        {
            if (_notifications.HasSubscribers) notification = new(id, MaintenanceEventKind.CompactionFailed,
                _options.TimeProvider.GetElapsedTime(started), ReasonCode: "maintenance-failed");
            throw;
        }
        finally
        {
            if (notification is { } summary)
            {
                try { _notifications.Publish(summary); }
                catch (ArgumentOutOfRangeException) { Interlocked.Increment(ref _observationFailures); }
            }
            lock (_lifecycle) { _directOperations--; SignalDrained(); }
        }
    }

    private void SignalDrained()
    { if (!_running && _directOperations == 0) _drained?.TrySetResult(); }

    /// <summary>Requests cancellation and awaits admitted engine operations, not user callbacks.
    /// The provider must drain this before disposing the engine.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_lifecycle)
        {
            if (!_running && _directOperations == 0) return ValueTask.CompletedTask;
            return new((_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        }
    }

    /// <summary>Detaches notifications and requests cancellation; does not block waiting for provider
    /// code. The owning provider must await its admitted RunAsync before destroying engine resources.</summary>
    public void Dispose()
    {
        lock (_lifecycle) { if (_disposed) return; _disposed = true; }
        _notifications.Dispose();
        _ = CancelAsync();
    }

    private async Task CancelAsync()
    { try { await _shutdown.CancelAsync().ConfigureAwait(false); } catch (AggregateException) { } }
}
