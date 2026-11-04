using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Idb.Commercial.Repositories.Peers;

namespace Idb.Transport.Peers;

/// <summary>Outbound TLS/JWT connections. The expected issuer/subject bind the local cache to the signed-in account.</summary>
public sealed class TcpPeerConnectionFactory : IPeerConnectionFactory
{
    private readonly TcpStoreClientOptions _options;
    private readonly PeerLimits _limits;
    private readonly string _account;
    private readonly PeerRegistrationOptions? _registration;
    public string? HistoryDateProperty => _registration?.DateProperty;
    public string Scope { get; }
    public TcpPeerConnectionFactory(TcpStoreClientOptions options, string expectedIssuer, string expectedSubject, PeerLimits? limits = null,
        PeerRegistrationOptions? registration = null)
    {
        _options = options; _limits = limits ?? new(); _limits.Validate();
        registration?.Validate(); _registration = registration;
        options.Manifest.Validate(); TransportValidation.Alias(options.StoreAlias);
        if (options.AuthenticationMode != TcpStoreAuthenticationMode.JwtBearer || options.TokenProvider is null
            || string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535)
            throw new ArgumentException("Peer connections require an endpoint and JWT token provider.");
        TransportValidation.Duration(options.ConnectTimeout, nameof(options.ConnectTimeout));
        _account = Account(expectedIssuer, expectedSubject);
        Scope = JsonSerializer.Serialize(new[] { options.Host, options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            options.TargetHost ?? options.Host, options.StoreAlias, Manifest(options.Manifest), _account });
        if (registration is not null) Scope += JsonSerializer.Serialize(registration);
    }
    internal static string Manifest(CompatibilityManifest manifest) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
    internal static string Account(string issuer, string subject)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("A validated issuer and subject are required.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { issuer, subject }))));
    }
    public async ValueTask<IPeerConnection> ConnectAsync(Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> handler,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ConnectTimeout);
        var token = await _options.TokenProvider!.GetAccessTokenAsync(deadline.Token).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(token.Value) > 16 * 1024) throw new PeerProtocolException(PeerError.InvalidRequest);
        var client = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, deadline.Token).ConfigureAwait(false);
            TransportValidation.ConfigureSubscriptionKeepAlive(client.Client);
            stream = new SslStream(client.GetStream(), false, _options.ServerCertificateValidation);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _options.TargetHost ?? _options.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            }, deadline.Token).ConfigureAwait(false);
            await PeerFrameCodec.WriteAsync(stream, new()
            {
                Command = PeerCommand.Hello, Dataset = _options.StoreAlias, Manifest = Manifest(_options.Manifest),
                AccessToken = token.Value, Limits = _limits, Registration = _registration
            }, _limits, deadline.Token).ConfigureAwait(false);
            var response = await PeerFrameCodec.ReadAsync(stream, _limits, deadline.Token).ConfigureAwait(false);
            if (response.Command != PeerCommand.Hello || !response.IsResponse) throw new PeerProtocolException(PeerError.InvalidRequest);
            if (response.Error != PeerError.None) throw new PeerProtocolException(response.Error);
            if (response.Manifest != Manifest(_options.Manifest) || response.Account != _account) throw new PeerProtocolException(PeerError.Unauthorized);
            if (response.Registration != _registration) throw new PeerProtocolException(PeerError.Incompatible);
            var negotiated = response.Limits ?? throw new PeerProtocolException(PeerError.InvalidRequest);
            negotiated.Validate();
            if (negotiated.MaximumPayloadBytes > _limits.MaximumPayloadBytes || negotiated.MaximumRecords > _limits.MaximumRecords
                || negotiated.PageSize > _limits.PageSize) throw new PeerProtocolException(PeerError.InvalidRequest);
            return new OwnedConnection(client, new PeerRpcConnection(stream, negotiated, handler, cancellationToken));
        }
        catch { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); client.Dispose(); throw; }
    }
    private sealed class OwnedConnection(TcpClient client, IPeerConnection connection) : IPeerConnection
    {
        public Task Completion => connection.Completion;
        public ValueTask<PeerMessage> RequestAsync(PeerMessage request, CancellationToken cancellationToken = default) => connection.RequestAsync(request, cancellationToken);
        public async ValueTask DisposeAsync() { await connection.DisposeAsync().ConfigureAwait(false); client.Dispose(); }
    }
}
