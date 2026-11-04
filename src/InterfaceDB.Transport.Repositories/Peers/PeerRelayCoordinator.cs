using System.Security.Cryptography;
using System.Threading.Channels;
using Idb.Commercial.Repositories.Peers;

namespace Idb.Transport.Peers;

internal sealed record PeerRelayEndpoint(string Id, string Principal, IPeerConnection Connection, Func<PeerAccess, bool> Authorized)
{
    public PeerRegistrationState? Registration { get; set; }
    public long ConnectedThrough { get; init; }
    public Func<PeerRecordDescriptor, bool> Permitted { get; init; } = _ => true;
}

internal sealed class PeerRelayCoordinator(IPeerSequenceLedger ledger, IPeerRangePlanner planner, PeerLimits limits,
    CancellationToken lifetime, IPeerRegistrationLedger? registrations = null, IPeerHistorySelector? historySelector = null,
    TimeProvider? timeProvider = null, string? historyDateProperty = null) : IAsyncDisposable
{
    private readonly IPeerHistorySelector _history = historySelector ?? new PeerHistorySelector();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PeerRelayEndpoint> _peers = new();
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly SemaphoreSlim _allocation = new(1, 1);
    private readonly object _membership = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task? _worker;
    internal async ValueTask<(PeerRegistrationState? State, long Through)> RegisterAsync(PeerRegistrationOptions? options, string principal, CancellationToken token)
    {
        await _allocation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var heads = await ledger.SnapshotAsync(token).ConfigureAwait(false);
            var through = heads.Count == 0 ? 0 : heads.Max(r => r.Sequence);
            if (options is null) return (null, through);
            if (registrations is null) throw new PeerProtocolException(PeerError.Incompatible);
            if (heads.Any(r => r.CreatedSequence == 0 || options.DateProperty is not null && r.HistoryDateUtc is null))
                throw new PeerProtocolException(PeerError.Incompatible);
            return (await registrations.GetOrCreateAsync(options, principal, through, token).ConfigureAwait(false), through);
        }
        finally { _allocation.Release(); }
    }
    internal void Add(PeerRelayEndpoint peer)
    {
        lock (_membership)
        {
            if (_peers.Count >= limits.MaximumPeers) throw new PeerProtocolException(PeerError.LimitExceeded);
            if (peer.Registration is { } registration && _peers.Values.Any(p => p.Registration?.Options.RegistrationId == registration.Options.RegistrationId))
                throw new PeerProtocolException(PeerError.Conflict);
            _peers[peer.Id] = peer;
            _worker ??= Task.Run(WorkAsync);
        }
        _wake.Writer.TryWrite(true);
    }
    internal void Remove(string id) { _peers.TryRemove(id, out _); _wake.Writer.TryWrite(true); }
    private async Task WorkAsync()
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                _wake.Reader.TryRead(out _);
                try { await ReconcileAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception) when (!_stop.IsCancellationRequested) { /* Explicit sync reports errors; new membership/write events retry. */ }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }
    internal async ValueTask<PeerMessage> HandleAsync(PeerRelayEndpoint peer, PeerMessage request, CancellationToken token)
    {
        if (!peer.Authorized(PeerAccess.Read)) throw new PeerProtocolException(PeerError.Unauthorized);
        switch (request.Command)
        {
            case PeerCommand.Allocate:
                if (!peer.Authorized(PeerAccess.Write)) throw new PeerProtocolException(PeerError.Unauthorized);
                if (request.Payload is not null || request.Record is null) throw new PeerProtocolException(PeerError.InvalidRequest);
                await _allocation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var heads = await ledger.SnapshotAsync(token).ConfigureAwait(false);
                    var previous = heads.FirstOrDefault(r => r.RecordId == request.Record.RecordId);
                    if (previous is not null && !peer.Permitted(previous)) throw new PeerProtocolException(PeerError.Unauthorized);
                    if ((request.Record.HistoryDateUtc is not null) != (historyDateProperty is not null))
                        throw new PeerProtocolException(PeerError.InvalidRequest);
                    if (heads.Count >= limits.MaximumRecords && !heads.Any(r => r.RecordId == request.Record.RecordId))
                        throw new PeerProtocolException(PeerError.LimitExceeded);
                    return new() { Record = await ledger.AllocateAsync(request.Record, peer.Principal, token).ConfigureAwait(false) };
                }
                finally { _allocation.Release(); }
            case PeerCommand.Reconcile:
                if (request.Records is { } pending)
                {
                    if (pending.Length > limits.PageSize) throw new PeerProtocolException(PeerError.LimitExceeded);
                    foreach (var record in pending)
                    {
                        record.Validate(false);
                        if (record.Sequence != 0) throw new PeerProtocolException(PeerError.InvalidRequest);
                    }
                }
                await ReconcileAsync(token, peer.Id, request.Records?.Select(r => r.RecordId).ToHashSet()).ConfigureAwait(false);
                return new(); // Each recipient receives only its own filtered status.
            default: throw new PeerProtocolException(PeerError.InvalidRequest);
        }
    }
    internal async ValueTask<PeerSyncStatus> ReconcileAsync(CancellationToken token, string? repairingPeer = null, HashSet<string>? pendingRecords = null)
    {
        // Background rounds need the same bounded lifetime as explicitly requested rounds.
        using var round = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        round.CancelAfter(limits.ReconciliationTimeout);
        token = round.Token;
        await _round.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var heads = await ledger.SnapshotAsync(token).ConfigureAwait(false);
            if (heads.Count > limits.MaximumRecords) throw new PeerProtocolException(PeerError.LimitExceeded);
            var approved = heads.ToDictionary(r => r.Sequence);
            var active = _peers.Values.Where(p => p.Authorized(PeerAccess.Read)).ToDictionary(p => p.Id);
            var inventories = new Dictionary<string, IReadOnlyList<PeerRecordDescriptor>>();
            foreach (var peer in active.Values)
            {
                try
                {
                    var items = new List<PeerRecordDescriptor>();
                    long cursor = 0;
                    while (true)
                    {
                        var page = await ReadAsync(peer, new() { Command = PeerCommand.Inventory, Cursor = cursor, PageSize = limits.PageSize }, token).ConfigureAwait(false);
                        var rows = page.Records ?? throw new InvalidDataException("Missing inventory.");
                        if (rows.Length > limits.PageSize) throw new InvalidDataException("Oversized inventory page.");
                        foreach (var row in rows)
                        {
                            row.Validate(true);
                            if (row.Sequence <= cursor || items.Count >= limits.MaximumRecords) throw new InvalidDataException("Invalid inventory order or size.");
                            cursor = row.Sequence;
                            if (approved.TryGetValue(row.Sequence, out var valid) && valid != row) throw new InvalidDataException("Peer advertised a forged assignment.");
                            items.Add(row);
                        }
                        if (rows.Length < limits.PageSize) break;
                    }
                    inventories[peer.Id] = items;
                }
                catch (Exception) when (!token.IsCancellationRequested) { /* An unavailable or invalid source cannot participate in this round. */ }
            }
            var confirmed = inventories.ToDictionary(p => p.Key, p => p.Value.Where(r => approved.GetValueOrDefault(r.Sequence) == r).Select(r => r.Sequence).ToHashSet());
            var retained = inventories.ToDictionary(p => p.Key, p => p.Value.Select(r => r.RecordId).ToHashSet());
            var now = _time.GetUtcNow();
            bool Wants(string id, PeerRecordDescriptor record) => active[id].Permitted(record)
                && _history.Includes(active[id].Registration, active[id].ConnectedThrough, record,
                    retained.TryGetValue(id, out var ids) && ids.Contains(record.RecordId), now,
                    id == repairingPeer && pendingRecords?.Contains(record.RecordId) == true);
            var failedSources = new HashSet<string>();
            foreach (var transfer in planner.Plan(heads, inventories, Wants, (id, record) => active[id].Permitted(record)))
            {
                var targets = transfer.Targets.Where(target => Wants(target, transfer.Record)).ToArray();
                if (targets.Length == 0) continue;
                PeerMessage? fetched = null;
                var candidates = new[] { transfer.Source }.Concat(inventories.Where(p => p.Value.Contains(transfer.Record)).Select(p => p.Key)).Distinct();
                foreach (var source in candidates)
                {
                    if (failedSources.Contains(source) || !active[source].Permitted(transfer.Record)) continue;
                    try
                    {
                        var reply = await ReadAsync(active[source], new() { Command = PeerCommand.Read, Cursor = transfer.Record.Sequence }, token).ConfigureAwait(false);
                        if (reply.Record != transfer.Record || reply.Payload is null
                            || Convert.ToHexString(SHA256.HashData(reply.Payload)) != transfer.Record.PayloadHash)
                            throw new InvalidDataException("Peer supplied a mismatched payload.");
                        fetched = reply; break;
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { failedSources.Add(source); }
                }
                if (fetched is null) continue;
                foreach (var target in targets)
                {
                    try
                    {
                        if (!Wants(target, transfer.Record)) continue;
                        await ReadAsync(active[target], new() { Command = PeerCommand.Apply, Record = transfer.Record, Payload = fetched.Payload }, token).ConfigureAwait(false);
                        confirmed[target].Add(transfer.Record.Sequence);
                    }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                }
            }
            var through = heads.Count == 0 ? 0 : heads.Max(r => r.Sequence);
            var missing = 0;
            foreach (var peer in active.Values)
            {
                var wanted = heads.Where(r => Wants(peer.Id, r)).ToArray();
                var count = wanted.Count(r => !confirmed.TryGetValue(peer.Id, out var held) || !held.Contains(r.Sequence));
                missing += count;
                if (count == 0 && inventories.ContainsKey(peer.Id) && peer.Registration is { InitialComplete: false } state)
                {
                    await registrations!.MarkInitialCompleteAsync(state.Options.RegistrationId, peer.Principal, token).ConfigureAwait(false);
                    peer.Registration = state with { InitialComplete = true };
                }
                try { await ReadAsync(peer, new() { Command = PeerCommand.Status, Status = new(through, count, Reconciled: true) }, token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
            return new(through, missing, Reconciled: true);
        }
        finally { _round.Release(); }
    }
    private static ValueTask<PeerMessage> ReadAsync(PeerRelayEndpoint peer, PeerMessage message, CancellationToken token)
    {
        if (!peer.Authorized(PeerAccess.Read)) throw new PeerProtocolException(PeerError.Unauthorized);
        return peer.Connection.RequestAsync(message, token);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
    }
}
