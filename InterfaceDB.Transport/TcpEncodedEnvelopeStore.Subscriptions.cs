namespace Idb.Transport;

public sealed partial class TcpEncodedEnvelopeStore
{
    // Scheduling hint only, after the host accepted authentication. Never used to authorize data.
    // A provider may supply expiry explicitly; JWT providers commonly only return the token text.
    private static DateTimeOffset? SubscriptionExpiry(Idb.Authentication.AccessToken? token)
    {
        if (token is null) return null;
        var expiry = token.ExpiresAt;
        try
        {
            var parts = token.Value.Split('.');
            if (parts.Length != 3) return FutureExpiry(expiry);
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            if (document.RootElement.TryGetProperty("exp", out var value) && value.TryGetInt64(out var seconds))
            {
                var jwtExpiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (expiry is null || jwtExpiry < expiry) expiry = jwtExpiry;
            }
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { }
        return FutureExpiry(expiry);
    }

    // An already stale provider hint cannot turn arbitrary later disconnects into a fast retry loop.
    private static DateTimeOffset? FutureExpiry(DateTimeOffset? expiry) => expiry > DateTimeOffset.UtcNow ? expiry : null;

    internal sealed class SubscriptionTokenExpiredException() : RemoteStoreException(RemoteErrorCode.AuthenticationExpired,
        "The subscription token expired; authenticate a new subscription.");

    internal async Task ReadChangesAsync(Action changed, CancellationToken token)
    {
        long sequence = 1;
        while (true)
        {
            var message = await MessagePackFrameCodec.ReadSubscriptionControlAsync(_stream, Limits, token).ConfigureAwait(false);
            if (message is null)
            {
                token.ThrowIfCancellationRequested();
                if (SubscriptionTokenExpiry is { } expiry)
                {
                    var remaining = expiry - DateTimeOffset.UtcNow;
                    // Host timers can fire just before the exact expiry tick. Wait out at most
                    // one second so a provider never reuses the same still-valid cached token.
                    if (remaining <= TimeSpan.FromSeconds(1))
                    {
                        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                        throw new SubscriptionTokenExpiredException();
                    }
                }
                throw new EndOfStreamException("The notification source disconnected.");
            }
            if (message.Kind != WireMessageKind.Changed || message.CorrelationId != checked(++sequence)
                || message.StoreAlias is not null || message.Manifest is not null || message.Limits is not null
                || message.Capabilities != 0 || message.Request is not null || message.Response is not null
                || message.Error != 0 || message.TimeoutMilliseconds != 0 || message.AccessToken is not null
                || message.AuthenticationMode != 0)
                throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid change notification.");
            changed();
        }
    }
}
