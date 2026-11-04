namespace Idb.Commercial.Repositories;

/// <summary>
/// Indicates that the peer advanced a key whose preferred endpoint is configured as authoritative.
/// </summary>
public sealed class ResilientRevisionConflictException : InvalidOperationException
{
    public ResilientRevisionConflictException(
        string storageKey,
        long preferredRevisionTimestampUtcMicroseconds,
        long peerRevisionTimestampUtcMicroseconds)
        : base(
            $"Peer revision {peerRevisionTimestampUtcMicroseconds} is later than preferred revision " +
            $"{preferredRevisionTimestampUtcMicroseconds} for authoritative key '{storageKey}'.")
    {
        StorageKey = storageKey;
        PreferredRevisionTimestampUtcMicroseconds = preferredRevisionTimestampUtcMicroseconds;
        PeerRevisionTimestampUtcMicroseconds = peerRevisionTimestampUtcMicroseconds;
    }

    public string StorageKey { get; }

    public long PreferredRevisionTimestampUtcMicroseconds { get; }

    public long PeerRevisionTimestampUtcMicroseconds { get; }
}
