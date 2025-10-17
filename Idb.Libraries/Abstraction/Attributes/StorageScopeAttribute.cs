using System;

namespace Idb.Libraries.Abstractions.Attributes;

/// <summary>
/// Marks an interface contract as an on-disk storage scope.
/// Concrete implementations are discovered via reflection at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class StorageScopeAttribute : Attribute
{
    /// <summary>
    /// Code-defined version tag used to segregate persisted data (e.g., "1.0", "2024-09", "beta").
    /// Defaults to "1.0" when not specified.
    /// </summary>
    public string? VersionTag { get; init; }

    /// <summary>
    /// Optional factory type used to compute file names for stored objects.
    /// </summary>
    public Type? FileNameFactory { get; init; }
}

