using Idb.Libraries.Abstractions.Services.Repository;

namespace Idb.Libraries.Abstractions.Services.Caching;

public interface ICachingRepositoryNode : IRepositoryNode
{
    void ClearCache(Type? contractType = null);
}
