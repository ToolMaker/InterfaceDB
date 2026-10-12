using System.Collections.Concurrent;
using System.Net.Security;
using System.Threading.Channels;
using Idb.Authentication;

namespace Idb.Transport;

public sealed partial class TcpStoreHost
{
    private sealed record Watch(string Alias, string Partition, Channel<byte> Queue);
    private readonly ConcurrentDictionary<long, Watch> _watches = new();

    /// <summary>Current registered remote listeners; useful for health checks and shutdown verification.</summary>
    public int ActiveSubscriptions => _watches.Count;

    private string Partition(AuthenticatedIdentity? identity) => _options.IsolateBySubject
        ? SubjectScopedEncodedStore.GetPrefix(identity!) : string.Empty;

    private void PublishChange(string alias, AuthenticatedIdentity? identity)
    {
        var partition = Partition(identity);
        foreach (var watch in _watches.Values)
            if (watch.Alias == alias && watch.Partition == partition) watch.Queue.Writer.TryWrite(0);
    }

    private async Task ServeSubscriptionAsync(long id, SslStream stream, TransportLimits limits,
        WireMessage hello, RegisteredStore store, AuthenticatedIdentity? identity, CancellationToken handshake)
    {
        var queue = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        using var expiry = identity is null ? null : _options.TimeProvider.CreateTimer(_ => { try { stop.Cancel(); } catch (ObjectDisposedException) { } }, null,
            identity.ExpiresAt - _options.TimeProvider.GetUtcNow() is var remaining && remaining > TimeSpan.Zero
                ? (remaining < TimeSpan.FromDays(30) ? remaining : TimeSpan.FromDays(30)) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        _watches[id] = new(hello.StoreAlias!, Partition(identity), queue);
        Task<WireMessage?>? close = null;
        try
        {
            // Registration precedes acknowledgement: the client's catch-up snapshot cannot miss a commit.
            await MessagePackFrameCodec.WriteAsync(stream, new()
            {
                Kind = WireMessageKind.HelloResult, CorrelationId = 1,
                Manifest = store.Registration.Manifest, Limits = limits, Capabilities = hello.Capabilities,
                AuthenticationMode = _options.AuthenticationMode
            }, limits, handshake).ConfigureAwait(false);
            close = MessagePackFrameCodec.ReadAsync(stream, limits, stop.Token).AsTask();
            long sequence = 1;
            while (!stop.IsCancellationRequested)
            {
                var pending = queue.Reader.WaitToReadAsync(stop.Token).AsTask();
                if (await Task.WhenAny(close, pending).ConfigureAwait(false) == close)
                {
                    var message = await close.ConfigureAwait(false);
                    if (message is not null && (message.Kind != WireMessageKind.Close || message.CorrelationId != 2
                        || message.Request is not null || message.Response is not null || message.StoreAlias is not null
                        || message.Manifest is not null || message.Limits is not null || message.AccessToken is not null
                        || message.Capabilities != 0 || message.TimeoutMilliseconds != 0 || message.Error != 0))
                        throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Only unsubscribe is permitted on a notification connection.");
                    return;
                }
                if (!await pending.ConfigureAwait(false)) return;
                queue.Reader.TryRead(out _);
                if (identity is not null)
                {
                    bool authorized;
                    try { authorized = _options.AuthorizeIdentity!(identity, hello.StoreAlias!, hello.Capabilities); }
                    catch { authorized = false; }
                    if (!authorized || identity.ExpiresAt <= _options.TimeProvider.GetUtcNow()) return;
                }
                using var send = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                send.CancelAfter(_options.MaximumOperationTimeout);
                await MessagePackFrameCodec.WriteAsync(stream, new()
                { Kind = WireMessageKind.Changed, CorrelationId = checked(++sequence) }, limits, send.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            _watches.TryRemove(id, out _);
            await stop.CancelAsync().ConfigureAwait(false);
            if (close is not null)
                try { await close.ConfigureAwait(false); } catch (Exception) { }
        }
    }
}
