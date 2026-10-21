using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Reader capability: returned objects are isolated from the committed cache, including
/// their nested data. Custom readers should implement this only when they uphold that guarantee.
/// </summary>
public interface IIsolatedObjectReader<TContract> : IObjectReader<TContract>
    where TContract : class, IStoredContract<TContract>
{
}
