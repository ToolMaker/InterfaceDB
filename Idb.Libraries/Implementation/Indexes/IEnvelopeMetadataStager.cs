using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Indexes;

internal interface IEnvelopeMetadataStager<TContract>
    where TContract : class, IStoredContract<TContract>
{
    IObjectEnvelope<TContract> Stage(
        TContract detachedEntity,
        string? existingEnvelopeCacheKey,
        byte[]? extensionMetadata);
}
