using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Idb.Commercial.Repositories.Peers;

internal sealed class PeerStateDirectory(string path) : IAsyncDisposable
{
    private readonly string _root = Path.GetFullPath(path);
    private FileStream? _owner;
    public async ValueTask InitializeAsync(string scope, CancellationToken token)
    {
        if (_owner is not null) return;
        Directory.CreateDirectory(_root);
        var owner = new FileStream(Path.Combine(_root, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
            var binding = Path.Combine(_root, ".scope");
            if (File.Exists(binding))
            {
                if (new FileInfo(binding).Length != expected.Length || !CryptographicOperations.FixedTimeEquals(
                    await File.ReadAllBytesAsync(binding, token).ConfigureAwait(false), expected))
                    throw new InvalidDataException("Peer state belongs to another endpoint, account or contract.");
            }
            else await WriteBytesAsync(".scope", expected, token).ConfigureAwait(false);
            _owner = owner;
        }
        catch { owner.Dispose(); throw; }
    }
    public IEnumerable<string> Files(string pattern) => Directory.EnumerateFiles(_root, pattern).Select(Path.GetFileName)!;
    public async ValueTask<T?> ReadAsync<T>(string name, long maximum, CancellationToken token)
    {
        var path = Path.Combine(_root, name);
        if (!File.Exists(path)) return default;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("Peer state exceeds its configured size limit.");
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Invalid peer state.");
    }
    public ValueTask WriteAsync<T>(string name, T value, CancellationToken token)
        => WriteBytesAsync(name, JsonSerializer.SerializeToUtf8Bytes(value), token);
    private async ValueTask WriteBytesAsync(string name, byte[] value, CancellationToken token)
    {
        var target = Path.Combine(_root, name);
        var temporary = target + ".pending";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await stream.WriteAsync(value, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(temporary, target, overwrite: true);
    }
    public ValueTask DisposeAsync() { _owner?.Dispose(); _owner = null; return ValueTask.CompletedTask; }
}

/// <summary>Local write-ahead state and reusable opaque payload snapshots. Borrowed entries must not be mutated.</summary>
public sealed class FilePeerJournal(string directory, PeerLimits? limits = null) : IPeerJournal
{
    private readonly PeerStateDirectory _directory = new(directory);
    private readonly PeerLimits _limits = limits ?? new();
    private readonly Dictionary<string, PeerJournalEntry> _entries = new(StringComparer.Ordinal);
    private bool _initialized;
    private string? _scope;
    private long _bytes;
    public async ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (_initialized) { if (_scope != scope) throw new InvalidOperationException("A journal instance belongs to one scope."); return; }
        _limits.Validate();
        await _directory.InitializeAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var file in _directory.Files("*.record"))
        {
            var entry = await _directory.ReadAsync<PeerJournalEntry>(file, _limits.MaximumPayloadBytes * 5L + 16_384, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Missing peer entry.");
            Validate(entry);
            if (Name(entry.RecordId) != file || !_entries.TryAdd(entry.RecordId, entry)) throw new InvalidDataException("Invalid peer record identity.");
            _bytes += Size(entry);
            if (_entries.Count > _limits.MaximumRecords || _bytes > _limits.MaximumReplicaBytes)
                throw new InvalidDataException("Peer journal exceeds configured capacity.");
        }
        _scope = scope; _initialized = true;
    }
    public ValueTask<IReadOnlyList<PeerJournalEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_initialized) throw new InvalidOperationException("Initialize the journal first.");
        return ValueTask.FromResult<IReadOnlyList<PeerJournalEntry>>(_entries.Values.ToArray());
    }
    public async ValueTask PutAsync(PeerJournalEntry entry, CancellationToken cancellationToken = default)
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the journal first.");
        Validate(entry);
        _entries.TryGetValue(entry.RecordId, out var before);
        var bytes = _bytes - Size(before) + Size(entry);
        if (before is null && _entries.Count >= _limits.MaximumRecords || bytes > _limits.MaximumReplicaBytes)
            throw new PeerProtocolException(PeerError.LimitExceeded);
        await _directory.WriteAsync(Name(entry.RecordId), entry, cancellationToken).ConfigureAwait(false);
        _entries[entry.RecordId] = entry; _bytes = bytes;
    }
    private void Validate(PeerJournalEntry entry)
    {
        if (entry.Committed is null && entry.Pending is null && entry.Publishing is null) throw new InvalidDataException("Empty peer entry.");
        if (entry.Committed is { } committed) Check(committed, true);
        if (entry.Pending is { } pending) Check(pending, false);
        if (entry.Publishing is { } publishing) Check(publishing, false);
        void Check(PeerRecord record, bool assigned)
        {
            record.Descriptor.Validate(assigned);
            if (record.Descriptor.RecordId != entry.RecordId || !assigned && record.Descriptor.Sequence != 0
                || record.Payload.Length > _limits.MaximumPayloadBytes
                || record.Descriptor.Deleted && record.Payload.Length != 0
                || Convert.ToHexString(SHA256.HashData(record.Payload)) != record.Descriptor.PayloadHash)
                throw new InvalidDataException("Invalid peer journal payload.");
        }
    }
    private static long Size(PeerJournalEntry? entry) => (entry?.Committed?.Payload.LongLength ?? 0)
        + (entry?.Pending?.Payload.LongLength ?? 0) + (entry?.Publishing?.Payload.LongLength ?? 0);
    private static string Name(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".record";
    public ValueTask DisposeAsync() => _directory.DisposeAsync();
}

/// <summary>Single-owner relay allocation metadata. Back up this directory; losing it cannot be repaired from a counter alone.</summary>
public sealed class FilePeerSequenceLedger(string directory, int maximumOperations = 100_000) : IPeerSequenceLedger
{
    private sealed record Allocation(PeerRecordDescriptor Record, string Principal);
    private readonly PeerStateDirectory _directory = new(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Allocation> _operations = [];
    private readonly Dictionary<string, PeerRecordDescriptor> _heads = new(StringComparer.Ordinal);
    private long _sequence;
    private bool _initialized;
    private string? _scope;
    public async ValueTask InitializeAsync(string scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) { if (_scope != scope) throw new InvalidOperationException("A ledger instance belongs to one dataset."); return; }
            if (maximumOperations is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
            await _directory.InitializeAsync(scope, cancellationToken).ConfigureAwait(false);
            var saved = await _directory.ReadAsync<Allocation[]>("allocations.json", maximumOperations * 2048L, cancellationToken).ConfigureAwait(false) ?? [];
            if (saved.Length > maximumOperations) throw new InvalidDataException("Allocation ledger exceeds its limit.");
            foreach (var allocation in saved)
            {
                var item = allocation.Record; item.Validate(true);
                var previous = _heads.GetValueOrDefault(item.RecordId);
                if (item.Sequence != checked(_sequence + 1) || item.BaseSequence != (_heads.GetValueOrDefault(item.RecordId)?.Sequence ?? 0)
                    || item.CreatedSequence != 0 && item.CreatedSequence != (previous?.CreatedSequence ?? item.Sequence)
                    || previous is not null && previous.HistoryDateUtc != item.HistoryDateUtc
                    || !_operations.TryAdd(item.MutationId, allocation)) throw new InvalidDataException("Broken sequence ledger.");
                _sequence = item.Sequence; _heads[item.RecordId] = item;
            }
            _scope = scope; _initialized = true;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<PeerRecordDescriptor> AllocateAsync(PeerRecordDescriptor mutation, string principal,
        CancellationToken cancellationToken = default)
    {
        mutation.Validate(false);
        if (mutation.Sequence != 0 || string.IsNullOrWhiteSpace(principal)) throw new PeerProtocolException(PeerError.InvalidRequest);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_initialized) throw new InvalidOperationException("Initialize the ledger first.");
            if (_operations.TryGetValue(mutation.MutationId, out var previous))
            {
                if (previous.Principal != principal || previous.Record with { Sequence = 0, CreatedSequence = 0 } != mutation)
                    throw new PeerProtocolException(PeerError.Conflict);
                return previous.Record;
            }
            var head = _heads.GetValueOrDefault(mutation.RecordId);
            if (mutation.BaseSequence != (head?.Sequence ?? 0) || head is not null && head.HistoryDateUtc != mutation.HistoryDateUtc)
                throw new PeerProtocolException(PeerError.Conflict);
            if (_operations.Count >= maximumOperations) throw new PeerProtocolException(PeerError.LimitExceeded);
            var assigned = mutation with { Sequence = checked(_sequence + 1),
                CreatedSequence = head is null ? checked(_sequence + 1) : head.CreatedSequence };
            var addition = new Allocation(assigned, principal);
            await _directory.WriteAsync("allocations.json", _operations.Values.Append(addition).ToArray(), cancellationToken).ConfigureAwait(false);
            _operations.Add(assigned.MutationId, addition); _heads[assigned.RecordId] = assigned; _sequence = assigned.Sequence;
            return assigned;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<IReadOnlyList<PeerRecordDescriptor>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _heads.Values.OrderBy(r => r.Sequence).ToArray(); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await _gate.WaitAsync().ConfigureAwait(false); try { await _directory.DisposeAsync().ConfigureAwait(false); } finally { _gate.Release(); } }
}
