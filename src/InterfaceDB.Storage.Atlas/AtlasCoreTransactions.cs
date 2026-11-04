using Idb.Storage.AtlasCore.Internal;

namespace Idb.Storage.AtlasCore;

public enum AtlasCoreTransactionState
{
    Created = 0,
    Active = 1,
    Committing = 2,
    Committed = 3,
    RolledBack = 4,
    Faulted = 5,
    Disposed = 6
}

public sealed class AtlasCoreTransactionStateChangedEventArgs : EventArgs
{
    internal AtlasCoreTransactionStateChangedEventArgs(
        Guid transactionId,
        AtlasCoreTransactionState previousState,
        AtlasCoreTransactionState state,
        Exception? failure)
    {
        TransactionId = transactionId;
        PreviousState = previousState;
        State = state;
        Failure = failure;
    }

    public Guid TransactionId { get; }

    public AtlasCoreTransactionState PreviousState { get; }

    public AtlasCoreTransactionState State { get; }

    public Exception? Failure { get; }
}

public interface IAtlasCoreTransaction : IAsyncDisposable
{
    Guid Id { get; }

    AtlasCoreTransactionState State { get; }

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

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

internal readonly record struct AtlasCorePendingMutation(
    string Key,
    AtlasGroupRecordKind Kind,
    byte[] Payload);

internal sealed class AtlasCoreTransaction : IAtlasCoreTransaction
{
    private readonly AtlasCoreStore _store;
    private readonly int _maximumMutations;
    private readonly int _maximumTransactionRecordBytes;
    private readonly object _sync = new();
    private readonly Dictionary<string, AtlasCorePendingMutation> _mutations =
        new(StringComparer.Ordinal);
    private AtlasCoreTransactionState _state = AtlasCoreTransactionState.Active;
    private int _stagedEntriesLength;
    private int _disposed;

    public AtlasCoreTransaction(
        AtlasCoreStore store,
        int maximumMutations,
        int maximumTransactionRecordBytes)
    {
        _store = store;
        _maximumMutations = maximumMutations;
        _maximumTransactionRecordBytes = maximumTransactionRecordBytes;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }

    internal AtlasCoreStore Owner => _store;

    public AtlasCoreTransactionState State
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
        lock (_sync)
        {
            RequireActive();
            var nextEntriesLength = ValidateMutationAdmission(
                AtlasGroupRecordKind.Put,
                key,
                payload.Length);
            var mutation = new AtlasCorePendingMutation(
                key,
                AtlasGroupRecordKind.Put,
                payload.ToArray());
            _mutations[key] = mutation;
            _stagedEntriesLength = nextEntriesLength;
        }

        return ValueTask.CompletedTask;
    }

    // Ownership transfers only from trusted in-assembly serializers. Public callers
    // retain defensive-copy semantics and may reuse their buffers immediately.
    internal ValueTask SaveOwnedAsync(
        string key,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            RequireActive();
            StageMutation(new AtlasCorePendingMutation(
                key,
                AtlasGroupRecordKind.Put,
                payload));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> DeleteAsync(
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
                var existed = staged.Kind == AtlasGroupRecordKind.Put;
                StageMutation(new AtlasCorePendingMutation(
                    key,
                    AtlasGroupRecordKind.Delete,
                    []));
                return existed;
            }
        }

        var exists = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false) is not null;
        lock (_sync)
        {
            RequireActive();
            StageMutation(new AtlasCorePendingMutation(
                key,
                AtlasGroupRecordKind.Delete,
                []));
        }

        return exists;
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
                return staged.Kind == AtlasGroupRecordKind.Delete
                    ? null
                    : staged.Payload.ToArray();
            }
        }

        return await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        AtlasCorePendingMutation[] mutations;
        lock (_sync)
        {
            RequireActive();
            TransitionTo(AtlasCoreTransactionState.Committing, failure: null);
            mutations = _mutations.Values.ToArray();
        }

        try
        {
            await _store.CommitTransactionAsync(Id, mutations, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _mutations.Clear();
                _stagedEntriesLength = 0;
                TransitionTo(AtlasCoreTransactionState.Committed, failure: null);
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                TransitionTo(AtlasCoreTransactionState.Faulted, exception);
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
            _stagedEntriesLength = 0;
            TransitionTo(AtlasCoreTransactionState.RolledBack, failure: null);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_sync)
        {
            if (_state == AtlasCoreTransactionState.Active)
            {
                _mutations.Clear();
                _stagedEntriesLength = 0;
                TransitionTo(AtlasCoreTransactionState.RolledBack, failure: null);
            }

            var previous = _state;
            _state = AtlasCoreTransactionState.Disposed;
            _store.OnTransactionStateChanged(Id, previous, _state, failure: null);
        }

        return ValueTask.CompletedTask;
    }

    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_state != AtlasCoreTransactionState.Active)
        {
            throw new InvalidOperationException(
                $"Transaction {Id} is {_state} and can no longer accept operations.");
        }
    }

    private void StageMutation(AtlasCorePendingMutation mutation)
    {
        var nextEntriesLength = ValidateMutationAdmission(
            mutation.Kind,
            mutation.Key,
            mutation.Payload.Length);
        _mutations[mutation.Key] = mutation;
        _stagedEntriesLength = nextEntriesLength;
    }

    private int ValidateMutationAdmission(
        AtlasGroupRecordKind kind,
        string key,
        int payloadLength)
    {
        if (!_mutations.ContainsKey(key) && _mutations.Count >= _maximumMutations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                $"An AtlasCore transaction may contain at most {_maximumMutations:N0} distinct mutations.");
        }

        try
        {
            var nextEntriesLength = _stagedEntriesLength;
            if (_mutations.TryGetValue(key, out var previous))
            {
                nextEntriesLength = checked(
                    nextEntriesLength
                    - AtlasTransactionLog.CalculateTransactionEntryLength(
                        previous.Kind,
                        previous.Key,
                        previous.Payload.Length));
            }

            nextEntriesLength = checked(
                nextEntriesLength
                + AtlasTransactionLog.CalculateTransactionEntryLength(kind, key, payloadLength));
            AtlasTransactionLog.ValidateTransactionAdmission(
                nextEntriesLength,
                _maximumTransactionRecordBytes);
            return nextEntriesLength;
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadLength),
                $"The encoded AtlasCore transaction exceeds the supported size: {exception.Message}");
        }
    }

    private void TransitionTo(AtlasCoreTransactionState state, Exception? failure)
    {
        var previous = _state;
        _state = state;
        _store.OnTransactionStateChanged(Id, previous, state, failure);
    }
}
