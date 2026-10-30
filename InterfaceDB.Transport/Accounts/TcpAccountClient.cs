using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Idb.Authentication;

namespace Idb.Transport;

/// <summary>Explicit account-registration endpoint. No automatic retry; a lost acknowledgement can mean the account exists.</summary>
public static class TcpAccountClient
{
    public static async ValueTask<AccountIdentity> CreateAsync(TcpStoreClientOptions connection, string username, string password,
        IClientCredentialsProvider? creator = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.AuthenticationMode != TcpStoreAuthenticationMode.Provider) throw new ArgumentException("Account creation requires Provider mode.");
        _ = AccountCredentialValidation.NormalizeUsername(username);
        AccountCredentialValidation.ValidatePassword(password);
        ArgumentException.ThrowIfNullOrWhiteSpace(connection.Host);
        if (connection.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(connection.Port));
        TransportValidation.Duration(connection.ConnectTimeout, nameof(connection.ConnectTimeout));
        connection.Limits.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(connection.ConnectTimeout);
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(connection.Host, connection.Port, deadline.Token).ConfigureAwait(false);
        using var stream = new SslStream(tcp.GetStream(), false, connection.ServerCertificateValidation);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = connection.TargetHost ?? connection.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online
        }, deadline.Token).ConfigureAwait(false);
        var credential = creator is null ? null : ProviderAuthenticationWire.Encode(await creator.GetCredentialsAsync(deadline.Token).ConfigureAwait(false));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new PasswordLogin(username, password));
        if (payload.Length > 8192) throw new ArgumentException("Account request exceeds its bound.");
        var frame = MessagePackFrameCodec.Encode(new() { Kind = WireMessageKind.CreateAccount, CorrelationId = 1,
            AuthenticationMode = TcpStoreAuthenticationMode.Provider, AccessToken = credential,
            Request = new() { Command = EncodedStoreCommand.Put, Key = "create", Payload = payload } }, connection.Limits);
        WireMessage reply;
        try
        {
            await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
            reply = await MessagePackFrameCodec.ReadAsync(stream, connection.Limits, Math.Min(connection.Limits.MaximumFrameBytes, TransportValidation.MaximumHelloFrameBytes), deadline.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException();
            if (reply.Kind != WireMessageKind.AccountResult || reply.CorrelationId != 1 || reply.AuthenticationMode != TcpStoreAuthenticationMode.Provider
                || reply.StoreAlias is not null || reply.Manifest is not null || reply.Limits is not null || reply.Capabilities != 0
                || reply.Request is not null || reply.TimeoutMilliseconds != 0 || reply.AccessToken is not null
                || reply.RequestedScope is not null || reply.ScopeIdentity is not null
                || (reply.Error == RemoteErrorCode.None) != (reply.Response is not null)) throw new InvalidDataException();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { throw new RemoteCommitOutcomeUnknownException(exception); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame); System.Security.Cryptography.CryptographicOperations.ZeroMemory(payload); }
        if (reply.Error != RemoteErrorCode.None) throw new RemoteStoreException(reply.Error, "Account creation was not acknowledged as successful.");
        return ProviderAuthenticationWire.ReadReceipt(reply.Response?.Payload is { } bytes ? Encoding.UTF8.GetString(bytes) : null);
    }
}
