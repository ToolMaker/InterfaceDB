namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when the active storage provider cannot use an object's envelope.
/// </summary>
public sealed class InvalidStorageEnvelopeException : StorageException
{
    public InvalidStorageEnvelopeException(string reason, string? storageKey = null, string? providerLocator = null)
        : base($"The storage envelope is invalid: {reason}")
    {
        Reason = reason;
        StorageKey = storageKey;
        ProviderLocator = providerLocator;
    }

    public string Reason { get; }
    public string? StorageKey { get; }
    public string? ProviderLocator { get; }
}
