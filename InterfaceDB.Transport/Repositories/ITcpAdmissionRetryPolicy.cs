namespace Idb.Transport.Repositories;

/// <summary>Controls retries of rejected admission only; no storage operation has been sent.</summary>
public interface ITcpAdmissionRetryPolicy
{
    TimeSpan GetDelay(int rejectedAttempts, TimeSpan initialDelay);
}

/// <summary>Exponential backoff with equal jitter, capped at one second or the configured initial delay.</summary>
public sealed class TcpAdmissionRetryPolicy : ITcpAdmissionRetryPolicy
{
    public static TcpAdmissionRetryPolicy Instance { get; } = new();

    public TimeSpan GetDelay(int rejectedAttempts, TimeSpan initialDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rejectedAttempts);
        TransportValidation.Duration(initialDelay, nameof(initialDelay));
        var cap = Math.Max(initialDelay.TotalMilliseconds, 1000);
        var upper = Math.Min(cap, initialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(rejectedAttempts - 1, 20)));
        return TimeSpan.FromMilliseconds(upper * (0.5 + Random.Shared.NextDouble() * 0.5));
    }
}
