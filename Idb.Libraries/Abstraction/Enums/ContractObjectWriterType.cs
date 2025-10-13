namespace Idb.Libraries.Abstraction.Enums;

/// <summary>
/// Specifies the object writer implementation to use for a contract.
/// </summary>
public enum ContractObjectWriterType
{
    /// <summary>
    /// A writer must be supplied by the host; no built-in registration will be provided.
    /// </summary>
    None = 0,

    /// <summary>
    /// Use the built-in binary writer implementation.
    /// </summary>
    Binary,
}
