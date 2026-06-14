using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>
/// Result of applying a storage change to the envelope cache.
/// </summary>
public sealed record EnvelopeStorageChangeResult<TContract>(
    EnvelopeStorageChangeType ChangeType,
    IObjectEnvelope<TContract>? OldEnvelope,
    IObjectEnvelope<TContract>? NewEnvelope)
    where TContract : class, IStoredContract<TContract>;
