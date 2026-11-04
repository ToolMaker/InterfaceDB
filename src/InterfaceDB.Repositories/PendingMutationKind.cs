namespace Idb.Commercial.Repositories;

/// <summary>
/// Identifies the state snapshot carried by a pending synchronization operation.
/// </summary>
public enum PendingMutationKind : byte
{
    None = 0,
    Upsert = 1,
    Delete = 2
}
