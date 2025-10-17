namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Specifies the object reader implementation to use for a contract.
/// </summary>
public enum ContractObjectReaderType
{
    /// <summary>
    /// A reader must be supplied by the host; no built-in registration will be provided.
    /// </summary>
    None = 0,

    /// <summary>
    /// Use the built-in binary reader implementation.
    /// </summary>
    Binary,
}

