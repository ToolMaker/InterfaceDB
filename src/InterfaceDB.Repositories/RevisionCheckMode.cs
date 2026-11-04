namespace Idb.Commercial.Repositories;

/// <summary>
/// Controls when reads consult the peer endpoint for a later revision.
/// </summary>
public enum RevisionCheckMode
{
    /// <summary>
    /// A successful preferred read does not contact the peer. The peer is used only when the
    /// preferred value is missing or the preferred endpoint is unavailable.
    /// </summary>
    PreferredOnly = 0,

    /// <summary>
    /// Uses the preferred fast path until an authenticated change signal marks a key or dataset dirty.
    /// </summary>
    WhenInvalidated = 1,

    /// <summary>
    /// Reads both endpoints and selects the later revision.
    /// </summary>
    AlwaysCompare = 2
}
