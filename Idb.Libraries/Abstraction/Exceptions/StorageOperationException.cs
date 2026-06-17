using Idb.Libraries.Abstractions.Enums;

namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Wraps a provider-specific failure while retaining provider-neutral operation context.
/// </summary>
public sealed class StorageOperationException : StorageException
{
    public StorageOperationException(
        StorageOperation operation,
        string? storageKey,
        string? providerLocator,
        Exception innerException)
        : base($"Storage operation '{operation}' failed for object '{storageKey ?? "<unknown>"}'.", innerException)
    {
        Operation = operation;
        StorageKey = storageKey;
        ProviderLocator = providerLocator;
    }

    public StorageOperation Operation { get; }
    public string? StorageKey { get; }
    public string? ProviderLocator { get; }
}
