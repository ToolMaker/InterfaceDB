using Idb.Diagnostics;
using Idb.Authentication;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Idb.Transport;

/// <summary>
/// A TLS session bound to one configured store alias. Requests are serialized; a failed session
/// must be explicitly replaced. Mutations are never automatically replayed.
/// </summary>
public sealed partial class TcpEncodedEnvelopeStore : IEncodedEnvelopeStore
{
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TimeSpan _operationTimeout;
    private readonly IDatabaseTelemetry? _telemetry;
    private long _correlationId = 1;
    private int _disposed;
    private int _broken;
    private Guid DiagnosticConnectionId { get; init; }
    private DateTimeOffset? SubscriptionTokenExpiry { get; init; }

    private TcpEncodedEnvelopeStore(TcpClient tcp, SslStream stream, TcpStoreClientOptions options,
        TransportLimits limits, EncodedStoreCapabilities capabilities)
    {
        _tcp = tcp;
        _stream = stream;
        _operationTimeout = options.OperationTimeout;
        _telemetry = options.Telemetry;
        Limits = limits;
        Capabilities = capabilities;
    }

    public CompatibilityManifest RemoteManifest { get; private init; } = null!;
    public string ScopeIdentity { get; private init; } = null!;
    public AccountIdentity? AccountIdentity { get; private init; }
    public static async ValueTask<CompatibilityManifest> DiscoverAsync(TcpStoreClientOptions options, CancellationToken token = default)
        => (await DiscoverStoreAsync(options, token).ConfigureAwait(false)).Manifest;

    public static async ValueTask<TcpStoreDescriptor> DiscoverStoreAsync(TcpStoreClientOptions options, CancellationToken token = default)
    {
        await using var discovery = await ConnectCoreAsync(options with
            { RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits }, false, token, true).ConfigureAwait(false);
        return new(discovery.RemoteManifest, discovery.ScopeIdentity) { AccountIdentity = discovery.AccountIdentity };
    }
    public TransportLimits Limits { get; }
    public EncodedStoreCapabilities Capabilities { get; }

    public static ValueTask<TcpEncodedEnvelopeStore> ConnectAsync(TcpStoreClientOptions options,
        CancellationToken cancellationToken = default) => ConnectCoreAsync(options, false, cancellationToken);

    internal static ValueTask<TcpEncodedEnvelopeStore> ConnectCoreAsync(TcpStoreClientOptions options,
        bool subscription, CancellationToken cancellationToken, bool discovery = false)
        => options is null ? ValueTask.FromException<TcpEncodedEnvelopeStore>(new ArgumentNullException(nameof(options)))
            : !DatabaseTelemetryOperation.IsEnabled(options.Telemetry) ? ConnectCoreAsyncUnobserved(options, subscription, cancellationToken, discovery)
            : ConnectCoreAsyncObserved(options, subscription, cancellationToken, discovery);

    private static ValueTask<TcpEncodedEnvelopeStore> ConnectCoreAsyncObserved(TcpStoreClientOptions options,
        bool subscription, CancellationToken cancellationToken, bool discovery = false)
    {
        var id = Guid.NewGuid();
        return TransportTelemetry.ObserveAsync(options.Telemetry, DatabaseComponent.TcpClient, DatabaseOperation.Connect,
                () => ConnectCoreAsyncUnobserved(options, subscription, cancellationToken, discovery, id), log: true, connectionId: id);
    }

    private static async ValueTask<TcpEncodedEnvelopeStore> ConnectCoreAsyncUnobserved(TcpStoreClientOptions options,
        bool subscription, CancellationToken cancellationToken, bool discovery = false, Guid diagnosticId = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        ArgumentNullException.ThrowIfNull(options.Manifest);
        ArgumentNullException.ThrowIfNull(options.Limits);
        if (!Enum.IsDefined(options.AuthenticationMode)) throw new ArgumentOutOfRangeException(nameof(options.AuthenticationMode));
        if (TransportValidation.RequiresCertificate(options.AuthenticationMode) && options.ClientCertificate is null)
            throw new ArgumentException("This authentication mode requires a client certificate.");
        if (options.ClientCertificate is { HasPrivateKey: false }) throw new ArgumentException("The client certificate requires a private key.");
        if (!TransportValidation.RequiresToken(options.AuthenticationMode) && options.TokenProvider is not null)
            throw new ArgumentException("A token provider requires a JWT authentication mode.");
        if (options.AuthenticationMode == TcpStoreAuthenticationMode.Provider) ArgumentNullException.ThrowIfNull(options.CredentialsProvider);
        else if (options.CredentialsProvider is not null || options.ExpectedAccountIdentity is not null)
            throw new ArgumentException("Provider credentials and account binding require Provider mode.");
        TransportValidation.Alias(options.StoreAlias);
        if (options.RequestedScope is not null) TransportValidation.ControlString(options.RequestedScope, false);
        if (options.ExpectedScopeIdentity is not null) TransportValidation.ControlString(options.ExpectedScopeIdentity, false);
        options.Manifest.Validate();
        options.Limits.Validate();
        TransportValidation.Capabilities(options.RequiredCapabilities);
        TransportValidation.Duration(options.ConnectTimeout, nameof(options.ConnectTimeout));
        TransportValidation.Duration(options.OperationTimeout, nameof(options.OperationTimeout));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ConnectTimeout);
        var tcp = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            if (subscription) TransportValidation.ConfigureSubscriptionKeepAlive(tcp.Client);
            var token = options.TokenProvider is null ? null
                : await options.TokenProvider.GetAccessTokenAsync(deadline.Token).ConfigureAwait(false);
            if (token is not null && (string.IsNullOrWhiteSpace(token.Value)
                || System.Text.Encoding.UTF8.GetByteCount(token.Value) > TransportValidation.MaximumAccessTokenBytes))
                throw new ArgumentException("The access token is missing or exceeds the transport limit.");
            await tcp.ConnectAsync(options.Host, options.Port, deadline.Token).ConfigureAwait(false);
            var transport = new TlsHandshakeStream(tcp.GetStream());
            stream = new SslStream(transport, false, options.ServerCertificateValidation);
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = options.TargetHost ?? options.Host,
                    ClientCertificates = options.ClientCertificate is null ? new() : new() { options.ClientCertificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }, deadline.Token).ConfigureAwait(false);
            }
            catch (IOException exception) when (exception.GetType() == typeof(IOException)
                && exception.InnerException is null && transport.PeerClosedWithoutResponse)
            {
                // A host can accept TCP just before stopping and close before replying to TLS.
                // Only an observed empty close is an outage: TLS alerts, certificate rejection,
                // partial handshakes and other IO failures retain their original classification.
                throw new EndOfStreamException("The server closed before sending a TLS response.", exception);
            }
            transport.CompleteHandshake();
            var authenticationData = options.AuthenticationMode == TcpStoreAuthenticationMode.Provider
                ? ProviderAuthenticationWire.Encode(await options.CredentialsProvider!.GetCredentialsAsync(deadline.Token).ConfigureAwait(false)) : token?.Value;
            await MessagePackFrameCodec.WriteAsync(stream, new WireMessage
            {
                Kind = discovery ? WireMessageKind.Discover : subscription ? WireMessageKind.Subscribe : WireMessageKind.Hello,
                CorrelationId = 1,
                StoreAlias = options.StoreAlias,
                Manifest = options.Manifest,
                Limits = options.Limits,
                Capabilities = options.RequiredCapabilities,
                AuthenticationMode = options.AuthenticationMode,
                AccessToken = authenticationData, RequestedScope = options.RequestedScope, ScopeIdentity = options.ExpectedScopeIdentity
            }, options.Limits, deadline.Token).ConfigureAwait(false);
            var reply = await MessagePackFrameCodec.ReadAsync(stream, options.Limits, deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The server closed during compatibility negotiation.");
            if (reply.Kind != WireMessageKind.HelloResult || reply.CorrelationId != 1
                || reply.Request is not null || reply.Response is not null || reply.StoreAlias is not null
                || reply.TimeoutMilliseconds != 0 || (reply.AccessToken is not null && options.AuthenticationMode != TcpStoreAuthenticationMode.Provider) || reply.RequestedScope is not null)
                throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid handshake response.");
            ThrowRemoteError(reply.Error);
            if (string.IsNullOrWhiteSpace(reply.ScopeIdentity) || (options.ExpectedScopeIdentity is not null && options.ExpectedScopeIdentity != reply.ScopeIdentity)
                || reply.AuthenticationMode != options.AuthenticationMode
                || reply.Manifest is null || (!discovery && reply.Manifest != options.Manifest) || (discovery && reply.Manifest.ContractIdentity != options.Manifest.ContractIdentity) || reply.Limits is null
                || reply.Capabilities != options.RequiredCapabilities
                || reply.Limits != options.Limits.Intersect(reply.Limits))
                throw new RemoteStoreException(RemoteErrorCode.IncompatibleManifest, "The host did not accept the requested compatibility and resource limits.");
            reply.Limits.Validate();
            var account = options.AuthenticationMode == TcpStoreAuthenticationMode.Provider ? ProviderAuthenticationWire.ReadReceipt(reply.AccessToken) : null;
            if (options.ExpectedAccountIdentity is not null && options.ExpectedAccountIdentity != account)
                throw new UnauthorizedAccessException("The connection authenticated a different account.");
            return new TcpEncodedEnvelopeStore(tcp, stream, options, reply.Limits, reply.Capabilities)
                { RemoteManifest = reply.Manifest, ScopeIdentity = reply.ScopeIdentity, AccountIdentity = account,
                    DiagnosticConnectionId = diagnosticId, SubscriptionTokenExpiry = subscription ? SubscriptionExpiry(token) : null };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
            tcp.Dispose();
            throw new TimeoutException("Connecting to the remote store timed out.", ex);
        }
        catch
        {
            stream?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    public async ValueTask<EncodedStoreResponse> ExecuteAsync(EncodedStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        var telemetry = DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.TcpClient, request is null ? DatabaseOperation.Unknown : TransportTelemetry.Operation(request.Command), log: true).WithConnection(DiagnosticConnectionId);
        try
        {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(request);
        TransportValidation.Request(request, Limits);
        if ((Capabilities & request.RequiredCapability) != request.RequiredCapability)
            throw new RemoteStoreException(RemoteErrorCode.UnsupportedCapability, "The negotiated store does not support this operation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        var acquired = false;
        var sent = false;
        try
        {
            await _requestGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            ThrowIfUnavailable();
            var id = checked(_correlationId + 1);
            // Serialization finishes before the first network write. Validation/encoding failure cannot mutate storage.
            var frame = MessagePackFrameCodec.Encode(new WireMessage
            {
                Kind = WireMessageKind.Request, CorrelationId = id, Request = request,
                TimeoutMilliseconds = checked((int)Math.Ceiling(_operationTimeout.TotalMilliseconds))
            }, Limits);
            deadline.Token.ThrowIfCancellationRequested();
            WireMessage reply;
            try
            {
                sent = true;
                _correlationId = id;
                await _stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
                reply = await MessagePackFrameCodec.ReadAsync(_stream, Limits, deadline.Token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The remote store closed before acknowledging the operation.");
                if (reply.Kind != WireMessageKind.Response || reply.CorrelationId != id
                    || reply.StoreAlias is not null || reply.Manifest is not null || reply.Limits is not null
                    || reply.Capabilities != EncodedStoreCapabilities.None || reply.Request is not null
                    || reply.TimeoutMilliseconds != 0
                    || (reply.Error == RemoteErrorCode.None) != (reply.Response is not null))
                    throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid or mismatched operation response.");
                if (reply.Error == RemoteErrorCode.None) TransportValidation.Response(request, reply.Response!);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                BreakConnection();
                if (request.IsMutation) throw new RemoteCommitOutcomeUnknownException(ex);
                throw;
            }

            if (reply.Error is RemoteErrorCode.OutcomeUnknown or RemoteErrorCode.ProtocolError or RemoteErrorCode.DeadlineExceeded
                or RemoteErrorCode.AuthenticationExpired or RemoteErrorCode.AuthenticationRequired)
                BreakConnection();
            ThrowRemoteError(reply.Error);
            telemetry.Complete(frame.Length);
            return reply.Response!;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (sent && request.IsMutation) throw new RemoteCommitOutcomeUnknownException(ex);
            throw new TimeoutException("The remote store operation timed out.", ex);
        }
        finally
        {
            if (acquired) _requestGate.Release();
        }
        }
        catch (Exception exception) { TransportTelemetry.Fail(telemetry, exception); throw; }
    }

    private static void ThrowRemoteError(RemoteErrorCode code)
    {
        if (code == RemoteErrorCode.None) return;
        if (code == RemoteErrorCode.OutcomeUnknown) throw new RemoteCommitOutcomeUnknownException();
        throw new RemoteStoreException(code, $"The remote store rejected the operation ({code}).");
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _broken) != 0)
            throw new IOException("The remote session has failed. Open a new connection and reconcile any uncertain mutation.");
    }

    private void BreakConnection()
    {
        Interlocked.Exchange(ref _broken, 1);
        _tcp.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _tcp.Dispose();
            _stream.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
