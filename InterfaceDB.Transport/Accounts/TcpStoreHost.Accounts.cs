using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using Idb.Authentication;

namespace Idb.Transport;

public sealed partial class TcpStoreHost
{
    private async Task ServeAccountCreationAsync(SslStream stream, WireMessage message, CancellationToken token)
    {
        var error = RemoteErrorCode.Unauthorized;
        EncodedStoreResponse? response = null;
        bool invokedManager = false;
        try
        {
            if (_options.AuthenticationMode != TcpStoreAuthenticationMode.Provider || message.AuthenticationMode != TcpStoreAuthenticationMode.Provider
                || _options.AccountManager is null || _authentication is null) throw new UnauthorizedAccessException();
            if (message.CorrelationId != 1 || message.StoreAlias is not null || message.Manifest is not null || message.Limits is not null
                || message.Capabilities != 0 || message.Response is not null || message.Error != 0 || message.TimeoutMilliseconds != 0
                || message.RequestedScope is not null || message.ScopeIdentity is not null
                || message.Request is not { Command: EncodedStoreCommand.Put, Key: "create", Payload: { Length: > 0 and <= 8192 },
                    Keys: null, Mutations: null, AfterKey: null, PageSize: 0 })
                throw new ArgumentException("Invalid account request.");
            var requested = JsonSerializer.Deserialize<PasswordLogin>(message.Request.Payload) ?? throw new ArgumentException();
            _ = AccountCredentialValidation.NormalizeUsername(requested.Username);
            AccountCredentialValidation.ValidatePassword(requested.Password);
            AuthenticatedIdentity? actor = null;
            if (message.AccessToken is not null)
            {
                actor = await _authentication.AuthenticateAsync(ProviderAuthenticationWire.Decode(message.AccessToken), token).ConfigureAwait(false);
                if (!await SessionValidAsync(actor, token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
            }
            invokedManager = true;
            var account = await _options.AccountManager.CreateAsync(requested.Username, requested.Password, actor, token).ConfigureAwait(false);
            response = new() { Payload = JsonSerializer.SerializeToUtf8Bytes(account) };
            error = RemoteErrorCode.None;
        }
        catch (UnauthorizedAccessException) { error = RemoteErrorCode.Unauthorized; }
        catch (AuthenticationBusyException) { error = RemoteErrorCode.Busy; }
        catch (Exception exception) when (!invokedManager && exception is ArgumentException or JsonException)
        { error = RemoteErrorCode.InvalidRequest; }
        catch (AccountCreationRejectedException) { error = RemoteErrorCode.InvalidRequest; }
        catch (AccountStoreCapacityException) { error = RemoteErrorCode.LimitExceeded; }
        catch
        {
            error = invokedManager ? RemoteErrorCode.OutcomeUnknown : RemoteErrorCode.Unauthorized;
            Report(new AuthenticationException("Account management failed."));
        }
        // No aliases, payloads, credential records or underlying exceptions are disclosed.
        await MessagePackFrameCodec.WriteAsync(stream, new() { Kind = WireMessageKind.AccountResult, CorrelationId = 1,
            AuthenticationMode = TcpStoreAuthenticationMode.Provider, Error = error, Response = response }, _options.Limits, token).ConfigureAwait(false);
    }
}
