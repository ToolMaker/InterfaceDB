using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories;

/// <summary>After a trusted provider migration, rebind copied synchronization metadata to the new baseline identity.</summary>
public static class RepositoryCacheMigration
{
    public static async ValueTask RebindAsync<T>(IEnvelopeMetadataRepository<T> target, string cacheScope,
        string previousIdentity, string selectedIdentity, IResilientEntityMapper<T> mapper, int maximumRecords = 100_000, CancellationToken token = default)
        where T : class, IStoredContract<T>
    {
        var records = new List<T>();
        await foreach (var entity in target.QueryManyAsync(_ => true, token).ConfigureAwait(false))
        {
            if (records.Count >= maximumRecords) throw new InvalidDataException("Migration cache bound exceeded.");
            var metadata = ResilientEnvelopeMetadataCodec.Decode(entity.Envelope!);
            if (metadata.CacheScope != cacheScope || metadata.RemoteIdentity != previousIdentity && metadata.RemoteIdentity != selectedIdentity)
                throw new InvalidDataException("The migration cache belongs to another identity.");
            records.Add(entity);
        }
        foreach (var entity in records)
        {
            var envelope = entity.Envelope!;
            var metadata = ResilientEnvelopeMetadataCodec.Decode(envelope);
            var detached = mapper.CreateDetachedCopy(entity);
            if (detached is null || detached.Envelope is not null || ReferenceEquals(detached, entity))
                throw new InvalidOperationException("Migration metadata requires a detached copy.");
            await target.SaveWithExtensionMetadataAsync(detached, envelope.EnvelopeCacheKey,
                ResilientEnvelopeMetadataCodec.Encode(metadata with { RemoteIdentity = selectedIdentity, RevisionTimestampUtcMicroseconds = 0 }),
                new(envelope.RevisionTimestampUtcMicroseconds, EnvelopeRevisionAction.MetadataOnly,
                    ExpectedExtensionMetadata: envelope.ExtensionMetadata), token).ConfigureAwait(false);
        }
    }
}
