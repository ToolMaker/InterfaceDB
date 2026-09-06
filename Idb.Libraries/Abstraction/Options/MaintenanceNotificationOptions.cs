namespace Idb.Libraries.Abstractions.Options;

public sealed record MaintenanceNotificationOptions
{
    public int MaximumSubscribers { get; init; } = 32;
    public int QueueCapacityPerSubscriber { get; init; } = 32;
    public int MaximumConcurrentCallbacks { get; init; } = 4;
    public int MaximumCausalDepth { get; init; } = 16;

    public void Validate()
    {
        if (MaximumSubscribers is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(MaximumSubscribers));
        if (QueueCapacityPerSubscriber is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(QueueCapacityPerSubscriber));
        if (MaximumConcurrentCallbacks is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentCallbacks));
        if (MaximumCausalDepth is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumCausalDepth));
    }
}
