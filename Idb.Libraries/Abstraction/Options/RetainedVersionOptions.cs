namespace Idb.Libraries.Abstractions.Options;

/// <summary>Explicit, process-scoped historical read admission. Null policy disables retention.</summary>
public sealed record RetainedVersionOptions
{
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public int MaximumSessions { get; init; } = 256;
    public int MaximumRecords { get; init; } = 256;
    public long MaximumBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public void Validate()
    {
        if (LeaseDuration <= TimeSpan.Zero || MaximumLifetime < LeaseDuration
            || MaximumLifetime > TimeSpan.FromDays(1) || CleanupInterval <= TimeSpan.Zero
            || CleanupInterval > TimeSpan.FromMinutes(1) || MaximumSessions < 1
            || MaximumRecords < 1 || MaximumBytes < 1 || TimeProvider is null)
            throw new ArgumentOutOfRangeException(nameof(RetainedVersionOptions), "Invalid finite retention limits.");
    }
}
