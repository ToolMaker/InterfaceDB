namespace Idb.Storage.AtlasCore.Internal;

/// <summary>Bounds physical partition write I/O, not the number of logical files/queues.</summary>
internal sealed class PartitionWriteScheduler(int maximum)
{
    private readonly SemaphoreSlim _permits = new(maximum, maximum);
    private int _active;
    private int _peak;
    public int Maximum { get; } = maximum;
    public int Peak => Volatile.Read(ref _peak);
    public async ValueTask EnterAsync()
    {
        await _permits.WaitAsync().ConfigureAwait(false);
        var active = Interlocked.Increment(ref _active);
        int observed;
        do { observed = Volatile.Read(ref _peak); }
        while (active > observed && Interlocked.CompareExchange(ref _peak, active, observed) != observed);
    }
    public void Exit()
    {
        Interlocked.Decrement(ref _active);
        _permits.Release();
    }
}
