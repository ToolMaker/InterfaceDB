namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Base exception for storage-provider failures.
/// </summary>
public abstract class StorageException : InterfaceDbException
{
    protected StorageException(string message)
        : base(message)
    {
    }

    protected StorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
