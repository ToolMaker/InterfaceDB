using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore;

public enum SegmentCoreTransactionState
{
    Created = 0,
    Active = 1,
    Committing = 2,
    Committed = 3,
    RolledBack = 4,
    Faulted = 5,
    Disposed = 6
}

public sealed class SegmentCoreTransactionStateChangedEventArgs : EventArgs
{
    internal SegmentCoreTransactionStateChangedEventArgs(
        Guid transactionId,
        SegmentCoreTransactionState previousState,
        SegmentCoreTransactionState state,
        Exception? failure)
    {
        TransactionId = transactionId;
        PreviousState = previousState;
        State = state;
        Failure = failure;
    }

    public Guid TransactionId { get; }

    public SegmentCoreTransactionState PreviousState { get; }

    public SegmentCoreTransactionState State { get; }

    public Exception? Failure { get; }
}

public interface IStorageTransaction : IAsyncDisposable
{
    Guid Id { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

public interface ISegmentCoreTransaction : IStorageTransaction
{

    SegmentCoreTransactionState State { get; }

    int PendingMutationCount { get; }

    ValueTask SaveAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

}

internal readonly record struct SegmentCorePendingMutation(
    string Key,
    ObjectLogRecordKind Kind,
    byte[] Payload);

internal sealed class SegmentCoreTransaction : ISegmentCoreTransaction
{
    private readonly SegmentCoreStore _store;
    private readonly object _sync = new();
    private readonly Dictionary<string, SegmentCorePendingMutation> _mutations =
        new(StringComparer.Ordinal);
    private SegmentCoreTransactionState _state = SegmentCoreTransactionState.Active;
    private int _disposed;

    public SegmentCoreTransaction(SegmentCoreStore store)
    {
        _store = store;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    internal SegmentCoreStore Owner => _store;

    public SegmentCoreTransactionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public int PendingMutationCount
    {
        get
        {
            lock (_sync)
            {
                return _mutations.Count;
            }
        }
    }

    public ValueTask SaveAsync(
        string key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = payload.ToArray();
        lock (_sync)
        {
            RequireActive();
            _mutations[key] = new SegmentCorePendingMutation(
                key,
                ObjectLogRecordKind.Put,
                copy);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        bool existed;
        lock (_sync)
        {
            RequireActive();
            if (_mutations.TryGetValue(key, out var staged))
            {
                existed = staged.Kind == ObjectLogRecordKind.Put;
                _mutations[key] = new SegmentCorePendingMutation(
                    key,
                    ObjectLogRecordKind.Delete,
                    []);
                return existed;
            }
        }

        existed = await _store.ContainsKeyAsync(key, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            RequireActive();
            _mutations[key] = new SegmentCorePendingMutation(
                key,
                ObjectLogRecordKind.Delete,
                []);
        }

        return existed;
    }

    public async ValueTask<byte[]?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            RequireActive();
            if (_mutations.TryGetValue(key, out var staged))
            {
                return staged.Kind == ObjectLogRecordKind.Delete
                    ? null
                    : staged.Payload.ToArray();
            }
        }

        return await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        SegmentCorePendingMutation[] mutations;
        lock (_sync)
        {
            RequireActive();
            TransitionTo(SegmentCoreTransactionState.Committing, failure: null);
            mutations = _mutations.Values.ToArray();
        }

        try
        {
            await _store.CommitTransactionAsync(Id, mutations, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _mutations.Clear();
                TransitionTo(SegmentCoreTransactionState.Committed, failure: null);
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                TransitionTo(SegmentCoreTransactionState.Faulted, exception);
            }

            throw;
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            RequireActive();
            _mutations.Clear();
            TransitionTo(SegmentCoreTransactionState.RolledBack, failure: null);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_state == SegmentCoreTransactionState.Active)
            {
                _mutations.Clear();
                TransitionTo(SegmentCoreTransactionState.RolledBack, failure: null);
            }

            var previous = _state;
            _state = SegmentCoreTransactionState.Disposed;
            _store.OnTransactionStateChanged(Id, previous, _state, failure: null);
        }

        await ValueTask.CompletedTask;
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_state != SegmentCoreTransactionState.Active)
        {
            throw new InvalidOperationException(
                $"Transaction {Id} is {_state} and can no longer accept operations.");
        }
    }

    private void TransitionTo(SegmentCoreTransactionState state, Exception? failure)
    {
        var previous = _state;
        _state = state;
        _store.OnTransactionStateChanged(Id, previous, state, failure);
    }
}
