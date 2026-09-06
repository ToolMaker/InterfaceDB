using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Libraries.Implementation.Maintenance;

/// <summary>Bounded concurrent work plus a shared token bucket. No worker, timer or wait occurs
/// until a caller explicitly acquires maintenance admission. Caller cancellation returns its slot.</summary>
public sealed class MaintenanceBudget : IMaintenanceBudget
{
    private readonly MaintenanceBudgetOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _rateAdmission = new(1, 1);
    private readonly object _rateGate = new();
    private double _tokens;
    private long _lastRefill;
    private int _active, _waiting;
    private long _units, _bytes, _waitTicks;

    public MaintenanceBudget(MaintenanceBudgetOptions? options = null)
    {
        _options = options ?? new(); _options.Validate();
        _slots = new(_options.MaximumConcurrentUnits, _options.MaximumConcurrentUnits);
        _tokens = _options.MaximumWorkUnitBytes;
        _lastRefill = _options.TimeProvider.GetTimestamp();
    }

    public long MaximumWorkUnitBytes => _options.MaximumWorkUnitBytes;
    public MaintenanceBudgetStatistics Statistics => new(Volatile.Read(ref _active), Volatile.Read(ref _waiting),
        Interlocked.Read(ref _units), Interlocked.Read(ref _bytes), TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks)));

    public async ValueTask<IDisposable> AcquireAsync(long ioBytes, CancellationToken cancellationToken = default)
    {
        if (ioBytes < 0 || ioBytes > MaximumWorkUnitBytes) throw new ArgumentOutOfRangeException(nameof(ioBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var started = _options.TimeProvider.GetTimestamp();
        Interlocked.Increment(ref _waiting);
        var acquired = false;
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            if (_options.BytesPerSecond != 0 && ioBytes != 0)
            {
                // A stream of tiny units must not consume every refill ahead of an already waiting large unit.
                await _rateAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await ChargeAsync(ioBytes, cancellationToken).ConfigureAwait(false); }
                finally { _rateAdmission.Release(); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _units);
            Interlocked.Add(ref _bytes, ioBytes);
            return new Lease(this);
        }
        catch
        {
            if (acquired) _slots.Release();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Add(ref _waitTicks, _options.TimeProvider.GetElapsedTime(started).Ticks);
        }
    }

    private async ValueTask ChargeAsync(long bytes, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan delay;
            lock (_rateGate)
            {
                var now = _options.TimeProvider.GetTimestamp();
                var elapsed = _options.TimeProvider.GetElapsedTime(_lastRefill, now).TotalSeconds;
                _tokens = Math.Min(MaximumWorkUnitBytes, _tokens + Math.Max(0, elapsed) * _options.BytesPerSecond);
                _lastRefill = now;
                if (_tokens >= bytes) { _tokens -= bytes; return; }
                // Bounded waits also recheck cancellation; no future reservations that cancellation could strand.
                delay = TimeSpan.FromSeconds(Math.Clamp((bytes - _tokens) / _options.BytesPerSecond, 0.001, 1));
            }
            await Task.Delay(delay, _options.TimeProvider, token).ConfigureAwait(false);
        }
    }

    private sealed class Lease(MaintenanceBudget owner) : IDisposable
    {
        private MaintenanceBudget? _owner = owner;
        public void Dispose()
        {
            var budget = Interlocked.Exchange(ref _owner, null);
            if (budget is null) return;
            Interlocked.Decrement(ref budget._active);
            budget._slots.Release();
        }
    }
}
