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
    /// Uses the file-system backed repository implementation.
    /// </summary>
    FileSystem,
}

