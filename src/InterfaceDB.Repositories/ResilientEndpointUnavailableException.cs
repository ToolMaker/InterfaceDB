using Idb.Libraries.Repositories;
namespace Idb.Commercial.Repositories;

/// <summary>
/// Explicitly marks a provider failure as temporary endpoint unavailability.
/// </summary>
public sealed class ResilientEndpointUnavailableException : RepositoryEndpointUnavailableException
{
    public ResilientEndpointUnavailableException(string message)
        : base(message)
    {
    }

    public ResilientEndpointUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
