namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Selects a documented starting point for provider-neutral concurrency and for
/// provider-specific physical storage settings.
/// </summary>
public enum StorageHardwareProfile
{
    /// <summary>
    /// Conservative, cross-platform settings that do not assume a particular filesystem or device.
    /// </summary>
    Portable = 0,

    /// <summary>
    /// Reduces queue depth, parallelism, buffers, and eager hydration for constrained processes.
    /// </summary>
    LowMemory = 1,

    /// <summary>
    /// Favors queue depth, batching, and parallelism while retaining portable durability mechanisms.
    /// </summary>
    HighThroughput = 2,

    /// <summary>
    /// Uses the qualified Windows/NVMe starting point. Deployments must still validate their own
    /// controller, filesystem, power-loss behavior, and tail latency before selecting it.
    /// </summary>
    WindowsNvme = 3,

    /// <summary>
    /// Indicates that provider-specific options are supplied explicitly by the application.
    /// </summary>
    Custom = 4
}
