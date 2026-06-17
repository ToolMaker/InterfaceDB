namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when an operation requires persisted identity but the object has no envelope.
/// </summary>
public sealed class StorageObjectNotPersistedException : StorageException
{
    public StorageObjectNotPersistedException(Type contractType)
        : base($"The '{contractType.FullName}' object has no storage envelope and cannot be treated as persisted.")
    {
        ContractType = contractType;
    }

    public Type ContractType { get; }
}
