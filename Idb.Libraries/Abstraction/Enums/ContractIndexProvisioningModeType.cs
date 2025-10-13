namespace Idb.Libraries.Abstraction.Enums;

/// <summary>
/// Specifies how repository indexes are provisioned for a contract.
/// </summary>
public enum ContractIndexProvisioningModeType
{
    /// <summary>
    /// No indexes will be provisioned. Consumers must register them manually.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indexes are created on demand when clients register them.
    /// </summary>
    Dynamic,

    /// <summary>
    /// Only indexes defined in configuration are created.
    /// </summary>
    Fixed,

    /// <summary>
    /// Configured indexes start the contract and clients can still register additional ones.
    /// </summary>
    Mixed
}
