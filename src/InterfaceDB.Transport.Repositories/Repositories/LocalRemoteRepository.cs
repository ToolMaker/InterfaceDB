using Idb.Diagnostics;
using Idb.Libraries.Repositories;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Idb.Commercial.Repositories;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Transport.Repositories;

public sealed class RepositoryUpgradeRequiredException(string version)
    : InvalidOperationException($"This client cannot run repository version '{version}'. Deploy the matching contract and migration code.")
{
    public string RequiredVersion { get; } = version;
}
public sealed record RepositoryVersionContext(string? PreviousVersion, CompatibilityManifest SelectedManifest, bool IsRemote);
/// <summary>Owns the selected repository and its provider/endpoints. Dispose stops subscriptions before releasing storage.</summary>
public sealed record RepositoryLease<T>(IRepository<T> Repository, Func<ValueTask> CloseAsync)
    : IAsyncDisposable where T : class, IStoredContract<T>
{
    public ValueTask DisposeAsync() => CloseAsync();
}
/// <summary>Trusted code deployed with the application. Prepare performs resumable migration; Open creates that exact baseline.</summary>
public sealed record RepositoryVersionPlan<T>(CompatibilityManifest Manifest,
    Func<RepositoryVersionContext, CancellationToken, ValueTask> PrepareAsync,
    Func<CancellationToken, ValueTask<RepositoryLease<T>>> OpenAsync) : IRepositoryVersionPlan<T> where T : class, IStoredContract<T>
{
    ValueTask IRepositoryVersionPlan<T>.PrepareAsync(RepositoryVersionContext context, CancellationToken token) => PrepareAsync(context, token);
    ValueTask<RepositoryLease<T>> IRepositoryVersionPlan<T>.OpenAsync(CancellationToken token) => OpenAsync(token);
}

/// <summary>
/// One version authority and stable application facade. A TCP authority is queried before any local
/// repository is opened. Offline startup uses only the last durably agreed version.
/// </summary>
public sealed partial class LocalRemoteRepository<TContract> : IRepository<TContract>, IRepositoryChangeNotifications, IAsyncDisposable
    where TContract : class, IStoredContract<TContract>
{
    private readonly IDatabaseTelemetry? _telemetry;
    private readonly IRepositoryVersionPlan<TContract>[] _plans;
    private readonly IRepositoryVersionAuthority? _remote;
    private readonly IRepositoryCursorStore _versions;
    private readonly string _scope;
    private readonly Func<Exception, bool> _unavailable;
    private readonly TimeSpan _retry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly RepositoryChangePublisher _changes = new();
    private RepositoryLease<TContract>? _lease;
    private IRepositoryChangeSubscription? _forward, _stateForward;
    private CompatibilityManifest? _manifest;
    private string? _selectionIdentity;
    private Task? _worker;
    private Exception? _fault;
    private int _disposed;
    public RepositoryConnection Connection { get; } = new();
    public string? RunningVersion => _manifest?.ContractVersion;
    public ContractRepositoryType RepositoryType => _remote is null ? _lease?.Repository.RepositoryType ?? ContractRepositoryType.None : ContractRepositoryType.Remote;

    public LocalRemoteRepository(IEnumerable<RepositoryVersionPlan<TContract>> versions,
        IRepositoryCursorStore versionState, string accountScope, TcpStoreClientOptions? remote = null,
        TimeSpan? retryDelay = null, Func<Exception, bool>? isUnavailable = null, IDatabaseTelemetry? telemetry = null)
        : this(remote is null ? null : new TcpRepositoryVersionAuthority(remote), versions, versionState, accountScope, retryDelay, isUnavailable, telemetry ?? remote?.Telemetry) { }

    public LocalRemoteRepository(IRepositoryVersionAuthority? authority, IEnumerable<IRepositoryVersionPlan<TContract>> versions,
        IRepositoryCursorStore versionState, string accountScope,
        TimeSpan? retryDelay = null, Func<Exception, bool>? isUnavailable = null, IDatabaseTelemetry? telemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountScope);
        _plans = versions.OrderBy(plan => Version.Parse(plan.Manifest.ContractVersion)).ToArray();
        if (_plans.Length == 0 || _plans.Select(plan => plan.Manifest.ContractVersion).Distinct().Count() != _plans.Length)
            throw new ArgumentException("Register unique version plans.");
        foreach (var plan in _plans)
        {
            plan.Manifest.Validate();
            if (plan.Manifest.ContractIdentity != _plans[0].Manifest.ContractIdentity) throw new ArgumentException("Mixed contract identities.");
        }
        _telemetry = telemetry;
        _remote = authority;
        _versions = versionState;
        _scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            accountScope + "|" + (authority?.Scope ?? "||") + "|" + _plans[0].Manifest.ContractIdentity)));
        _unavailable = isUnavailable ?? TcpRepositoryAvailability.IsUnavailable;
        _retry = retryDelay ?? TimeSpan.FromSeconds(5);
        TransportValidation.Duration(_retry, nameof(retryDelay));
    }

    public IRepositoryChangeSubscription SubscribeChanges(Func<ValueTask> handler, CancellationToken lifetime = default)
        => _changes.SubscribeChanges(handler, lifetime);

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => !DatabaseTelemetryOperation.IsEnabled(_telemetry) ? InitializeAsyncUnobserved(cancellationToken)
            : InitializeAsyncObserved(cancellationToken);

    private ValueTask InitializeAsyncObserved(CancellationToken cancellationToken = default)
        => DatabaseTelemetryOperation.ObserveAsync(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Initialize,
                () => InitializeAsyncUnobserved(cancellationToken), log: true, bytes: 0);

    private async ValueTask InitializeAsyncUnobserved(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_fault is { } fault) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
            if (_lease is null) await SelectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (!_unavailable(exception) && exception is not RepositoryAccessRenewalRequiredException) _fault = exception;
            Connection.Set(_unavailable(exception) ? RepositoryConnectionState.Offline : RepositoryConnectionState.Faulted, exception);
            throw;
        }
        finally
        {
            if (_remote is not null && _worker is null && _fault is null && _disposed == 0)
                _worker = Task.Run(WatchAsync);
            _gate.Release();
        }
    }

    private static string ManifestHash(CompatibilityManifest manifest) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest)));

    private async ValueTask SelectAsync(CancellationToken token)
    {
        var receipt = await _versions.ReadAsync(_scope, token).ConfigureAwait(false);
        string? previous = null;
        if (receipt is not null)
        {
            var parts = receipt.Split('|');
            var installed = _plans.SingleOrDefault(item => item.Manifest.ContractVersion == parts[0]);
            if (parts.Length != 2 || installed is null || ManifestHash(installed.Manifest) != parts[1])
                throw new InvalidDataException("The previously agreed baseline is missing or changed in this client deployment.");
            previous = parts[0];
        }
        CompatibilityManifest selected;
        bool offline = false;
        try { selected = _remote is null ? _plans[^1].Manifest : await _remote.DiscoverAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (_remote is not null && _unavailable(exception) && previous is not null)
        {
            await _remote.ValidateAccessAsync(false, token).ConfigureAwait(false);
            selected = _plans.SingleOrDefault(plan => plan.Manifest.ContractVersion == previous)?.Manifest
                ?? throw new RepositoryUpgradeRequiredException(previous);
            if (_remote.LastApprovedManifest is { } approved && approved != selected)
            {
                if (!_plans.Any(candidate => candidate.Manifest == approved)) throw new RepositoryUpgradeRequiredException(approved.ContractVersion);
                throw new InvalidOperationException("The cached server approval requires another baseline. Reconnect to complete its activation.");
            }
            offline = true;
            DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Connect, log: true)
                .Fail(exception, DatabaseTelemetryScope.Classify(exception), recoverable: true);
            Connection.Set(RepositoryConnectionState.Offline, exception);
        }
        var plan = _plans.SingleOrDefault(item => item.Manifest == selected)
            ?? throw new RepositoryUpgradeRequiredException(selected.ContractVersion);
        if (_manifest == selected && _lease is not null && _selectionIdentity == _remote?.SelectionIdentity)
        {
            Connection.Set(offline ? RepositoryConnectionState.Offline
                : _lease.Repository is IResilientRepository<TContract> current ? current.Connection.State : RepositoryConnectionState.Online);
            return;
        }
        if (previous is not null && Version.Parse(previous) > Version.Parse(selected.ContractVersion))
            throw new InvalidOperationException("Automatic repository downgrade is unsupported.");
        Connection.Set(RepositoryConnectionState.Synchronizing);
        _forward?.Dispose(); _stateForward?.Dispose();
        if (_stateForward is not null) await _stateForward.DisposeAsync().ConfigureAwait(false);
        if (_lease is { } retired)
        {
            _lease = null; // In-flight callbacks from the retired version no longer own the facade.
            await retired.DisposeAsync().ConfigureAwait(false);
        }
        if (previous != selected.ContractVersion)
            await plan.PrepareAsync(new(previous, selected, _remote is not null), token).ConfigureAwait(false);
        var opened = await plan.OpenAsync(token).ConfigureAwait(false);
        try
        {
            await opened.Repository.InitializeAsync(token).ConfigureAwait(false);
            RepositoryDataAvailability.RequireComplete(opened.Repository);
            if (opened.Repository.RunningVersion != selected.ContractVersion)
                throw new InvalidDataException("The version factory opened a different repository baseline.");
            if (!offline) await _versions.WriteAsync(_scope, selected.ContractVersion + "|" + ManifestHash(selected), token).ConfigureAwait(false);
        }
        catch { await opened.DisposeAsync().ConfigureAwait(false); throw; }
        _lease = opened; _manifest = selected; _fault = null; _selectionIdentity = _remote?.SelectionIdentity;
        if (opened.Repository is IRepositoryChangeNotifications notifications)
            _forward = notifications.SubscribeChanges(() =>
            {
                if (ReferenceEquals(_lease, opened)) _changes.PublishChanged();
                return ValueTask.CompletedTask;
            }, _stop.Token);
        if (opened.Repository is IResilientRepository<TContract> composed)
            _stateForward = composed.Connection.SubscribeChanges(() =>
            {
                var state = composed.Connection.State;
                if (ReferenceEquals(_lease, opened) && _disposed == 0 && _fault is null
                    && state != RepositoryConnectionState.Disposed)
                    Connection.Set(state, composed.Connection.LastFailure);
                return ValueTask.CompletedTask;
            }, _stop.Token);
        Connection.Set(offline ? RepositoryConnectionState.Offline
            : opened.Repository is IResilientRepository<TContract> ready ? ready.Connection.State : RepositoryConnectionState.Online);
        _changes.PublishChanged();
    }

    private async Task WatchAsync()
    {
        var retry = new RepositoryRetryBackoff(_retry);
        while (!_stop.IsCancellationRequested)
        {
            long connectedAt = 0;
            try
            {
                Connection.Set(RepositoryConnectionState.Reconnecting);
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                CompatibilityManifest manifest;
                try { await SelectAsync(_stop.Token).ConfigureAwait(false); manifest = _manifest!; }
                finally { _gate.Release(); }
                connectedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                // The selected composition owns its data subscription and catch-up. This listener
                // owns version lifetime; server restart/cutover forces discovery before reopening.
                if (_lease?.Repository is not IResilientRepository<TContract>)
                    Connection.Set(RepositoryConnectionState.Online);
                await _remote!.ObserveAsync(manifest, _stop.Token).ConfigureAwait(false);
                retry.Reset();
                // A normal return requests rediscovery (for example, approval lease renewal).
                // Network authorities report unexpected disconnects as availability exceptions.
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (_stop.IsCancellationRequested) break;
                var recoverable = _unavailable(exception) || exception is RepositoryAccessRenewalRequiredException
                    or RemoteStoreException { Code: RemoteErrorCode.IncompatibleManifest };
                DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Refresh, log: true)
                    .Fail(exception, DatabaseTelemetryScope.Classify(exception), recoverable);
                if (!recoverable)
                {
                    _fault = exception;
                    Connection.Set(RepositoryConnectionState.Faulted, exception);
                    break;
                }
                Connection.Set(exception is RepositoryAccessRenewalRequiredException
                    ? RepositoryConnectionState.Faulted : RepositoryConnectionState.Offline, exception);
                if (connectedAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(connectedAt) >= _retry) retry.Reset();
                try { await Task.Delay(retry.NextDelay(), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            }
        }
    }

    private ValueTask<TResult> WithWritableRepositoryAsync<TResult>(Func<IRepository<TContract>, ValueTask<TResult>> operation, CancellationToken token)
    {
        if (_changes.IsDispatchingCallback) throw new RepositoryFeedbackLoopException();
        return WithRepositoryAsync(operation, token, writing: true);
    }

    private ValueTask<TResult> WithRepositoryAsync<TResult>(Func<IRepository<TContract>, ValueTask<TResult>> operation, CancellationToken token, bool writing = false)
        => !DatabaseTelemetryOperation.IsEnabled(_telemetry) ? WithRepositoryAsyncUnobserved<TResult>(operation, token, writing)
            : WithRepositoryAsyncObserved<TResult>(operation, token, writing);

    private ValueTask<TResult> WithRepositoryAsyncObserved<TResult>(Func<IRepository<TContract>, ValueTask<TResult>> operation, CancellationToken token, bool writing = false)
        => DatabaseTelemetryOperation.ObserveAsync(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Execute,
                () => WithRepositoryAsyncUnobserved<TResult>(operation, token, writing), log: true, bytes: 0);

    private async ValueTask<TResult> WithRepositoryAsyncUnobserved<TResult>(Func<IRepository<TContract>, ValueTask<TResult>> operation, CancellationToken token, bool writing = false)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_fault is { } fault) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
            // Version selection can retire the lease between initialization and acquiring this gate.
            if (_lease is null) await SelectAsync(token).ConfigureAwait(false);
            if (_remote is not null) await _remote.ValidateAccessAsync(writing, token).ConfigureAwait(false);
            return await operation(_lease!.Repository).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private async IAsyncEnumerable<T> StreamAsync<T>(Func<IRepository<TContract>, IAsyncEnumerable<T>> operation,
        [EnumeratorCancellation] CancellationToken token)
    {
        var values = await WithRepositoryAsync(async repository =>
        {
            var snapshot = new List<T>();
            await foreach (var item in operation(repository).WithCancellation(token).ConfigureAwait(false)) {
                if (snapshot.Count >= 100_000) throw new InvalidDataException("Query exceeds the repository limit.");
                snapshot.Add(item);
            }
            return snapshot;
        }, token).ConfigureAwait(false);
        foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return value; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _forward?.Dispose(); _stateForward?.Dispose(); if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); Connection.Dispose(); _changes.Dispose(); _stop.Dispose(); }
    }
}
