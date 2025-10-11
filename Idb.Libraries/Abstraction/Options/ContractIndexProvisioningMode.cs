namespace Idb.Libraries.Abstractions.Options;

/// <summary>
/// Specifies how repository indexes are provisioned for a contract.
/// </summary>
public enum ContractIndexProvisioningMode
{
    /// <summary>
    /// Indexes are created on demand when clients register them.
    /// </summary>
    Dynamic = 0,

    /// <summary>
    /// Only indexes defined in configuration are created.
    /// </summary>
    Fixed,

    /// <summary>
    /// Configured indexes start the contract and clients can still register additional ones.
    /// </summary>
    Mixed
}
