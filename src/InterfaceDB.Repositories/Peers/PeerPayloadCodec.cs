using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.Peers;

public interface IPeerPayloadCodec<T>
{
    DateTimeOffset? GetHistoryDate(T entity) => null;
    ValueTask<byte[]> EncodeAsync(T entity, Guid mutationId, CancellationToken cancellationToken = default);
    T Decode(PeerRecord record);
}

/// <summary>Uses the same serializer and envelope lifecycle registered for the local database.</summary>
public sealed class PeerPayloadCodec<T>(IObjectSerializer<T> serializer, IEnvelopeLifecycle<T> lifecycle,
    IResilientEntityMapper<T> mapper, ISequenceAccessor<T> sequences, string datasetIdentity, string contractVersion,
    PeerLimits limits, IPeerHistoryDateAccessor<T>? historyDate = null) : IPeerPayloadCodec<T> where T : class, IStoredContract<T>
{
    public DateTimeOffset? GetHistoryDate(T entity) => historyDate?.Get(entity);
    public async ValueTask<byte[]> EncodeAsync(T entity, Guid mutationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detached = mapper.CreateDetachedCopy(entity);
        sequences.Set(detached, null);
        var id = mapper.GetStorageKey(detached);
        var envelope = await lifecycle.WrapAsync(detached).ConfigureAwait(false);
        envelope.EnvelopeCacheKey = Key(id);
        envelope.Location = null; envelope.ExtensionMetadata = null;
        envelope.RevisionTimestampUtcMicroseconds = 0; envelope.VersionTag = contractVersion;
        var payload = serializer is IContextualObjectSerializer<T> contextual
            ? contextual.Serialize(envelope, Context(id, mutationId)) : serializer.Serialize(envelope);
        if (payload.Length > limits.MaximumPayloadBytes) throw new PeerProtocolException(PeerError.LimitExceeded);
        return payload;
    }
    public T Decode(PeerRecord record)
    {
        var header = record.Descriptor;
        if (record.Payload.Length > limits.MaximumPayloadBytes || Convert.ToHexString(SHA256.HashData(record.Payload)) != header.PayloadHash)
            throw new InvalidDataException("Peer payload digest does not match its assignment.");
        var envelope = serializer is IContextualObjectSerializer<T> contextual
            ? contextual.Deserialize(record.Payload, Context(header.RecordId, header.MutationId)) : serializer.Deserialize(record.Payload);
        if (envelope.VersionTag != contractVersion || envelope.EnvelopeCacheKey != Key(header.RecordId)
            || envelope.RevisionTimestampUtcMicroseconds != 0 || envelope.ExtensionMetadata is not null)
            throw new InvalidDataException("Peer payload context is incompatible.");
        var entity = mapper.CreateDetachedCopy(lifecycle.Unwrap(envelope));
        if (mapper.GetStorageKey(entity) != header.RecordId || sequences.Get(entity) is not null
            || GetHistoryDate(entity) != header.HistoryDateUtc)
            throw new InvalidDataException("Peer payload identity or sequence was forged.");
        sequences.Set(entity, header.Sequence == 0 ? null : header.Sequence);
        return entity;
    }
    private static string Key(string id) => "peer-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private byte[] Context(string id, Guid operation) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new[] { "InterfaceDB-peer-v1", datasetIdentity, contractVersion, id, operation.ToString("N") });
}
