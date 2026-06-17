namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when an operation would resurrect or modify an object whose envelope was deleted.
/// </summary>
public sealed class StorageObjectDeletedException : StorageException
{
    public StorageObjectDeletedException(string? storageKey, string? providerLocator)
        : base($"Storage object '{storageKey ?? "<unknown>"}' has been deleted and cannot be saved.")
    {
        StorageKey = storageKey;
        ProviderLocator = providerLocator;
    }

    public string? StorageKey { get; }
    public string? ProviderLocator { get; }
}
