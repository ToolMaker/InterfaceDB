namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Raised when an envelope-less object attempts to create an existing single-cardinality storage object.
/// </summary>
public sealed class StorageObjectAlreadyExistsException : InvalidOperationException
{
    public StorageObjectAlreadyExistsException(string path)
        : base($"A single-cardinality storage object already exists at '{path}'. Load the stored object before updating it.")
    {
        Path = path;
    }

    public string Path { get; }
}
