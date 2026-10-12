using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Idb.Authentication;

namespace Idb.Transport;

/// <summary>
/// Embeddable TLS listener over host-owned stores. Each alias grants one session lease at a time,
/// preserving the first provider's single-owner model. Stop the host before disposing borrowed stores/certificates.
/// </summary>
public sealed partial class TcpStoreHost : IAsyncDisposable
{
    private sealed record RegisteredStore(TcpStoreRegistration Registration, SemaphoreSlim SessionGate,
        ConcurrentDictionary<string, byte> SubjectSessions);
    private sealed class Connection(TcpClient client)
    {
        internal TcpClient Client { get; } = client;
        internal Task Completion { get; set; } = Task.CompletedTask;
    }

    private readonly TcpStoreHostOptions _options;
    private readonly Dictionary<string, RegisteredStore> _stores;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _connectionSlots;
    private readonly SemaphoreSlim _subscriptionSlots;
    private readonly ConcurrentDictionary<long, Connection> _connections = new();
    private readonly Task _acceptLoop;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private long _nextConnectionId;

    private TcpStoreHost(TcpStoreHostOptions options)
    {
        _options = options;
        _stores = new(StringComparer.Ordinal);
        foreach (var registration in options.Stores)
        {
            ArgumentNullException.ThrowIfNull(registration);
            TransportValidation.Alias(registration.Alias);
            ArgumentNullException.ThrowIfNull(registration.Manifest);
            ArgumentNullException.ThrowIfNull(registration.Store);
            registration.Manifest.Validate();
            TransportValidation.Capabilities(registration.Store.Capabilities);
            if (_stores.Values.Any(existing => ReferenceEquals(existing.Registration.Store, registration.Store)))
                throw new ArgumentException("A physical store can be registered under only one alias.");
            _stores.Add(registration.Alias, new(registration, new(1, 1), new(StringComparer.Ordinal)));
        }
        if (_stores.Count == 0) throw new ArgumentException("At least one configured store is required.");
        _connectionSlots = new(options.MaximumConnections, options.MaximumConnections);
        var subscriptions = options.MaximumSubscriptions ?? options.MaximumConnections / 2;
        _subscriptionSlots = new(subscriptions, Math.Max(1, subscriptions));
        _listener = new(options.ListenEndPoint);
        _listener.Start(options.MaximumConnections);
        LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;
        _acceptLoop = AcceptLoopAsync();
    }

    public IPEndPoint LocalEndPoint { get; }
    public Task Completion => _acceptLoop;

    public static ValueTask<TcpStoreHost> StartAsync(TcpStoreHostOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ListenEndPoint);
        ArgumentNullException.ThrowIfNull(options.ServerCertificate);
        ArgumentNullException.ThrowIfNull(options.Stores);
        if (!Enum.IsDefined(options.AuthenticationMode)) throw new ArgumentOutOfRangeException(nameof(options.AuthenticationMode));
        if (options.IsolateBySubject && !TransportValidation.RequiresToken(options.AuthenticationMode))
            throw new ArgumentException("Subject isolation requires JWT authentication.");
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (TransportValidation.RequiresCertificate(options.AuthenticationMode)) ArgumentNullException.ThrowIfNull(options.AuthorizeClient);
        if (TransportValidation.RequiresToken(options.AuthenticationMode))
        {
            ArgumentNullException.ThrowIfNull(options.TokenValidator);
            ArgumentNullException.ThrowIfNull(options.AuthorizeIdentity);
        }
        else if (options.TokenValidator is not null || options.AuthorizeIdentity is not null)
            throw new ArgumentException("Token validation and identity authorization require a JWT authentication mode.");
        ArgumentNullException.ThrowIfNull(options.Limits);
        if (!options.ServerCertificate.HasPrivateKey) throw new ArgumentException("The server certificate requires a private key.");
        if (options.MaximumConnections is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(options.MaximumConnections));
        if (options.MaximumSubscriptions is { } subscriptions && (subscriptions < 0 || subscriptions >= options.MaximumConnections))
            throw new ArgumentOutOfRangeException(nameof(options.MaximumSubscriptions), "Reserve at least one connection for storage operations.");
        options.Limits.Validate();
        if (options.IsolateBySubject && options.Limits.MaximumKeyBytes <= SubjectScopedEncodedStore.PrefixBytes)
            throw new ArgumentException("The host key limit must also accommodate the subject partition prefix.");
        TransportValidation.Duration(options.HandshakeTimeout, nameof(options.HandshakeTimeout));
        TransportValidation.Duration(options.IdleTimeout, nameof(options.IdleTimeout));
        TransportValidation.Duration(options.MaximumSessionLifetime, nameof(options.MaximumSessionLifetime));
        TransportValidation.Duration(options.MaximumOperationTimeout, nameof(options.MaximumOperationTimeout));
        return ValueTask.FromResult(new TcpStoreHost(options));
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false); }
                catch { _connectionSlots.Release(); throw; }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _nextConnectionId);
                var connection = new Connection(client);
                _connections[id] = connection;
                connection.Completion = RunConnectionAsync(id, client);
            }
        }
        catch (Exception ex) when (_shutdown.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            Report(ex);
            _shutdown.Cancel();
            foreach (var connection in _connections.Values) connection.Client.Dispose();
            throw;
        }
    }

    private async Task RunConnectionAsync(long id, TcpClient client)
    {
        RegisteredStore? lease = null;
        RegisteredStore? selected = null;
        string? subjectLease = null;
        var subscriptionReserved = false;
        var requiresCertificate = TransportValidation.RequiresCertificate(_options.AuthenticationMode);
        try
        {
            using (client)
            using (var stream = new SslStream(client.GetStream(), false, requiresCertificate ? _options.ClientCertificateValidation : null))
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                handshake.CancelAfter(_options.HandshakeTimeout);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _options.ServerCertificate,
                    ClientCertificateRequired = requiresCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, handshake.Token).ConfigureAwait(false);
                if (requiresCertificate && (!stream.IsMutuallyAuthenticated || stream.RemoteCertificate is null))
                    throw new AuthenticationException("A client certificate is required.");
                var hello = await MessagePackFrameCodec.ReadAsync(stream, _options.Limits,
                    Math.Min(TransportValidation.MaximumHelloFrameBytes, _options.Limits.MaximumFrameBytes),
                    handshake.Token).ConfigureAwait(false);
                if (hello is null) return;
                var rejection = ValidateHello(hello);
                AuthenticatedIdentity? identity = null;
                if (rejection == RemoteErrorCode.None && hello.AuthenticationMode != _options.AuthenticationMode)
                    rejection = RemoteErrorCode.AuthenticationRequired;
                if (rejection == RemoteErrorCode.None && TransportValidation.RequiresToken(_options.AuthenticationMode))
                {
                    if (string.IsNullOrWhiteSpace(hello.AccessToken)) rejection = RemoteErrorCode.AuthenticationRequired;
                    else
                    {
                        try
                        {
                            identity = await _options.TokenValidator!.ValidateAsync(hello.AccessToken, handshake.Token).ConfigureAwait(false);
                            if (identity?.Principal.Identity?.IsAuthenticated != true
                                || identity.ExpiresAt <= _options.TimeProvider.GetUtcNow()) rejection = RemoteErrorCode.Unauthorized;
                            else if (!_options.AuthorizeIdentity!(identity, hello.StoreAlias!, hello.Capabilities))
                                rejection = RemoteErrorCode.Unauthorized;
                        }
                        catch (OperationCanceledException) when (handshake.IsCancellationRequested) { throw; }
                        catch (Exception)
                        {
                            // Validation libraries/custom validators can include credentials in exception details.
                            Report(new AuthenticationException("Access token validation or authorization failed."));
                            rejection = RemoteErrorCode.Unauthorized;
                        }
                    }
                }
                else if (rejection == RemoteErrorCode.None && hello.AccessToken is not null)
                    rejection = RemoteErrorCode.Unauthorized;
                if (rejection == RemoteErrorCode.None && _options.IsolateBySubject
                    && !SubjectScopedEncodedStore.HasSubject(identity!)) rejection = RemoteErrorCode.Unauthorized;
                if (rejection == RemoteErrorCode.None && requiresCertificate)
                {
                    using var certificate = new X509Certificate2(stream.RemoteCertificate!);
                    if (!_options.AuthorizeClient!(certificate, hello.StoreAlias!)) rejection = RemoteErrorCode.Unauthorized;
                }
                if (rejection == RemoteErrorCode.None)
                {
                    if (!_stores.TryGetValue(hello.StoreAlias!, out var candidate)) rejection = RemoteErrorCode.UnknownStore;
                    else if (hello.Kind == WireMessageKind.Discover)
                    {
                        if (hello.Manifest!.ContractIdentity != candidate.Registration.Manifest.ContractIdentity
                            || hello.Capabilities != (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits))
                            rejection = RemoteErrorCode.IncompatibleManifest;
                        else selected = candidate;
                    }
                    else if (candidate.Registration.Manifest != hello.Manifest) rejection = RemoteErrorCode.IncompatibleManifest;
                    else if (((candidate.Registration.Store.Capabilities | (_options.IsolateBySubject ? EncodedStoreCapabilities.SubjectIsolation : 0)) & hello.Capabilities) != hello.Capabilities)
                        rejection = RemoteErrorCode.UnsupportedCapability;
                    else if (hello.Kind == WireMessageKind.Subscribe
                        && (hello.Capabilities & (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate))
                            != (EncodedStoreCapabilities.Read | EncodedStoreCapabilities.Enumerate))
                        rejection = RemoteErrorCode.UnsupportedCapability;
                    else if (hello.Kind == WireMessageKind.Subscribe)
                    {
                        if (!_subscriptionSlots.Wait(0)) rejection = RemoteErrorCode.Busy;
                        else { selected = candidate; subscriptionReserved = true; }
                    }
                    else if (_options.IsolateBySubject)
                    {
                        subjectLease = SubjectScopedEncodedStore.GetPrefix(identity!);
                        if (!candidate.SubjectSessions.TryAdd(subjectLease, 0))
                        {
                            subjectLease = null;
                            rejection = RemoteErrorCode.Busy;
                        }
                        else selected = lease = candidate;
                    }
                    else if (!candidate.SessionGate.Wait(0)) rejection = RemoteErrorCode.Busy;
                    else selected = lease = candidate;
                }
                if (rejection != RemoteErrorCode.None)
                {
                    await MessagePackFrameCodec.WriteAsync(stream, new()
                    {
                        Kind = WireMessageKind.HelloResult, CorrelationId = hello.CorrelationId, Error = rejection
                    }, _options.Limits, handshake.Token).ConfigureAwait(false);
                    return;
                }
                var hostLimits = _options.Limits;
                if (_options.IsolateBySubject)
                    hostLimits = hostLimits with { MaximumKeyBytes = hostLimits.MaximumKeyBytes - SubjectScopedEncodedStore.PrefixBytes };
                var limits = hostLimits.Intersect(hello.Limits!);
                if (hello.Kind == WireMessageKind.Discover)
                {
                    await MessagePackFrameCodec.WriteAsync(stream, new()
                    {
                        Kind = WireMessageKind.HelloResult, CorrelationId = 1,
                        Manifest = selected!.Registration.Manifest, Limits = limits, Capabilities = hello.Capabilities,
                        AuthenticationMode = _options.AuthenticationMode
                    }, limits, handshake.Token).ConfigureAwait(false);
                    return;
                }
                if (hello.Kind == WireMessageKind.Subscribe)
                {
                    TransportValidation.ConfigureSubscriptionKeepAlive(client.Client);
                    await ServeSubscriptionAsync(id, stream, limits, hello, selected!, identity, handshake.Token).ConfigureAwait(false);
                    return;
                }
                await MessagePackFrameCodec.WriteAsync(stream, new()
                {
                    Kind = WireMessageKind.HelloResult, CorrelationId = hello.CorrelationId,
                    Manifest = lease!.Registration.Manifest, Limits = limits, Capabilities = hello.Capabilities,
                    AuthenticationMode = _options.AuthenticationMode
                }, limits, handshake.Token).ConfigureAwait(false);
                var sessionStore = _options.IsolateBySubject
                    ? new SubjectScopedEncodedStore(lease.Registration.Store, identity!) : lease.Registration.Store;
                var sessionExpiresAt = _options.TimeProvider.GetUtcNow() + _options.MaximumSessionLifetime;
                await ServeSessionAsync(stream, sessionStore, limits, hello.Capabilities,
                    hello.StoreAlias!, identity, sessionExpiresAt).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!_shutdown.IsCancellationRequested && ex is not OperationCanceledException) Report(ex);
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            if (lease is not null)
            {
                if (subjectLease is null) lease.SessionGate.Release();
                else lease.SubjectSessions.TryRemove(subjectLease, out _);
            }
            if (subscriptionReserved) _subscriptionSlots.Release();
            client.Dispose();
            _connections.TryRemove(id, out _);
            _connectionSlots.Release();
        }
    }

    private static RemoteErrorCode ValidateHello(WireMessage hello)
    {
        if (hello.Kind is not (WireMessageKind.Hello or WireMessageKind.Subscribe or WireMessageKind.Discover) || hello.CorrelationId != 1 || hello.StoreAlias is null
            || hello.Manifest is null || hello.Limits is null || hello.Request is not null
            || hello.Response is not null || hello.Error != RemoteErrorCode.None || hello.TimeoutMilliseconds != 0)
            return RemoteErrorCode.ProtocolError;
        try
        {
            TransportValidation.Alias(hello.StoreAlias);
            hello.Manifest.Validate();
            hello.Limits.Validate();
            TransportValidation.Capabilities(hello.Capabilities);
            return RemoteErrorCode.None;
        }
        catch (ArgumentException) { return RemoteErrorCode.InvalidRequest; }
    }

    private async Task ServeSessionAsync(SslStream stream, IEncodedEnvelopeStore store,
        TransportLimits limits, EncodedStoreCapabilities capabilities, string alias,
        AuthenticatedIdentity? identity, DateTimeOffset sessionExpiresAt)
    {
        long lastId = 1;
        while (!_shutdown.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var idleBudget = _options.IdleTimeout;
            var sessionRemaining = sessionExpiresAt - _options.TimeProvider.GetUtcNow();
            if (sessionRemaining <= TimeSpan.Zero) return;
            if (sessionRemaining < idleBudget) idleBudget = sessionRemaining;
            if (identity is not null)
            {
                var remaining = identity.ExpiresAt - _options.TimeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero) return;
                if (remaining < idleBudget) idleBudget = remaining;
            }
            idle.CancelAfter(idleBudget);
            var message = await MessagePackFrameCodec.ReadAsync(stream, limits, idle.Token).ConfigureAwait(false);
            if (message is null) return;
            if (message.CorrelationId != lastId + 1 || message.StoreAlias is not null || message.Manifest is not null
                || message.Limits is not null || message.Capabilities != EncodedStoreCapabilities.None
                || message.Response is not null || message.Error != RemoteErrorCode.None)
                throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid request envelope or sequence.");
            lastId = message.CorrelationId;
            if (message.Kind == WireMessageKind.Close && message.Request is null && message.TimeoutMilliseconds == 0) return;
            if (message.Kind != WireMessageKind.Request || message.Request is null || message.TimeoutMilliseconds < 1)
                throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "A storage request is required.");
            var request = message.Request;
            TransportValidation.Request(request, limits);
            if (identity is not null && identity.ExpiresAt <= _options.TimeProvider.GetUtcNow())
            {
                await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.AuthenticationExpired).ConfigureAwait(false);
                return;
            }
            if (sessionExpiresAt <= _options.TimeProvider.GetUtcNow())
            {
                await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.DeadlineExceeded).ConfigureAwait(false);
                return;
            }
            if (identity is not null)
            {
                bool authorized;
                try { authorized = _options.AuthorizeIdentity!(identity, alias, request.RequiredCapability); }
                catch { authorized = false; }
                if (!authorized)
                {
                    await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.Unauthorized).ConfigureAwait(false);
                    continue;
                }
                // Authorization is application code and may outlast the token's remaining lifetime.
                if (identity.ExpiresAt <= _options.TimeProvider.GetUtcNow())
                {
                    await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.AuthenticationExpired).ConfigureAwait(false);
                    return;
                }
            }
            if (sessionExpiresAt <= _options.TimeProvider.GetUtcNow())
            {
                await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.DeadlineExceeded).ConfigureAwait(false);
                return;
            }
            if ((capabilities & request.RequiredCapability) != request.RequiredCapability)
            {
                await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.UnsupportedCapability).ConfigureAwait(false);
                continue;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var operationBudget = TimeSpan.FromMilliseconds(
                Math.Min(message.TimeoutMilliseconds, _options.MaximumOperationTimeout.TotalMilliseconds));
            var sessionBudget = sessionExpiresAt - _options.TimeProvider.GetUtcNow();
            if (sessionBudget < operationBudget) operationBudget = sessionBudget;
            if (operationBudget <= TimeSpan.Zero)
            {
                await SendReplyAsync(stream, limits, lastId, null, RemoteErrorCode.DeadlineExceeded).ConfigureAwait(false);
                return;
            }
            operation.CancelAfter(operationBudget);
            Task<EncodedStoreResponse>? execution = null;
            var error = RemoteErrorCode.None;
            EncodedStoreResponse? response = null;
            try
            {
                execution = store.ExecuteAsync(request, operation.Token).AsTask();
                response = await execution.WaitAsync(operation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(ex);
                // Once storage was invoked, generic failures cannot prove that a mutation did not commit.
                error = request.IsMutation ? RemoteErrorCode.OutcomeUnknown
                    : ex is OperationCanceledException ? RemoteErrorCode.DeadlineExceeded
                    : ex is RemoteStoreException remote ? remote.Code : RemoteErrorCode.StorageFailure;
            }
            try
            {
                await SendReplyAsync(stream, limits, lastId, response, error, request.IsMutation).ConfigureAwait(false);
            }
            finally
            {
                // A deadline may complete before the store's durable commit. Keep the alias leased
                // and the work counted against the connection bound until that execution finishes.
                if (execution is { IsCompleted: false })
                {
                    try { await execution.ConfigureAwait(false); }
                    catch (Exception ex) { Report(ex); }
                }
                // Even an uncertain outcome invalidates observers after storage has settled.
                if (request.IsMutation && execution is not null) PublishChange(alias, identity);
            }
            if (error is RemoteErrorCode.OutcomeUnknown or RemoteErrorCode.DeadlineExceeded) return;
        }
    }

    private async Task SendReplyAsync(SslStream stream, TransportLimits limits, long id,
        EncodedStoreResponse? response, RemoteErrorCode error, bool mutation = false)
    {
        byte[] frame;
        try
        {
            frame = MessagePackFrameCodec.Encode(new() { Kind = WireMessageKind.Response, CorrelationId = id,
                Response = response, Error = error }, limits);
        }
        catch (RemoteStoreException ex)
        {
            Report(ex);
            frame = MessagePackFrameCodec.Encode(new() { Kind = WireMessageKind.Response, CorrelationId = id,
                Error = mutation ? RemoteErrorCode.OutcomeUnknown : RemoteErrorCode.LimitExceeded }, limits);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(_options.MaximumOperationTimeout);
        await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
    }

    private void Report(Exception exception)
    {
        try { _options.OnError?.Invoke(exception); }
        catch { /* Diagnostics must not change storage or transport outcomes. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        foreach (var connection in _connections.Values) connection.Client.Dispose();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (Exception ex) { Report(ex); }
        await Task.WhenAll(_connections.Values.Select(connection => connection.Completion)).ConfigureAwait(false);
        // Gates intentionally remain valid for concurrent callers completing their finally blocks.
    }
}
