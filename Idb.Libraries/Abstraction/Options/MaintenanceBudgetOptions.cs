namespace Idb.Libraries.Abstractions.Options;

public sealed record MaintenanceBudgetOptions
{
    public int MaximumConcurrentUnits { get; init; } = 1;
    /// <summary>Zero disables byte-rate throttling, not concurrency admission.</summary>
    public long BytesPerSecond { get; init; }
    /// <summary>Also the maximum burst. Split larger work before admission; never silently undercharge it.</summary>
    public long MaximumWorkUnitBytes { get; init; } = 16L * 1024 * 1024;
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public void Validate()
    {
        if (MaximumConcurrentUnits is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentUnits));
        if (BytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(BytesPerSecond));
        if (MaximumWorkUnitBytes is < 1 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumWorkUnitBytes));
        ArgumentNullException.ThrowIfNull(TimeProvider);
    }
}
