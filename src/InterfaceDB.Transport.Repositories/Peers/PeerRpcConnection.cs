using System.Collections.Concurrent;
using Idb.Commercial.Repositories.Peers;

namespace Idb.Transport.Peers;

/// <summary>One reader and a serialized writer, with independent requests in both directions.</summary>
internal sealed class PeerRpcConnection : IPeerConnection
{
    private readonly Stream _stream;
    private readonly PeerLimits _limits;
    private readonly Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> _handler;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _outgoing = new(16, 16);
    private readonly SemaphoreSlim _incoming = new(16, 16);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PeerMessage>> _pending = new();
    private readonly ConcurrentDictionary<long, Task> _handlers = new();
    private readonly CancellationTokenSource _stop;
    private readonly Task _reader;
    private long _nextId;
    private long _lastReceived;
    private int _disposed;
    internal PeerRpcConnection(Stream stream, PeerLimits limits, Func<PeerMessage, CancellationToken, ValueTask<PeerMessage>> handler,
        CancellationToken lifetime)
    {
        _stream = stream; _limits = limits; _handler = handler;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _reader = Task.Run(ReadLoopAsync);
    }
    public Task Completion => _reader;
    public async ValueTask<PeerMessage> RequestAsync(PeerMessage request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        timeout.CancelAfter(RequestBudget(request.Command));
        await _outgoing.WaitAsync(timeout.Token).ConfigureAwait(false);
        long id = 0;
        try
        {
            var completion = new TaskCompletionSource<PeerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Allocation and write share the lock so concurrently issued requests arrive in increasing order.
            await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                id = Interlocked.Increment(ref _nextId);
                _pending[id] = completion;
                await PeerFrameCodec.WriteAsync(_stream, request with { CorrelationId = id, IsResponse = false }, _limits, timeout.Token).ConfigureAwait(false);
            }
            catch { _stop.Cancel(); throw; }
            finally { _writer.Release(); }
            var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response.Command != request.Command) throw new InvalidDataException("Mismatched peer response.");
            if (response.Error != PeerError.None) throw new PeerProtocolException(response.Error);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        { throw new TimeoutException("Peer request timed out."); }
        finally { if (id != 0) _pending.TryRemove(id, out _); _outgoing.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await PeerFrameCodec.ReadAsync(_stream, _limits, _stop.Token).ConfigureAwait(false);
                if (message.CorrelationId <= 0 || message.Command == PeerCommand.Hello
                    || message.AccessToken is not null || message.Dataset is not null || message.Account is not null || message.Manifest is not null
                    || message.Registration is not null)
                    throw new InvalidDataException("Invalid established-session control fields.");
                if (message.IsResponse)
                {
                    if (_pending.TryRemove(message.CorrelationId, out var pending)) pending.TrySetResult(message);
                    continue;
                }
                if (message.CorrelationId <= _lastReceived || message.Error != PeerError.None)
                    throw new InvalidDataException("Replayed peer request.");
                _lastReceived = message.CorrelationId;
                if (!_incoming.Wait(0)) throw new PeerProtocolException(PeerError.LimitExceeded);
                var task = DispatchAsync(message);
                _handlers[message.CorrelationId] = task;
                _ = task.ContinueWith(_ => _handlers.TryRemove(message.CorrelationId, out var removed),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel();
            foreach (var pending in _pending.Values) pending.TrySetException(new EndOfStreamException("Peer connection ended."));
        }
    }
    private async Task DispatchAsync(PeerMessage request)
    {
        try
        {
            PeerMessage response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(RequestBudget(request.Command));
            try { response = await _handler(request, timeout.Token).ConfigureAwait(false); }
            catch (PeerProtocolException e) { response = new() { Error = e.Error }; }
            catch (OperationCanceledException) { response = new() { Error = PeerError.Unavailable }; }
            catch { response = new() { Error = PeerError.Failure }; }
            await _writer.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { await PeerFrameCodec.WriteAsync(_stream, response with { Command = request.Command, CorrelationId = request.CorrelationId, IsResponse = true }, _limits, _stop.Token).ConfigureAwait(false); }
            finally { _writer.Release(); }
        }
        catch { _stop.Cancel(); }
        finally { _incoming.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch (IOException) { }
        try { await _reader.ConfigureAwait(false); } catch (Exception) { /* Completion exposes the connection failure to its owner. */ }
        await Task.WhenAll(_handlers.Values).ConfigureAwait(false);
    }

    private TimeSpan RequestBudget(PeerCommand command) => command == PeerCommand.Reconcile
        ? _limits.ReconciliationTimeout : _limits.RequestTimeout;
}
