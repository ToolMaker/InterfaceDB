using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>Observed mutations only. References protect an instance while its envelope is wrapped;
/// physical keys order different instances of the same stored entity. Unassigned insert identities
/// use an exclusive publication barrier; existing unrelated keys hold compatible shared leases.</summary>
internal sealed class NotificationMutationLocks<T> where T : class, IStoredContract<T>
{
    private sealed class ReferenceIdentity(long id)
    {
        internal readonly long Id = id;
        internal readonly SemaphoreSlim Gate = new(1, 1);
    }
    private sealed class KeyIdentity(string key)
    {
        internal readonly string Key = key;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
    }
    private readonly ConditionalWeakTable<T, ReferenceIdentity> _references = new();
    private readonly Dictionary<string, KeyIdentity> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly NotificationPublicationBarrier _publication = new();
    private long _nextReference;

    internal int RetainedKeyCount { get { lock (_gate) return _keys.Count; } }

    internal ValueTask<IDisposable> AcquireAsync(T entity, CancellationToken token)
        => AcquireAsync(new[] { entity }, token);

    internal async ValueTask<IDisposable> AcquireAsync(IReadOnlyList<T> entities, CancellationToken token)
    {
        var refs = new ReferenceIdentity[entities.Count];
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < entities.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(entities[i]);
            if (!seen.Add(entities[i])) throw new ArgumentException("An observed batch cannot repeat the same object instance.");
            refs[i] = _references.GetValue(entities[i], _ => new(Interlocked.Increment(ref _nextReference)));
        }
        Array.Sort(refs, static (a, b) => a.Id.CompareTo(b.Id));
        var acquiredRefs = 0;
        var acquiredKeys = 0;
        KeyIdentity[] keys = [];
        IDisposable? publication = null;
        try
        {
            for (; acquiredRefs < refs.Length; acquiredRefs++)
                await refs[acquiredRefs].Gate.WaitAsync(token).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var unassigned = false;
            foreach (var entity in entities)
            {
                if (entity.Envelope is not { EnvelopeCacheKey.Length: > 0 } envelope) unassigned = true;
                else if (!names.Add(envelope.EnvelopeCacheKey))
                    throw new ArgumentException("An observed batch cannot contain duplicate physical identities.");
            }
            publication = await _publication.AcquireAsync(unassigned, token).ConfigureAwait(false);
            if (!unassigned)
            {
                var ordered = names.Order(StringComparer.Ordinal).ToArray();
                keys = new KeyIdentity[ordered.Length];
                lock (_gate)
                {
                    for (var i = 0; i < ordered.Length; i++)
                    {
                        if (!_keys.TryGetValue(ordered[i], out var entry))
                            _keys.Add(ordered[i], entry = new(ordered[i]));
                        entry.Users++; keys[i] = entry;
                    }
                }
                for (; acquiredKeys < keys.Length; acquiredKeys++)
                    await keys[acquiredKeys].Gate.WaitAsync(token).ConfigureAwait(false);
            }
            return new Lease(this, refs, keys, publication);
        }
        catch
        {
            for (var i = acquiredKeys - 1; i >= 0; i--) keys[i].Gate.Release();
            ReleaseKeyReferences(keys);
            publication?.Dispose();
            for (var i = acquiredRefs - 1; i >= 0; i--) refs[i].Gate.Release();
            throw;
        }
    }

    private void ReleaseKeyReferences(KeyIdentity[] keys)
    {
        lock (_gate)
            foreach (var key in keys)
                if (--key.Users == 0) _keys.Remove(key.Key);
    }

    private sealed class Lease(NotificationMutationLocks<T> owner, ReferenceIdentity[] refs,
        KeyIdentity[] keys, IDisposable publication) : IDisposable
    {
        private NotificationMutationLocks<T>? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            for (var i = keys.Length - 1; i >= 0; i--) keys[i].Gate.Release();
            current.ReleaseKeyReferences(keys);
            publication.Dispose();
            for (var i = refs.Length - 1; i >= 0; i--) refs[i].Gate.Release();
        }
    }
}

/// <summary>Fair async reader/writer barrier, used only for observed publication of new identities.</summary>
internal sealed class NotificationPublicationBarrier
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private int _readers;
    private bool _writer;

    private sealed class Waiter(NotificationPublicationBarrier owner, bool exclusive, CancellationToken token)
    {
        internal readonly NotificationPublicationBarrier Owner = owner;
        internal readonly bool Exclusive = exclusive;
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<IDisposable> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }

    internal ValueTask<IDisposable> AcquireAsync(bool exclusive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_waiting.Count == 0 && !_writer && (!exclusive || _readers == 0))
                return ValueTask.FromResult<IDisposable>(Grant(exclusive));
            waiter = new(this, exclusive, token);
            waiter.Node = _waiting.AddLast(waiter);
        }
        return WaitAsync(waiter);
    }

    private static async ValueTask<IDisposable> WaitAsync(Waiter waiter)
    {
        using var registration = waiter.Token.UnsafeRegister(static value =>
        {
            var pending = (Waiter)value!;
            pending.Owner.Cancel(pending);
        }, waiter);
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private Lease Grant(bool exclusive)
    {
        if (exclusive) _writer = true; else _readers++;
        return new(this, exclusive);
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return; // Grant won; the caller owns and must release the lease.
            _waiting.Remove(waiter.Node); waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.Token);
            Drain();
        }
    }

    private void Release(bool exclusive)
    {
        lock (_gate)
        {
            if (exclusive) _writer = false; else _readers--;
            Drain();
        }
    }

    private void Drain()
    {
        while (!_writer && _waiting.First is { } head)
        {
            var next = head.Value;
            if (next.Exclusive && _readers != 0) return;
            _waiting.RemoveFirst(); next.Node = null;
            next.Completion.TrySetResult(Grant(next.Exclusive));
        }
    }

    private sealed class Lease(NotificationPublicationBarrier owner, bool exclusive) : IDisposable
    {
        private NotificationPublicationBarrier? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(exclusive);
    }
}
