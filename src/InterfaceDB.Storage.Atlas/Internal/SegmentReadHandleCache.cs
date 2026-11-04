using Microsoft.Win32.SafeHandles;

namespace Idb.Storage.AtlasCore.Internal;

internal readonly record struct SegmentReadHandleCacheStatistics(
    int CurrentOpenHandles,
    int PeakOpenHandles,
    long Hits,
    long Misses,
    long Evictions);

/// <summary>
/// A process-local, strictly bounded LRU cache for random-access segment handles.
/// Handles are leased while I/O is in flight, so an eviction can never close a handle
/// that RandomAccess is still using.
/// </summary>
internal sealed class SegmentReadHandleCache : IAsyncDisposable
{
    private static readonly TimeSpan CancellationPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = new();
    private readonly int _capacity;
    private long _accessSequence;
    private long _hits;
    private long _misses;
    private long _evictions;
    private int _currentOpenHandles;
    private int _peakOpenHandles;
    private int _waiters;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    public SegmentReadHandleCache(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public SegmentReadHandleCacheStatistics Statistics
    {
        get
        {
            lock (_sync)
            {
                return new SegmentReadHandleCacheStatistics(
                    _currentOpenHandles,
                    _peakOpenHandles,
                    Interlocked.Read(ref _hits),
                    _misses,
                    _evictions);
            }
        }
    }

    public SegmentReadHandleLease Acquire(
        string path,
        FileOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new CacheKey(Path.GetFullPath(path), options);

        lock (_sync)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(key, out var existing))
                {
                    if (TryAcquire(existing, out var lease)) return lease;
                    throw new InvalidOperationException("A retired handle remains in the active cache.");
                }

                if (_entries.Count < _capacity)
                {
                    _misses++;
                    var handle = File.OpenHandle(
                        key.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        key.Options);
                    var created = new CacheEntry(
                        key,
                        handle,
                        Interlocked.Increment(ref _accessSequence));
                    _entries.Add(key, created);
                    _currentOpenHandles++;
                    _peakOpenHandles = Math.Max(_peakOpenHandles, _currentOpenHandles);
                    return new SegmentReadHandleLease(this, created);
                }

                // Advertise before checking idle entries: a last release between that
                // check and Monitor.Wait must wake us, rather than wait for the poll timeout.
                _waiters++;
                try
                {
                    var eviction = FindEvictionCandidate();
                    if (eviction is not null)
                    {
                        _entries.Remove(eviction.Key);
                        _currentOpenHandles--;
                        _evictions++;
                        eviction.Handle.Dispose();
                        continue;
                    }
                    // Every handle is leased. Preserve the hard descriptor bound.
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = Monitor.Wait(_sync, CancellationPollInterval);
                }
                finally { _waiters--; }
            }
        }
    }

    // Hot group-local hints bypass path normalization and the global dictionary/LRU gate.
    // Entry gates never acquire _sync while held; slow paths always take _sync first.
    internal bool TryAcquire(CacheEntry entry, out SegmentReadHandleLease lease)
    {
        lock (entry.Sync)
        {
            if (Volatile.Read(ref _disposed) || entry.Retired) { lease = default; return false; }
            entry.LeaseCount++;
            entry.LastAccess = Interlocked.Increment(ref _accessSequence);
            Interlocked.Increment(ref _hits);
            lease = new SegmentReadHandleLease(this, entry);
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task? pending = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return _disposeCompletion is null
                    ? ValueTask.CompletedTask
                    : new ValueTask(_disposeCompletion.Task);
            }

            Volatile.Write(ref _disposed, true);
            foreach (var entry in _entries.Values)
            {
                lock (entry.Sync)
                {
                    entry.Retired = true;
                    if (entry.LeaseCount == 0)
                    {
                        entry.Handle.Dispose();
                        _currentOpenHandles--;
                    }
                }
            }

            _entries.Clear();
            if (_currentOpenHandles != 0)
            {
                _disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending = _disposeCompletion.Task;
            }

            Monitor.PulseAll(_sync);
        }

        return pending is null ? ValueTask.CompletedTask : new ValueTask(pending);
    }

    internal void Release(CacheEntry entry)
    {
        SafeFileHandle? retiredHandle = null;
        TaskCompletionSource? disposeCompletion = null;
        bool becameIdle;
        lock (entry.Sync)
        {
            if (entry.LeaseCount <= 0)
            {
                throw new InvalidOperationException("An AtlasCore segment read-handle lease was released twice.");
            }

            entry.LeaseCount--;
            becameIdle = entry.LeaseCount == 0;
            if (entry.Retired && entry.LeaseCount == 0)
            {
                retiredHandle = entry.Handle;
            }
        }

        // Close before freeing descriptor capacity or completing disposal.
        retiredHandle?.Dispose();
        if (retiredHandle is not null || (becameIdle && Volatile.Read(ref _waiters) != 0))
        lock (_sync)
        {
            if (retiredHandle is not null)
            {
                _currentOpenHandles--;
                if (_currentOpenHandles == 0)
                {
                    disposeCompletion = _disposeCompletion;
                }
            }

            Monitor.PulseAll(_sync);
        }

        disposeCompletion?.TrySetResult();
    }

    private CacheEntry? FindEvictionCandidate()
    {
        CacheEntry? candidate = null;
        foreach (var entry in _entries.Values)
        {
            if (Volatile.Read(ref entry.LeaseCount) != 0)
            {
                continue;
            }

            if (candidate is null
                || Volatile.Read(ref entry.LastAccess) < Volatile.Read(ref candidate.LastAccess)
                || (Volatile.Read(ref entry.LastAccess) == Volatile.Read(ref candidate.LastAccess)
                    && CompareKeys(entry.Key, candidate.Key) < 0))
            {
                candidate = entry;
            }
        }

        if (candidate is not null)
        lock (candidate.Sync)
        {
            if (candidate.LeaseCount == 0) { candidate.Retired = true; return candidate; }
        }
        return null;
    }

    private static int CompareKeys(CacheKey left, CacheKey right)
    {
        var path = string.CompareOrdinal(left.Path, right.Path);
        return path != 0 ? path : left.Options.CompareTo(right.Options);
    }

    internal readonly record struct CacheKey(string Path, FileOptions Options);

    internal sealed class CacheEntry(
        CacheKey key,
        SafeFileHandle handle,
        long lastAccess)
    {
        public object Sync { get; } = new();
        public CacheKey Key { get; } = key;

        public SafeFileHandle Handle { get; } = handle;

        public long LastAccess = lastAccess;

        public int LeaseCount = 1;

        public bool Retired { get; set; }
    }
}

/// <remarks>
/// This internal value is deliberately allocation-free. It must not be copied or disposed
/// more than once; AtlasCore keeps every lease within one lexical using scope.
/// </remarks>
internal struct SegmentReadHandleLease : IDisposable
{
    private SegmentReadHandleCache? _owner;
    private SegmentReadHandleCache.CacheEntry? _entry;

    internal SegmentReadHandleLease(
        SegmentReadHandleCache owner,
        SegmentReadHandleCache.CacheEntry entry)
    {
        _owner = owner;
        _entry = entry;
    }

    public string Path => _entry?.Key.Path
        ?? throw new ObjectDisposedException(nameof(SegmentReadHandleLease));

    public SafeFileHandle Handle => _entry?.Handle
        ?? throw new ObjectDisposedException(nameof(SegmentReadHandleLease));

    internal SegmentReadHandleCache.CacheEntry Entry => _entry
        ?? throw new ObjectDisposedException(nameof(SegmentReadHandleLease));

    public void Dispose()
    {
        var owner = _owner;
        var entry = _entry;
        _owner = null;
        _entry = null;
        if (owner is not null && entry is not null)
        {
            owner.Release(entry);
        }
    }
}
