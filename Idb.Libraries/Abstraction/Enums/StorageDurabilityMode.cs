namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Describes the persistence guarantee exposed by a storage repository.
/// </summary>
public enum StorageDurabilityMode
{
    /// <summary>
    /// A completed write may still rely on operating-system or device caches and is not guaranteed
    /// to survive an immediate process or power failure.
    /// </summary>
    Performance = 0,

    /// <summary>
    /// A completed write has crossed the provider's durable commit boundary and is recoverable
    /// according to that provider's documented storage contract.
    /// </summary>
    Durable = 1
}
