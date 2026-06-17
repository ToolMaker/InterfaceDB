namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Base exception for failures reported by InterfaceDb.
/// </summary>
public abstract class InterfaceDbException : Exception
{
    protected InterfaceDbException(string message)
        : base(message)
    {
    }

    protected InterfaceDbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
