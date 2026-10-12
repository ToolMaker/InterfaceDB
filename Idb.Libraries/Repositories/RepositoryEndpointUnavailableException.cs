namespace Idb.Libraries.Repositories;

/// <summary>Explicitly marks temporary endpoint unavailability, distinct from authorization or data errors.</summary>
public class RepositoryEndpointUnavailableException : IOException
{
    public RepositoryEndpointUnavailableException(string message) : base(message) { }
    public RepositoryEndpointUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
