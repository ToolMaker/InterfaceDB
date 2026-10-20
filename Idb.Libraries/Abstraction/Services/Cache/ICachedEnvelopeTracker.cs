using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Cache;

/// <summary>Notifies an optional cache directory after an existing envelope is hydrated in place.</summary>
public interface ICachedEnvelopeTracker<TContract> where TContract : class, IStoredContract<TContract>
{
    /// <summary>Refreshes only if this exact envelope is still present; never restores removed entries.</summary>
    void RefreshCachedObject(IObjectEnvelope<TContract> envelope);
}
