namespace Idb.Libraries.Abstractions.Enums;

/// <summary>
/// Defines whether a factory-resolved storage identity can contain one or many stored objects.
/// </summary>
public enum StorageObjectCardinality
{
    /// <summary>
    /// One canonical stored object may exist for the resolved key and location.
    /// </summary>
    Single = 0,

    /// <summary>
    /// Each envelope-less save receives a unique physical identity from the factory.
    /// </summary>
    Multiple = 1
}
