namespace Idb.Commercial.Repositories;

/// <summary>One schedule per retry worker. Never changes admission, authentication or durable pending state.</summary>
internal sealed class RepositoryRetryBackoff(TimeSpan initial, Func<double>? jitter = null)
{
    private readonly double maximumMs = Math.Max(initial.TotalMilliseconds, TimeSpan.FromMinutes(2).TotalMilliseconds);
    private readonly Func<double> random = jitter ?? Random.Shared.NextDouble;
    private int failures;
    public void Reset() => failures = 0;
    public TimeSpan NextDelay()
    {
        var exponent = Math.Min(failures++, 30);
        if (failures > 31) failures = 31;
        var ceiling = Math.Min(maximumMs, initial.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(exponent == 0 ? ceiling : ceiling * (0.8 + 0.2 * Math.Clamp(random(), 0, 1)));
    }
}
