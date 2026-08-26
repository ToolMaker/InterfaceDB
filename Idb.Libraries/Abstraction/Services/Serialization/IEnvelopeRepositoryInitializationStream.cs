using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Optional repository capability that initializes storage and exposes the objects needed to
/// build volatile indexes in one hydration pass.
/// </summary>
/// <typeparam name="TContract">Contract type stored by the repository.</typeparam>
public interface IEnvelopeRepositoryInitializationStream<TContract>
    where TContract : class, IStoredContract<TContract>
{
    /// <summary>
    /// Initializes the repository without a separate eager payload pass and enumerates each live
    /// envelope once for index construction.
    /// </summary>
    IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(
        CancellationToken cancellationToken = default);
}
