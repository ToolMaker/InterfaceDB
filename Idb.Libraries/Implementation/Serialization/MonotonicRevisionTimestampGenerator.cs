using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Monotonic within this generator, including wall-clock rollback. Independent clocks are not globally unique.</summary>
public sealed class MonotonicRevisionTimestampGenerator(TimeProvider? timeProvider = null) : IRevisionTimestampGenerator
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _last;

    public long Next(long observedRevisionTimestampUtcMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(observedRevisionTimestampUtcMicroseconds);
        lock (_gate)
        {
            return _last = Math.Max((_clock.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
                checked(Math.Max(_last, observedRevisionTimestampUtcMicroseconds) + 1));
        }
    }
}
