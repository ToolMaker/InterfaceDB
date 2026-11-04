namespace Idb.Commercial.Repositories;

public enum ResilientEndpoint
{
    Preferred = 0,
    Peer = 1
}

public enum ResilientRepositoryOperation
{
    Initialize = 0,
    Read = 1,
    Save = 2,
    Delete = 3,
    Synchronize = 4,
    NotifyChanged = 5
}

/// <summary>
/// Describes a handled endpoint or synchronization failure.
/// </summary>
public sealed record ResilientRepositoryFailure(
    ResilientEndpoint? Endpoint,
    ResilientRepositoryOperation Operation,
    string? StorageKey,
    Exception Exception,
    bool IsTransient = false);
