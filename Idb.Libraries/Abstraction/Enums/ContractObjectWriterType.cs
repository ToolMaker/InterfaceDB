namespace Idb.Libraries.Abstraction.Enums;

/// <summary>
/// Specifies the object writer implementation to use for a contract.
/// </summary>
public enum ContractObjectWriterType
{
    /// <summary>
    /// Use the default writer registered in DI.
    /// </summary>
    None = 0,

    /// <summary>
    /// Use the built-in MessagePack-based writer.
    /// </summary>
    MessagePack,
}

