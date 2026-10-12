namespace Idb.Libraries.Repositories;

/// <summary>
/// Supplies an account/dataset identity for cache isolation without requiring a storage connection.
/// This is an identity guard, not an authorization decision; applications still enforce offline access policy.
/// </summary>
public interface IRepositoryCacheIdentity
{
    ValueTask<string> GetCacheIdentityAsync(CancellationToken cancellationToken = default);
}
