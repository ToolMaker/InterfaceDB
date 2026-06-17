namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when a provider cannot safely complete an operation because of competing storage activity.
/// </summary>
public sealed class StorageConcurrencyException : StorageException
{
    public StorageConcurrencyException(
        string message,
        string? storageKey,
        string? providerLocator,
        Exception innerException)
        : base(message, innerException)
    {
        StorageKey = storageKey;
        ProviderLocator = providerLocator;
    }

    public string? StorageKey { get; }
    public string? ProviderLocator { get; }
}
