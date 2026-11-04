namespace Idb.Commercial.Repositories;

/// <summary>
/// Controls whether callers may originate mutations through the resilient repository.
/// </summary>
public enum MutationAuthority
{
    /// <summary>
    /// The preferred endpoint accepts local writes and queues them for peer synchronization.
    /// </summary>
    PreferredWritable = 0,

    /// <summary>
    /// The peer is authoritative. The preferred endpoint is a read-through offline cache.
    /// </summary>
    PeerAuthoritativeReadOnly = 1
}
