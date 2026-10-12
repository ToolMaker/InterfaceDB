namespace Idb.Transport;

public sealed partial class TcpEncodedEnvelopeStore
{
    internal async Task ReadChangesAsync(Action changed, CancellationToken token)
    {
        long sequence = 1;
        while (true)
        {
            var message = await MessagePackFrameCodec.ReadAsync(_stream, Limits, token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The notification source disconnected.");
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
