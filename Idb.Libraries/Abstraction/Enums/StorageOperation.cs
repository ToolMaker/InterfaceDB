namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Identifies a provider-neutral storage operation.
/// </summary>
public enum StorageOperation
{
    Initialize = 0,
    Read = 1,
    Query = 2,
    Write = 3,
    Delete = 4,
    Migrate = 5
}
