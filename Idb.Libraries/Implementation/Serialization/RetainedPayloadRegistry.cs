using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Metadata-only retention for provider addresses. Providers coordinate acquisition/retirement;
/// history files are outside their latest-key recovery namespace. Only opt-in stores construct a registry.</summary>
public sealed class RetainedPayloadRegistry<TAddress> : IDisposable where TAddress : notnull
{
    private readonly object _gate = new();
    private readonly RetainedVersionOptions _options;
    private readonly Dictionary<TAddress, Entry> _records = new();
    private readonly HashSet<Lease> _leases = new();
    private readonly Dictionary<string, long> _pendingDeletes = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, ValueTask<IDisposable>> _enterRead;
    private readonly Func<string, TAddress, CancellationToken, ValueTask<byte[]>> _read;
    private readonly Func<string, TAddress, IMaintenanceBudget, CancellationToken, ValueTask<byte[]>>? _budgetedRead;
    private readonly string _historyRoot;
    private string? _ownerRoot;
    private FileStream? _ownerLock;
    private readonly ITimer _timer;
    private bool _closed;
    private long _bytes, _archived, _copied, _readBytes, _cleanupFailures;

    public RetainedPayloadRegistry(string root, RetainedVersionOptions options,
        Func<CancellationToken, ValueTask<IDisposable>> enterRead,
        Func<string, TAddress, CancellationToken, ValueTask<byte[]>> read)
        : this(root, options, enterRead, read, null) { }

    public RetainedPayloadRegistry(string root, RetainedVersionOptions options,
        Func<CancellationToken, ValueTask<IDisposable>> enterRead,
        Func<string, TAddress, CancellationToken, ValueTask<byte[]>> read,
        Func<string, TAddress, IMaintenanceBudget, CancellationToken, ValueTask<byte[]>>? budgetedRead)
    {
        options.Validate(); _options = options; _enterRead = enterRead; _read = read; _budgetedRead = budgetedRead;
        _historyRoot = Path.Combine(Path.GetFullPath(root), ".idb-history");
        // Recovery never treats session history as current data. Only enabled stores scan it.
        if (Directory.Exists(_historyRoot))
        {
            RejectLink(_historyRoot);
            using var cleanup = OpenCleanupGuard();
            CleanupOrphansUnderGuard();
        }
        _timer = options.TimeProvider.CreateTimer(_ => CleanupExpired(), null, options.CleanupInterval, options.CleanupInterval);
    }

    public RetainedVersionStatistics Statistics
    { get { lock (_gate) return new(_leases.Count, _records.Count, _bytes, _archived, _copied, _readBytes, _cleanupFailures); } }

    /// <summary>Bounded, metadata-only observation. Providers must still coordinate publication and
    /// retirement: a later acquisition can add a hold after this snapshot.</summary>
    public IReadOnlyList<(TAddress Address, long Bytes)> SnapshotUnarchived()
    { lock (_gate) return _records.Values.Where(r => r.Archive is null && r.Holds > 0).Select(r => (r.Address, r.Bytes)).ToArray(); }

    /// <summary>Caller must already protect address selection from retirement and committed deletion.</summary>
    public Lease Acquire(string key, TAddress address, long bytes)
    {
        lock (_gate)
        {
            ThrowIfClosed(); ExpireUnderLock();
            if (bytes < 0 || bytes > _options.MaximumBytes || _leases.Count >= _options.MaximumSessions)
                throw new RetainedVersionUnavailableException("Historical session/byte admission limit reached.");
            if (!_records.TryGetValue(address, out var record))
            {
                if (_records.Count + _pendingDeletes.Count >= _options.MaximumRecords
                    || bytes > _options.MaximumBytes - _bytes - _pendingDeletes.Values.Sum())
                    throw new RetainedVersionUnavailableException("Historical record/byte admission limit reached.");
                record = new(key, address, bytes); _records.Add(address, record); _bytes += bytes;
            }
            if (record.Key != key || record.Invalidated)
                throw new RetainedVersionUnavailableException("The retained record does not belong to this entity incarnation.");
            var lease = new Lease(this, record, _options.TimeProvider.GetTimestamp());
            _leases.Add(lease); record.Holds++;
            return lease;
        }
    }

    /// <summary>Invalidate only after a committed delete, including raw/transactional deletes.</summary>
    public void Deleted(string key)
    { lock (_gate) foreach (var record in _records.Values) if (record.Key == key) record.Invalidated = true; }

    public void CleanupExpired()
    {
        lock (_gate)
        {
            if (_closed) return;
            ExpireUnderLock();
            foreach (var item in _pendingDeletes.ToArray())
                if (TryDelete(item.Key)) { _archived -= item.Value; _pendingDeletes.Remove(item.Key); }
        }
    }

    private void ExpireUnderLock()
    {
        var now = _options.TimeProvider.GetTimestamp();
        foreach (var lease in _leases.ToArray())
            if (Elapsed(lease.Created, now) >= lease.Lifetime) ReleaseUnderLock(lease);
    }

    private TimeSpan Elapsed(long started, long now) => _options.TimeProvider.GetElapsedTime(started, now);

    private Use UseLease(Lease lease)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (!lease.Active || Elapsed(lease.Created, _options.TimeProvider.GetTimestamp()) >= lease.Lifetime)
            { ReleaseUnderLock(lease); throw new RetainedVersionUnavailableException("The retained version lease expired or was released."); }
            lease.Record.Holds++; return new Use(this, lease.Record);
        }
    }

    private void Renew(Lease lease, TimeSpan duration)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var elapsed = Elapsed(lease.Created, _options.TimeProvider.GetTimestamp());
            if (!lease.Active || elapsed >= lease.Lifetime)
            { ReleaseUnderLock(lease); throw new RetainedVersionUnavailableException("An expired lease cannot be renewed."); }
            if (duration <= TimeSpan.Zero || duration > _options.LeaseDuration || elapsed + duration > _options.MaximumLifetime)
                throw new RetainedVersionUnavailableException("Renewal exceeds the configured duration or maximum lifetime.");
            lease.Lifetime = elapsed + duration;
        }
    }

    private async ValueTask<byte[]> ReadEntryAsync(Entry record, CancellationToken token)
    {
        // Admit the physical reader before choosing an address: relocation cannot free a stale choice.
        using var admission = await _enterRead(token).ConfigureAwait(false);
        string? archive;
        lock (_gate) archive = record.Archive;
        var payload = archive is null ? await _read(record.Key, record.Address, token).ConfigureAwait(false)
            : await File.ReadAllBytesAsync(archive, token).ConfigureAwait(false);
        Interlocked.Add(ref _readBytes, payload.Length);
        return payload;
    }

    /// <summary>Called under the provider's retirement protection. It reads encoded payloads only.
    /// Mapping is published only after a complete history file exists. A partial failure preserves old addresses.</summary>
    public async ValueTask<long> RelocateAsync(Func<TAddress, bool> retiring, CancellationToken token = default)
        => await RelocateAsync(retiring, null, token).ConfigureAwait(false);

    public async ValueTask<long> RelocateAsync(Func<TAddress, bool> retiring, IMaintenanceBudget? budget,
        CancellationToken token = default)
    {
        Entry[] snapshot;
        lock (_gate)
        {
            ExpireUnderLock();
            snapshot = _records.Values.Where(record => record.Archive is null && retiring(record.Address)).ToArray();
            foreach (var record in snapshot) record.Holds++;
        }
        long copied = 0;
        try
        {
            foreach (var record in snapshot)
            {
                token.ThrowIfCancellationRequested();
                byte[] payload;
                if (budget is not null && _budgetedRead is not null)
                    payload = await _budgetedRead(record.Key, record.Address, budget, token).ConfigureAwait(false);
                else
                {
                    using var admission = budget is null ? null : await budget.AcquireAsync(record.Bytes, token).ConfigureAwait(false);
                    payload = await _read(record.Key, record.Address, token).ConfigureAwait(false);
                }
                await ArchiveAsync(record, payload, token, budget).ConfigureAwait(false);
                copied += payload.Length;
            }
            return copied;
        }
        finally { lock (_gate) foreach (var record in snapshot) DropUnderLock(record); }
    }

    /// <summary>File providers capture the exact committed bytes under their cross-process write guard.</summary>
    public ValueTask ArchiveAsync(Lease lease, byte[] payload, CancellationToken token = default)
        => ArchiveAsync(lease.Record, payload, token);

    private async ValueTask ArchiveAsync(Entry record, byte[] payload, CancellationToken token, IMaintenanceBudget? budget = null)
    {
        string path;
        lock (_gate)
        {
            if (record.Archive is not null) return;
            EnsureOwnerUnderLock();
            path = Path.Combine(_ownerRoot!, Guid.NewGuid().ToString("N") + ".version");
        }
        var temporary = path + ".writing";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (budget is null) await stream.WriteAsync(payload, token).ConfigureAwait(false);
                else
                {
                    var chunkSize = (int)Math.Min(64 * 1024, budget.MaximumWorkUnitBytes);
                    for (var offset = 0; offset < payload.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, payload.Length - offset);
                        using var admission = await budget.AcquireAsync(count, token).ConfigureAwait(false);
                        await stream.WriteAsync(payload.AsMemory(offset, count), token).ConfigureAwait(false);
                    }
                }
                using (budget is null ? null : await budget.AcquireAsync(0, token).ConfigureAwait(false))
                { await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            lock (_gate)
            {
                if (record.Holds != 0 && record.Archive is null)
                { record.Archive = path; record.ArchiveBytes = payload.Length; _archived += payload.Length; _copied += payload.Length; path = ""; }
            }
        }
        finally
        {
            TryDelete(temporary);
            if (path.Length != 0) TryDelete(path);
        }
    }

    private void EnsureOwnerUnderLock()
    {
        if (_ownerRoot is not null) return;
        Directory.CreateDirectory(_historyRoot);
        RejectLink(_historyRoot);
        // A held cleanup file serializes owner publication with orphan reclamation.
        using var cleanup = OpenCleanupGuard();
        CleanupOrphansUnderGuard();
        var owner = Path.Combine(_historyRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(owner);
        _ownerLock = new FileStream(Path.Combine(owner, "owner.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        _ownerRoot = owner;
    }

    private FileStream OpenCleanupGuard()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new(Path.Combine(_historyRoot, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { Thread.Sleep(10); }
        }
    }

    private void CleanupOrphansUnderGuard()
    {
        foreach (var directory in Directory.EnumerateDirectories(_historyRoot).Take(1024))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                RejectLink(directory);
                var ownerPath = Path.Combine(directory, "owner.lock");
                if (File.Exists(ownerPath)) RejectLink(ownerPath);
                using (var ownership = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    foreach (var file in Directory.EnumerateFiles(directory))
                        if (file != ownerPath) { RejectLink(file); File.Delete(file); }
                File.Delete(ownerPath); Directory.Delete(directory);
            }
            catch (IOException) { /* A live owner or a failed cleanup must be preserved. */ }
            catch (UnauthorizedAccessException) { _cleanupFailures++; }
        }
    }

    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("History paths cannot be links."); }

    private void ReleaseUnderLock(Lease lease)
    { if (lease.Active) { lease.Active = false; _leases.Remove(lease); DropUnderLock(lease.Record); } }

    private void DropUnderLock(Entry record)
    {
        if (--record.Holds != 0) return;
        _records.Remove(record.Address); _bytes -= record.Bytes;
        if (record.Archive is { } archive)
        {
            if (TryDelete(archive)) _archived -= record.ArchiveBytes;
            else _pendingDeletes[archive] = record.ArchiveBytes;
        }
    }

    private bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { Interlocked.Increment(ref _cleanupFailures); return false; }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _cleanupFailures); return false; }
    }

    private void ThrowIfClosed() { if (_closed) throw new RetainedVersionUnavailableException("The issuing store was closed; retained handles do not survive restart."); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true; _timer.Dispose();
            foreach (var lease in _leases.ToArray()) ReleaseUnderLock(lease);
            // Keep ownership while admitted uses still need files. Last use closes it.
            CloseOwnerIfDrained();
        }
    }

    private void CloseOwnerIfDrained()
    {
        if (!_closed || _records.Count != 0) return;
        _ownerLock?.Dispose(); _ownerLock = null;
        if (_ownerRoot is not null)
        {
            TryDelete(Path.Combine(_ownerRoot, "owner.lock"));
            try { Directory.Delete(_ownerRoot); }
            catch (IOException) { _cleanupFailures++; }
            catch (UnauthorizedAccessException) { _cleanupFailures++; }
        }
    }

    internal sealed class Entry(string key, TAddress address, long bytes)
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly string Key = key;
        internal readonly TAddress Address = address;
        internal readonly long Bytes = bytes;
        internal int Holds;
        internal bool Invalidated;
        internal string? Archive;
        internal long ArchiveBytes;
    }

    public sealed class Lease : IRetainedPayloadLease
    {
        private readonly RetainedPayloadRegistry<TAddress> _owner;
        internal readonly Entry Record;
        internal readonly long Created;
        internal TimeSpan Lifetime;
        internal bool Active = true;
        internal Lease(RetainedPayloadRegistry<TAddress> owner, Entry record, long created)
        { _owner = owner; Record = record; Created = created; Lifetime = owner._options.LeaseDuration; }
        public Guid RecordId => Record.Id;
        public string Key => Record.Key;
        public Use AcquireUse() => _owner.UseLease(this);
        IRetainedPayloadUse IRetainedPayloadLease.AcquireUse() => AcquireUse();
        public void Renew(TimeSpan duration) => _owner.Renew(this, duration);
        public async ValueTask<byte[]> ReadAsync(CancellationToken token = default)
        { using var use = AcquireUse(); return await use.ReadAsync(token).ConfigureAwait(false); }
        public void Dispose() { lock (_owner._gate) _owner.ReleaseUnderLock(this); }
    }

    public sealed class Use : IRetainedPayloadUse
    {
        private readonly RetainedPayloadRegistry<TAddress> _owner;
        private readonly Entry _record;
        private int _disposed;
        internal Use(RetainedPayloadRegistry<TAddress> owner, Entry record) { _owner = owner; _record = record; }
        public bool IsSameIncarnation { get { lock (_owner._gate) return !_owner._closed && _disposed == 0 && !_record.Invalidated; } }
        public async ValueTask<byte[]> ReadAsync(CancellationToken token = default)
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _owner.ThrowIfClosed();
                _record.Holds++; // A concurrent Dispose cannot reclaim an already admitted read.
            }
            try { return await _owner.ReadEntryAsync(_record, token).ConfigureAwait(false); }
            finally { lock (_owner._gate) { _owner.DropUnderLock(_record); _owner.CloseOwnerIfDrained(); } }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_owner._gate) { _owner.DropUnderLock(_record); _owner.CloseOwnerIfDrained(); }
        }
    }
}
