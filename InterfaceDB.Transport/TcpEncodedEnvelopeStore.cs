using Idb.Diagnostics;
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
    public static async ValueTask<CompatibilityManifest> DiscoverAsync(TcpStoreClientOptions options, CancellationToken token = default)
    {
        await using var discovery = await ConnectCoreAsync(options with
            { RequiredCapabilities = EncodedStoreCapabilities.Read | EncodedStoreCapabilities.DurableCommits
                | (options.RequiredCapabilities & TransportValidation.ScopeCapabilities) }, false, token, true).ConfigureAwait(false);
        return discovery.RemoteManifest;
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
        => TransportTelemetry.ObserveAsync(options.Telemetry, DatabaseComponent.TcpClient, DatabaseOperation.Connect,
                () => ConnectCoreAsyncUnobserved(options, subscription, cancellationToken, discovery), log: true, bytes: 0);

    private static async ValueTask<TcpEncodedEnvelopeStore> ConnectCoreAsyncUnobserved(TcpStoreClientOptions options,
        bool subscription, CancellationToken cancellationToken, bool discovery = false)
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
        TransportValidation.Alias(options.StoreAlias);
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
            stream = new SslStream(tcp.GetStream(), false, options.ServerCertificateValidation);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.TargetHost ?? options.Host,
                ClientCertificates = options.ClientCertificate is null ? new() : new() { options.ClientCertificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            }, deadline.Token).ConfigureAwait(false);
            await MessagePackFrameCodec.WriteAsync(stream, new WireMessage
            {
                Kind = discovery ? WireMessageKind.Discover : subscription ? WireMessageKind.Subscribe : WireMessageKind.Hello,
                CorrelationId = 1,
                StoreAlias = options.StoreAlias,
                Manifest = options.Manifest,
                Limits = options.Limits,
                Capabilities = options.RequiredCapabilities,
                AuthenticationMode = options.AuthenticationMode,
                AccessToken = token?.Value
            }, options.Limits, deadline.Token).ConfigureAwait(false);
            var reply = await MessagePackFrameCodec.ReadAsync(stream, options.Limits, deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The server closed during compatibility negotiation.");
            if (reply.Kind != WireMessageKind.HelloResult || reply.CorrelationId != 1
                || reply.Request is not null || reply.Response is not null || reply.StoreAlias is not null
                || reply.TimeoutMilliseconds != 0 || reply.AccessToken is not null)
                throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid handshake response.");
            ThrowRemoteError(reply.Error);
            if (reply.AuthenticationMode != options.AuthenticationMode
                || reply.Manifest is null || (!discovery && reply.Manifest != options.Manifest) || (discovery && reply.Manifest.ContractIdentity != options.Manifest.ContractIdentity) || reply.Limits is null
                || reply.Capabilities != options.RequiredCapabilities
                || reply.Limits != options.Limits.Intersect(reply.Limits))
                throw new RemoteStoreException(RemoteErrorCode.IncompatibleManifest, "The host did not accept the requested compatibility and resource limits.");
            reply.Limits.Validate();
            return new TcpEncodedEnvelopeStore(tcp, stream, options, reply.Limits, reply.Capabilities) { RemoteManifest = reply.Manifest };
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
        var telemetry = DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.TcpClient, request is null ? DatabaseOperation.Unknown : TransportTelemetry.Operation(request.Command), log: true);
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
                if (reply.Error == RemoteErrorCode.None) ValidateResponse(request, reply.Response!);
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

    private static void ValidateResponse(EncodedStoreRequest request, EncodedStoreResponse response)
    {
        bool invalid = response.AffectedCount < 0;
        switch (request.Command)
        {
            case EncodedStoreCommand.Changes:
                invalid |= response.Records is null || response.Records.Length > request.PageSize
                    || response.Payload is null || response.Payload.Length > 100
                    || response.AffectedCount != 0 || (response.Found && (response.NextAfterKey is not null || response.Records?.Length != 0))
                    || response.Records?.Any(record => record.Payload.Length != 0) == true;
                break;
            case EncodedStoreCommand.Get:
                invalid |= response.Found != (response.Payload is not null) || response.Records is not null
                    || response.NextAfterKey is not null || response.AffectedCount != 0;
                break;
            case EncodedStoreCommand.GetMany:
            case EncodedStoreCommand.Enumerate:
                invalid |= response.Records is null || response.Payload is not null || response.Found || response.AffectedCount != 0;
                if (request.Command == EncodedStoreCommand.GetMany)
                {
                    invalid |= response.NextAfterKey is not null || response.Records?.Length > request.Keys!.Length;
                    if (response.Records is not null)
                    {
                        var remaining = request.Keys!.GroupBy(key => key, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                        foreach (var record in response.Records)
                        {
                            if (!remaining.TryGetValue(record.Key, out var count) || count == 0) invalid = true;
                            else remaining[record.Key] = count - 1;
                        }
                    }
                }
                else
                {
                    invalid |= response.Records?.Length > request.PageSize;
                    var previous = request.AfterKey;
                    foreach (var record in response.Records ?? [])
                    {
                        if (previous is not null && StringComparer.Ordinal.Compare(previous, record.Key) >= 0) invalid = true;
                        previous = record.Key;
                    }
                    if (response.NextAfterKey is not null)
                        invalid |= response.Records is not { Length: > 0 }
                            || response.NextAfterKey != response.Records[^1].Key;
                }
                break;
            case EncodedStoreCommand.Put:
                invalid |= response.Found || response.AffectedCount != 1;
                goto default;
            case EncodedStoreCommand.Delete:
                invalid |= response.AffectedCount != (response.Found ? 1 : 0);
                goto default;
            case EncodedStoreCommand.ApplyBatch:
                invalid |= response.Found || response.AffectedCount > request.Mutations!.Length
                    || response.AffectedCount < request.Mutations.Count(mutation => mutation.Payload is not null);
                goto default;
            case EncodedStoreCommand.Checkpoint:
            case EncodedStoreCommand.Ping:
                invalid |= response.Found || response.AffectedCount != 0;
                goto default;
            default:
                invalid |= response.Payload is not null || response.Records is not null || response.NextAfterKey is not null;
                break;
        }
        if (invalid) throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid storage response shape.");
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
