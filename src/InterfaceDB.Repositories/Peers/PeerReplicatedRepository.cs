using System.Runtime.CompilerServices;
using Idb.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.Peers;

/// <summary>Local-first writes, client-owned reconnect, and relay-coordinated peer reconciliation.
/// Owns its journal/connections/subscriptions; borrows the injected local repository and codec.</summary>
public sealed partial class PeerReplicatedRepository<T> : IRepository<T>, IRepositoryNotifications<T>, IAsyncDisposable
    where T : class, IStoredContract<T>
{
    private readonly IEnvelopeMetadataRepository<T> _local;
    private readonly IRepositoryNotifications<T> _notifications;
    private readonly IResilientEntityMapper<T> _mapper;
    private readonly ISequenceAccessor<T> _sequences;
    private readonly IPeerPayloadCodec<T> _codec;
    private readonly IPeerJournal _journal;
    private readonly IPeerConnectionFactory _transport;
    private readonly IDatabaseTelemetry? _telemetry;
    private readonly PeerLimits _limits;
    private readonly TimeSpan _retry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, PeerJournalEntry> _entries = new(StringComparer.Ordinal);
    private RepositoryNotificationAdapter<T>? _subscription;
    private Task? _worker;
    private IPeerConnection? _connection;
    private bool _initialized;
    private int _disposed;
    private Exception? _projectionFailure;
    private PeerSyncStatus _status = new(0, 0);
    public RepositoryConnection Connection { get; } = new();
    public PeerSyncStatus Synchronization => Volatile.Read(ref _status);
    public ContractRepositoryType RepositoryType => _local.RepositoryType;
    public string? RunningVersion => _local.RunningVersion;

    public PeerReplicatedRepository(IEnvelopeMetadataRepository<T> local, IRepositoryNotifications<T> notifications,
        IResilientEntityMapper<T> mapper, ISequenceAccessor<T> sequences, IPeerPayloadCodec<T> codec,
        IPeerJournal journal, IPeerConnectionFactory transport, PeerLimits? limits = null, TimeSpan? retryDelay = null,
        IDatabaseTelemetry? telemetry = null)
    {
        _local = local; _notifications = notifications; _mapper = mapper; _sequences = sequences;
        _codec = codec; _journal = journal; _transport = transport; _limits = limits ?? new();
        _telemetry = telemetry;
        _limits.Validate(); _retry = retryDelay ?? TimeSpan.FromSeconds(5);
        if (_retry < TimeSpan.FromMilliseconds(10) || _retry > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (local.CommitDurability != StorageDurabilityMode.Durable) throw new ArgumentException("Peer replication requires durable local commits.");
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) { CheckProjection(); return; }
            await _local.InitializeAsync(cancellationToken).ConfigureAwait(false);
            RepositoryDataAvailability.RequireComplete(_local);
            await _journal.InitializeAsync(_transport.Scope, cancellationToken).ConfigureAwait(false);
            foreach (var entry in await _journal.ReadAsync(cancellationToken).ConfigureAwait(false)) _entries[entry.RecordId] = entry;
            await foreach (var entity in _local.QueryManyAsync(_ => true, cancellationToken).ConfigureAwait(false))
                if (!_entries.ContainsKey(_mapper.GetStorageKey(entity)))
                    throw new InvalidOperationException("Use an empty local store or its matching peer journal; existing records require an explicit import.");
            _subscription = new(_notifications, () => { Wake(); return ValueTask.CompletedTask; }, _stop.Token);
            await _subscription.Ready.ConfigureAwait(false);
            foreach (var entry in _entries.Values) await ProjectAsync(entry.Pending ?? entry.Publishing ?? entry.Committed!, null, true, cancellationToken).ConfigureAwait(false);
            _status = new(0, 0, PendingCount());
            _initialized = true;
            _worker = Task.Run(RunAsync);
        }
        catch { _subscription?.Dispose(); _subscription = null; throw; }
        finally { _gate.Release(); }
    }

    private void Wake() => _wake.Writer.TryWrite(true);
    private int PendingCount() => _entries.Values.Count(e => e.Pending is not null || e.Publishing is not null);
    private async ValueTask PersistAsync(PeerJournalEntry entry, CancellationToken token)
    {
        try { await _journal.PutAsync(entry, token).ConfigureAwait(false); }
        catch (PeerProtocolException e) when (e.Error == PeerError.LimitExceeded) { throw; }
        catch (Exception e) { _projectionFailure = e; throw; }
    }
    private void CheckProjection()
    {
        if (_projectionFailure is { } failure) throw new InvalidOperationException("Local projection failed; reopen to recover the durable journal.", failure);
    }
    public async ValueTask<T> SaveAsync(T entityToSave, CancellationToken cancellationToken = default)
        => (await SaveCoreAsync(entityToSave, false, cancellationToken).ConfigureAwait(false))!;
    public async ValueTask<bool> DeleteAsync(T entityToDelete, CancellationToken cancellationToken = default)
        => await SaveCoreAsync(entityToDelete, true, cancellationToken).ConfigureAwait(false) is not null;
    public async ValueTask<IReadOnlyList<T>> SaveManyAsync(IEnumerable<T> entitiesToSave, CancellationToken cancellationToken = default)
    {
        var inputs = entitiesToSave.Take(_limits.PageSize + 1).ToArray();
        if (inputs.Length > _limits.PageSize) throw new PeerProtocolException(PeerError.LimitExceeded);
        var results = new List<T>();
        foreach (var entity in inputs) results.Add(await SaveAsync(entity, cancellationToken).ConfigureAwait(false));
        return results;
    }
    private async ValueTask<T?> SaveCoreAsync(T entity, bool deleted, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Callback.Value is { Active: true } callback && ReferenceEquals(callback.Owner, this)) throw new RepositoryFeedbackLoopException();
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckProjection();
            var id = _mapper.GetStorageKey(entity);
            var existing = await FindLocalAsync(id, token).ConfigureAwait(false);
            if (existing is null && deleted) return null;
            if (existing is not null && (entity.Envelope is null
                || existing.Envelope!.RevisionTimestampUtcMicroseconds != entity.Envelope.RevisionTimestampUtcMicroseconds))
                throw new PeerProtocolException(PeerError.Conflict);
            if (existing is null && entity.Envelope is not null) throw new PeerProtocolException(PeerError.Conflict);
            _entries.TryGetValue(id, out var before);
            if (existing is not null && before is null) throw new InvalidOperationException("The peer repository requires exclusive ownership of local writes. Import existing data explicitly.");
            var mutation = Guid.NewGuid();
            var historyDate = _codec.GetHistoryDate(entity);
            var previousDate = (before?.Pending ?? before?.Publishing ?? before?.Committed)?.Descriptor.HistoryDateUtc;
            if (before is not null && historyDate != previousDate)
                throw new ArgumentException("The history date is immutable after the first local save.");
            var payload = deleted ? [] : await _codec.EncodeAsync(entity, mutation, token).ConfigureAwait(false);
            var descriptor = new PeerRecordDescriptor(id, mutation, before?.Pending?.Descriptor.BaseSequence
                ?? before?.Publishing?.Descriptor.BaseSequence ?? before?.Committed?.Descriptor.Sequence ?? 0, 0, deleted, Convert.ToHexString(SHA256.HashData(payload)))
                { HistoryDateUtc = historyDate };
            descriptor.Validate(false);
            var pending = new PeerRecord(descriptor, payload);
            var entry = new PeerJournalEntry(id, before?.Committed, pending, before?.Publishing);
            await PersistAsync(entry, token).ConfigureAwait(false);
            _entries[id] = entry;
            T? saved;
            try { saved = await ProjectAsync(pending, _mapper.CreateDetachedCopy(entity), false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { _projectionFailure = exception; throw; }
            _status = _status with { PendingRecords = PendingCount() };
            Wake();
            return deleted ? entity : saved;
        }
        finally { _gate.Release(); }
    }

    private ValueTask<T?> FindLocalAsync(string id, CancellationToken token)
        => _local.QueryFirstAsync(entity => _mapper.GetStorageKey(entity) == id, token);
    private async ValueTask<T?> ProjectAsync(PeerRecord record, T? decoded, bool replicated, CancellationToken token, bool preserveRevision = false)
    {
        var header = record.Descriptor;
        var existing = await FindLocalAsync(header.RecordId, token).ConfigureAwait(false);
        var stamp = System.Text.Encoding.UTF8.GetBytes($"IDB-PEER-1:{header.MutationId:N}:{header.Sequence}");
        if (existing?.Envelope?.ExtensionMetadata is { } previous && previous.AsSpan().SequenceEqual(stamp)) return existing;
        using var scope = replicated ? _subscription?.BeginWriteScope() : null;
        if (header.Deleted)
        {
            if (existing is not null) await _local.DeleteAsync(existing, token).ConfigureAwait(false);
            return null;
        }
        var candidate = decoded ?? _codec.Decode(record);
        _sequences.Set(candidate, header.Sequence == 0 ? null : header.Sequence);
        return await _local.SaveWithExtensionMetadataAsync(candidate, existing?.Envelope?.EnvelopeCacheKey, stamp,
            new EnvelopeWriteCondition(existing?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0,
                preserveRevision && existing is not null ? EnvelopeRevisionAction.Import : EnvelopeRevisionAction.Advance,
                preserveRevision ? existing?.Envelope?.RevisionTimestampUtcMicroseconds ?? 0 : 0, ExpectedMissing: existing is null), token).ConfigureAwait(false);
    }

    public async ValueTask<PeerSyncStatus> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var connection = Volatile.Read(ref _connection) ?? throw new PeerProtocolException(PeerError.Unavailable);
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var conflicts = await PublishPendingAsync(connection, cancellationToken).ConfigureAwait(false);
            if (conflicts)
            {
                PeerRecordDescriptor[] pending;
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { pending = _entries.Values.Select(e => (e.Publishing ?? e.Pending)?.Descriptor).OfType<PeerRecordDescriptor>().ToArray(); }
                finally { _gate.Release(); }
                foreach (var batch in pending.Chunk(_limits.PageSize))
                    await connection.RequestAsync(new() { Command = PeerCommand.Reconcile, Records = batch }, cancellationToken).ConfigureAwait(false);
            }
            else await connection.RequestAsync(new() { Command = PeerCommand.Reconcile }, cancellationToken).ConfigureAwait(false);
            if (conflicts) throw new PeerProtocolException(PeerError.Conflict);
            return Synchronization;
        }
        finally { _syncGate.Release(); }
    }

    private async Task RunAsync()
    {
        var retry = new RepositoryRetryBackoff(_retry);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Connection.Set(RepositoryConnectionState.Reconnecting);
                await using var connection = await _transport.ConnectAsync(HandleAsync, _stop.Token).ConfigureAwait(false);
                Volatile.Write(ref _connection, connection);
                Connection.Set(RepositoryConnectionState.Synchronizing);
                Wake();
                while (!_stop.IsCancellationRequested)
                {
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var ready = _wake.Reader.ReadAsync(waiting.Token).AsTask();
                    var done = await Task.WhenAny(ready, connection.Completion).ConfigureAwait(false);
                    if (done == connection.Completion) { waiting.Cancel(); try { await ready.ConfigureAwait(false); } catch (OperationCanceledException) { } await connection.Completion.ConfigureAwait(false); throw new EndOfStreamException(); }
                    await ready.ConfigureAwait(false);
                    try
                    {
                        await SynchronizeAsync(_stop.Token).ConfigureAwait(false);
                        retry.Reset();
                        Connection.Set(RepositoryConnectionState.Online);
                    }
                    catch (PeerProtocolException e) when (e.Error == PeerError.Conflict)
                    {
                        DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Refresh, log: true)
                            .Fail(e, DatabaseError.Conflict);
                        Connection.Set(RepositoryConnectionState.Faulted, e);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                var terminal = e is PeerProtocolException p && p.Error is PeerError.Unauthorized or PeerError.Incompatible or PeerError.InvalidRequest or PeerError.LimitExceeded
                    || e is System.Security.Authentication.AuthenticationException or InvalidDataException || _projectionFailure is not null;
                Connection.Set(terminal ? RepositoryConnectionState.Faulted : RepositoryConnectionState.Offline, e);
                DatabaseTelemetryScope.Start(_telemetry, DatabaseComponent.LocalRemote, DatabaseOperation.Refresh, log: true)
                    .Fail(e, DatabaseTelemetryScope.Classify(e), recoverable: !terminal);
                if (terminal) break;
            }
            finally { Volatile.Write(ref _connection, null); }
            try { await Task.Delay(retry.NextDelay(), _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async ValueTask<bool> PublishPendingAsync(IPeerConnection connection, CancellationToken token)
    {
        var conflicts = false;
        string[] pendingIds;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { pendingIds = _entries.Values.Where(e => e.Pending is not null || e.Publishing is not null).Select(e => e.RecordId).ToArray(); }
        finally { _gate.Release(); }
        foreach (var recordId in pendingIds)
        {
            PeerRecord record;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var entry = _entries[recordId];
                record = entry.Publishing ?? entry.Pending!;
                if (record is null) continue;
                if (entry.Publishing is null)
                {
                    var publishing = entry with { Publishing = record, Pending = null };
                    await PersistAsync(publishing, token).ConfigureAwait(false); _entries[recordId] = publishing;
                }
            }
            finally { _gate.Release(); }
            PeerMessage reply;
            try { reply = await connection.RequestAsync(new() { Command = PeerCommand.Allocate, Record = record.Descriptor }, token).ConfigureAwait(false); }
            catch (PeerProtocolException e) when (e.Error == PeerError.Conflict) { conflicts = true; continue; }
            var assigned = reply.Record ?? throw new InvalidDataException("Missing sequence assignment.");
            assigned.Validate(true);
            if (assigned with { Sequence = 0, CreatedSequence = 0 } != record.Descriptor) throw new InvalidDataException("Sequence assignment changed the mutation.");
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = _entries[assigned.RecordId];
                var newer = current.Pending;
                if (newer is not null) newer = newer with { Descriptor = newer.Descriptor with { BaseSequence = assigned.Sequence } };
                var committed = current.Committed is { } latest && latest.Descriptor.Sequence > assigned.Sequence
                    ? latest : record with { Descriptor = assigned };
                var updated = current with { Committed = committed, Pending = newer, Publishing = null };
                await PersistAsync(updated, token).ConfigureAwait(false);
                _entries[assigned.RecordId] = updated;
                if (newer is null)
                {
                    try { await ProjectAsync(committed, null, true, CancellationToken.None,
                        preserveRevision: committed.Descriptor.MutationId == assigned.MutationId).ConfigureAwait(false); }
                    catch (Exception e) { _projectionFailure = e; throw; }
                }
                else Wake();
                _status = _status with { PendingRecords = PendingCount() };
            }
            finally { _gate.Release(); }
        }
        return conflicts;
    }

    /// <summary>Explicitly discard or rebase a conflicting pending edit after the authoritative peer record has arrived.</summary>
    public async ValueTask ResolveConflictAsync(string recordId, PeerConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (Callback.Value is { Active: true } callback && ReferenceEquals(callback.Owner, this)) throw new RepositoryFeedbackLoopException();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = _entries.GetValueOrDefault(recordId) ?? throw new KeyNotFoundException();
                var local = current.Pending ?? current.Publishing;
                if (local is null) return;
                if (current.Committed is null || current.Committed.Descriptor.Sequence <= local.Descriptor.BaseSequence)
                    throw new InvalidOperationException("Retrieve the conflicting committed record before resolving it.");
                PeerRecord? pending = null;
                T? decoded = null;
                if (resolution == PeerConflictResolution.KeepLocal)
                {
                    var id = Guid.NewGuid(); decoded = local.Descriptor.Deleted ? null : _codec.Decode(local);
                    var payload = local.Descriptor.Deleted ? [] : await _codec.EncodeAsync(decoded!, id, cancellationToken).ConfigureAwait(false);
                    pending = new(local.Descriptor with { MutationId = id, BaseSequence = current.Committed.Descriptor.Sequence,
                        PayloadHash = Convert.ToHexString(SHA256.HashData(payload)) }, payload);
                }
                var updated = current with { Pending = pending, Publishing = null };
                await PersistAsync(updated, cancellationToken).ConfigureAwait(false); _entries[recordId] = updated;
                try { await ProjectAsync(pending ?? current.Committed, decoded, true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) { _projectionFailure = e; throw; }
                _status = _status with { PendingRecords = PendingCount() };
                Wake();
            }
            finally { _gate.Release(); }
        }
        finally { _syncGate.Release(); }
    }

    private async ValueTask<PeerMessage> HandleAsync(PeerMessage request, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            switch (request.Command)
            {
                case PeerCommand.Inventory:
                    if (request.Cursor < 0 || request.PageSize is < 1 || request.PageSize > _limits.PageSize) throw new PeerProtocolException(PeerError.InvalidRequest);
                    return new() { Records = _entries.Values.Select(e => e.Committed?.Descriptor).OfType<PeerRecordDescriptor>()
                        .Where(r => r.Sequence > request.Cursor).OrderBy(r => r.Sequence).Take(request.PageSize).ToArray() };
                case PeerCommand.Read:
                    var found = _entries.Values.Select(e => e.Committed).FirstOrDefault(e => e?.Descriptor.Sequence == request.Cursor)
                        ?? throw new PeerProtocolException(PeerError.Unavailable);
                    return new() { Record = found.Descriptor, Payload = found.Payload };
                case PeerCommand.Apply:
                    var header = request.Record ?? throw new PeerProtocolException(PeerError.InvalidRequest);
                    header.Validate(true);
                    var incoming = new PeerRecord(header, request.Payload ?? throw new PeerProtocolException(PeerError.InvalidRequest));
                    _entries.TryGetValue(header.RecordId, out var old);
                    if (old?.Committed is { } previous && previous.Descriptor.Sequence >= header.Sequence)
                    {
                        if (previous.Descriptor.Sequence == header.Sequence && previous.Descriptor != header) throw new PeerProtocolException(PeerError.Conflict);
                        return new();
                    }
                    // Decode once, before persisting untrusted payload bytes; the relay validated its allocation and digest.
                    if (header.Deleted && (incoming.Payload.Length != 0
                        || Convert.ToHexString(SHA256.HashData(incoming.Payload)) != header.PayloadHash))
                        throw new InvalidDataException("Tombstones must not contain application payloads.");
                    var decoded = header.Deleted ? null : _codec.Decode(incoming);
                    var entry = new PeerJournalEntry(header.RecordId, incoming, old?.Pending, old?.Publishing);
                    await PersistAsync(entry, token).ConfigureAwait(false);
                    _entries[header.RecordId] = entry;
                    if (entry.Pending is null && entry.Publishing is null)
                    {
                        try { await ProjectAsync(incoming, decoded, true, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception e) { _projectionFailure = e; throw; }
                    }
                    return new();
                case PeerCommand.Status:
                    _status = (request.Status ?? throw new PeerProtocolException(PeerError.InvalidRequest)) with
                    { PendingRecords = PendingCount() };
                    return new();
                default: throw new PeerProtocolException(PeerError.InvalidRequest);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _subscription?.Dispose();
        if (_connection is { } connection) await connection.DisposeAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _journal.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        Connection.Dispose(); _stop.Dispose();
    }

    private async ValueTask<TResult> WithRepositoryAsync<TResult>(Func<IRepository<T>, ValueTask<TResult>> operation, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { CheckProjection(); return await operation(_local).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async IAsyncEnumerable<TResult> StreamAsync<TResult>(Func<IRepository<T>, IAsyncEnumerable<TResult>> operation,
        [EnumeratorCancellation] CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var snapshot = new List<TResult>();
        try
        {
            CheckProjection();
            await foreach (var item in operation(_local).WithCancellation(token).ConfigureAwait(false))
            {
                if (snapshot.Count >= _limits.MaximumRecords) throw new PeerProtocolException(PeerError.LimitExceeded);
                snapshot.Add(item);
            }
        }
        finally { _gate.Release(); }
        foreach (var item in snapshot) { token.ThrowIfCancellationRequested(); yield return item; }
    }
}
