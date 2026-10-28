namespace Idb.Libraries.Repositories;

/// <summary>
/// Supplies an account/dataset identity for cache isolation. A remote implementation may need initial
/// server approval; persist that approval or supply trusted server configuration for offline startup.
/// This is an identity guard, not an authorization decision; applications still enforce offline access policy.
/// </summary>
public interface IRepositoryCacheIdentity
{
    ValueTask<string> GetCacheIdentityAsync(CancellationToken cancellationToken = default);
}
