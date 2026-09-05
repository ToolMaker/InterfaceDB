namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Enumerates repository implementations available for contract storage.
/// </summary>
public enum ContractRepositoryType
{
    /// <summary>
    /// No repository explicitly configured.
    /// </summary>
    None = 0,

    /// <summary>
    /// Uses the flat file-system backed repository implementation.
    /// </summary>
    FileSystem,

    /// <summary>
    /// Uses the append-only, striped SegmentCore storage provider.
    /// </summary>
    SegmentCore,

    /// <summary>
    /// Uses the adaptive, bounded AtlasCore storage provider.
    /// </summary>
    AtlasCore,

}
