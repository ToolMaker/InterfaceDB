namespace Idb.Libraries.Abstraction.Enums;

/// <summary>
/// Specifies the object reader implementation to use for a contract.
/// </summary>
public enum ContractObjectReaderType
{
    /// <summary>
    /// Use the default reader registered in DI.
    /// </summary>
    None = 0,

    /// <summary>
    /// Use the built-in MessagePack-based reader.
    /// </summary>
    MessagePack,
}

