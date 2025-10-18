using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Composite facade that wraps reader, writer, and serializer behaviours behind a single dependency.
/// Concrete implementations can add concerns like hashing or caching internally without exposing them upstream.
/// </summary>
/// <typeparam name="TContract">Contract interface represented in the repository.</typeparam>
public interface IEnvelopeRepository<TContract> :
    IObjectReader<TContract>,
    IObjectWriter<TContract>,
    IObjectSerializer<TContract>
    where TContract : class, IRepoDto<TContract>
{

}
