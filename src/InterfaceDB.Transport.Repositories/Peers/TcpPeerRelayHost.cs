using System.Collections.Concurrent;
using Idb.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Idb.Authentication;
using Idb.Commercial.Repositories.Peers;

namespace Idb.Transport.Peers;

public enum PeerAccess { Read, Write }
public sealed record PeerDatasetRegistration(string Alias, CompatibilityManifest Manifest, IPeerSequenceLedger Ledger)
{
    public IPeerRegistrationLedger? Registrations { get; init; }
    public string? HistoryDateProperty { get; init; }
}
public sealed record TcpPeerRelayHostOptions
{
    public IDatabaseTelemetry? Telemetry { get; init; }
    public IPEndPoint ListenEndPoint { get; init; } = new(IPAddress.Loopback, 7443);
    public required X509Certificate2 ServerCertificate { get; init; }
    public required IAccessTokenValidator TokenValidator { get; init; }
    public required Func<AuthenticatedIdentity, string, PeerAccess, bool> AuthorizeIdentity { get; init; }
    public Func<AuthenticatedIdentity, string, PeerHistoryPolicy?> HistoryPolicy { get; init; } = (_, _) => new();
    public required IReadOnlyList<PeerDatasetRegistration> Datasets { get; init; }
    public PeerLimits Limits { get; init; } = new();
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumSessionLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>One TLS listening port, many outbound device connections. Borrows the token validator and ledgers.</summary>
public sealed class TcpPeerRelayHost : IAsyncDisposable
{
    private readonly TcpPeerRelayHostOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, (TcpClient Client, Task Work)> _connections = new();
    private readonly Dictionary<string, (PeerDatasetRegistration Registration, PeerRelayCoordinator Coordinator)> _datasets = new(StringComparer.Ordinal);
    private Task _accept = Task.CompletedTask;
    private long _connectionId;
    private int _disposed;
    public IPEndPoint LocalEndPoint => (IPEndPoint)_listener.LocalEndpoint;
    private TcpPeerRelayHost(TcpPeerRelayHostOptions options)
    { _options = options; _listener = new(options.ListenEndPoint); _slots = new(options.Limits.MaximumPeers, options.Limits.MaximumPeers); }
    public static async ValueTask<TcpPeerRelayHost> StartAsync(TcpPeerRelayHostOptions options, IPeerRangePlanner? planner = null,
        CancellationToken cancellationToken = default, IPeerHistorySelector? historySelector = null)
    {
        options.Limits.Validate();
        ArgumentNullException.ThrowIfNull(options.TokenValidator); ArgumentNullException.ThrowIfNull(options.AuthorizeIdentity);
        ArgumentNullException.ThrowIfNull(options.HistoryPolicy); ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!options.ServerCertificate.HasPrivateKey || options.Datasets.Count == 0) throw new ArgumentException("A TLS certificate and datasets are required.");
        if (options.Datasets.Select(d => d.Ledger).Distinct(ReferenceEqualityComparer.Instance).Count() != options.Datasets.Count)
            throw new ArgumentException("Each dataset needs its own sequence ledger instance.");
        var registrations = options.Datasets.Select(d => d.Registrations).Where(r => r is not null).ToArray();
        if (registrations.Distinct(ReferenceEqualityComparer.Instance).Count() != registrations.Length)
            throw new ArgumentException("Each dataset needs its own registration ledger instance.");
        TransportValidation.Duration(options.HandshakeTimeout, nameof(options.HandshakeTimeout));
        TransportValidation.Duration(options.MaximumSessionLifetime, nameof(options.MaximumSessionLifetime));
        var host = new TcpPeerRelayHost(options);
        try
        {
            foreach (var registration in options.Datasets)
            {
                TransportValidation.Alias(registration.Alias); registration.Manifest.Validate();
                await registration.Ledger.InitializeAsync(registration.Alias + ":" + TcpPeerConnectionFactory.Manifest(registration.Manifest), cancellationToken).ConfigureAwait(false);
                if (registration.Registrations is not null)
                    await registration.Registrations.InitializeAsync(registration.Alias + ":" + TcpPeerConnectionFactory.Manifest(registration.Manifest), cancellationToken).ConfigureAwait(false);
                if (registration.HistoryDateProperty is not null && (string.IsNullOrWhiteSpace(registration.HistoryDateProperty)
                    || registration.HistoryDateProperty.Length > 256 || registration.Registrations is null))
                    throw new ArgumentException("A date-enabled dataset needs a valid property and durable registrations.");
                host._datasets.Add(registration.Alias, (registration, new(registration.Ledger, planner ?? new PeerRangePlanner(), options.Limits, host._stop.Token,
                    registration.Registrations, historySelector ?? new PeerHistorySelector(), options.TimeProvider, registration.HistoryDateProperty)));
            }
            host._listener.Start(); host._accept = Task.Run(host.AcceptAsync); return host;
        }
        catch { await host.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _connectionId);
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = RunAsync(id, client, start.Task);
                _connections[id] = (client, task); start.SetResult();
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Report(error); throw; }
    }
    private async Task RunAsync(long id, TcpClient client, Task start)
    {
        await start.ConfigureAwait(false);
        PeerRelayCoordinator? joined = null;
        var peerId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using (client)
            await using (var stream = new SslStream(client.GetStream(), false))
            {
                TransportValidation.ConfigureSubscriptionKeepAlive(client.Client);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(_options.HandshakeTimeout);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _options.ServerCertificate, ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, handshake.Token).ConfigureAwait(false);
                var hello = await PeerFrameCodec.ReadAsync(stream, _options.Limits with { MaximumPayloadBytes = 0 }, handshake.Token).ConfigureAwait(false);
                try
                {
                    if (hello.Command != PeerCommand.Hello || hello.IsResponse || hello.CorrelationId != 0
                        || string.IsNullOrWhiteSpace(hello.AccessToken) || hello.AccessToken.Length > 16 * 1024 || hello.Dataset is null
                        || hello.Payload is not null || hello.Record is not null || hello.Records is not null)
                        throw new PeerProtocolException(PeerError.InvalidRequest);
                    var identity = await _options.TokenValidator.ValidateAsync(hello.AccessToken, handshake.Token).ConfigureAwait(false);
                    var now = _options.TimeProvider.GetUtcNow();
                    if (identity.Principal.Identity?.IsAuthenticated != true || identity.ExpiresAt <= now
                        || !_options.AuthorizeIdentity(identity, hello.Dataset, PeerAccess.Read))
                        throw new PeerProtocolException(PeerError.Unauthorized);
                    if (!_datasets.TryGetValue(hello.Dataset, out var dataset)) throw new PeerProtocolException(PeerError.Unauthorized);
                    if (hello.Manifest != TcpPeerConnectionFactory.Manifest(dataset.Registration.Manifest)) throw new PeerProtocolException(PeerError.Incompatible);
                    var issuer = identity.Principal.FindFirst("iss")?.Value;
                    var subject = identity.Principal.FindFirst("sub")?.Value;
                    var account = TcpPeerConnectionFactory.Account(issuer!, subject!);
                    hello.Registration?.Validate();
                    if (_options.HistoryPolicy(identity, hello.Dataset)?.Allows(hello.Registration) != true)
                        throw new PeerProtocolException(PeerError.Unauthorized);
                    if (hello.Registration?.DateProperty != dataset.Registration.HistoryDateProperty)
                        throw new PeerProtocolException(PeerError.Incompatible);
                    var requested = hello.Limits ?? throw new PeerProtocolException(PeerError.InvalidRequest);
                    requested.Validate();
                    // Inventory batch size is shared across a dataset; incompatible clients fail before joining.
                    if (requested.PageSize < _options.Limits.PageSize || requested.MaximumPayloadBytes < _options.Limits.MaximumPayloadBytes
                        || requested.MaximumRecords < _options.Limits.MaximumRecords)
                        throw new PeerProtocolException(PeerError.Incompatible);
                    var registration = await dataset.Coordinator.RegisterAsync(hello.Registration, account, handshake.Token).ConfigureAwait(false);
                    await PeerFrameCodec.WriteAsync(stream, new() { Command = PeerCommand.Hello, IsResponse = true,
                        Manifest = hello.Manifest, Account = account, Limits = _options.Limits, Registration = hello.Registration }, _options.Limits, handshake.Token).ConfigureAwait(false);
                    using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var expires = identity.ExpiresAt - now;
                    session.CancelAfter(expires < _options.MaximumSessionLifetime ? expires : _options.MaximumSessionLifetime);
                    var ready = new TaskCompletionSource<PeerRelayEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using var connection = new PeerRpcConnection(stream, _options.Limits,
                        async (message, token) => await dataset.Coordinator.HandleAsync(await ready.Task.WaitAsync(token).ConfigureAwait(false), message, token).ConfigureAwait(false), session.Token);
                    var peer = new PeerRelayEndpoint(peerId, account, connection, access => !session.IsCancellationRequested
                        && identity.ExpiresAt > _options.TimeProvider.GetUtcNow() && _options.AuthorizeIdentity(identity, hello.Dataset, access)
                        && _options.HistoryPolicy(identity, hello.Dataset)?.Allows(hello.Registration) == true)
                    {
                        Registration = registration.State, ConnectedThrough = registration.Through,
                        Permitted = record => _options.HistoryPolicy(identity, hello.Dataset)?.Allows(record) == true
                    };
                    joined = dataset.Coordinator;
                    joined.Add(peer); ready.SetResult(peer);
                    await connection.Completion.ConfigureAwait(false);
                }
                catch (Exception error) when (joined is null)
                {
                    Report(error);
                    var code = error is PeerProtocolException p ? p.Error : PeerError.Unauthorized;
                    await PeerFrameCodec.WriteAsync(stream, new() { Command = PeerCommand.Hello, IsResponse = true, Error = code }, _options.Limits, handshake.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { if (!_stop.IsCancellationRequested) Report(error); }
        finally { joined?.Remove(peerId); _connections.TryRemove(id, out _); _slots.Release(); }
    }
    private void Report(Exception error)
        => DatabaseTelemetryScope.Start(_options.Telemetry, DatabaseComponent.TcpHost, DatabaseOperation.Connect, log: true)
            .Fail(error, error is PeerProtocolException protocol ? protocol.Error switch
            {
                PeerError.Unauthorized => DatabaseError.Denied,
                PeerError.Conflict => DatabaseError.Conflict,
                PeerError.InvalidRequest or PeerError.LimitExceeded => DatabaseError.InvalidInput,
                _ => DatabaseError.RemoteRejected
            } : DatabaseTelemetryScope.Classify(error));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener.Stop();
        foreach (var connection in _connections.Values) connection.Client.Dispose();
        try { await _accept.ConfigureAwait(false); } catch (Exception) when (_stop.IsCancellationRequested) { }
        await Task.WhenAll(_connections.Values.Select(c => c.Work)).ConfigureAwait(false);
        foreach (var dataset in _datasets.Values) await dataset.Coordinator.DisposeAsync().ConfigureAwait(false);
    }
}
