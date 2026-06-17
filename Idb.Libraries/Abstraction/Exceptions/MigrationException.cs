namespace Idb.Libraries.Abstractions.Exceptions;

/// <summary>
/// Base exception for invalid migration definitions and migration execution failures.
/// </summary>
public abstract class MigrationException : InterfaceDbException
{
    protected MigrationException(string message)
        : base(message)
    {
    }

    protected MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
