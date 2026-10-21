using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>An owned encoded snapshot. Each materialization is isolated from storage and other consumers.</summary>
internal sealed class CommittedEnvelopeSnapshot<TContract> : ICommittedEnvelopeSnapshot<TContract> where TContract : class, IStoredContract<TContract>
{
    private readonly byte[] _payload;
    private readonly IObjectSerializer<TContract> _serializer;
    private readonly IEnvelopeLifecycle<TContract> _lifecycle;
    private readonly string _key;
    private readonly string? _location;
    private readonly byte[]? _associatedData;

    public CommittedEnvelopeSnapshot(byte[] payload, IObjectSerializer<TContract> serializer,
        IEnvelopeLifecycle<TContract> lifecycle, string key, string? location,
        byte[]? associatedData = null)
    {
        // Only trusted providers construct this type, transferring their newly owned read buffer.
        _payload = payload;
        _serializer = serializer;
        _lifecycle = lifecycle;
        _key = key;
        _location = location;
        _associatedData = associatedData;
    }

    public int ByteLength => _payload.Length;

    public TContract Materialize(bool attachEnvelope = true)
    {
        // Formatters may retain input slices. Never lend the immutable event's backing bytes.
        var payload = _payload.ToArray();
        var envelope = _associatedData is null
            ? _serializer.Deserialize(payload)
            : ContextualObjectSerialization.Deserialize(_serializer, payload, _associatedData);
        envelope.EnvelopeCacheKey = _key;
        envelope.Location = _location;
        envelope.State = StorageObjectState.Active;
        var plain = envelope.ObjectInstance ?? throw new InvalidDataException("Snapshot has no entity.");
        var entity = _lifecycle.Unwrap(envelope);
        if (attachEnvelope)
        {
            StoredContractEnvelopeAccessor<TContract>.SetEnvelope(entity, envelope);
            envelope.ObjectInstance = entity;
            return entity;
        }

        // Field-encrypted reads may use a proxy that owns its envelope. For a deletion payload,
        // materialize the values accessible to this repository into the isolated plain instance.
        foreach (var name in envelope.EncryptedFields.Keys)
        {
            var property = typeof(TContract).GetInterfaces().Append(typeof(TContract))
                .SelectMany(type => type.GetProperties()).FirstOrDefault(property => property.Name == name)
                ?? throw new NotSupportedException("Encrypted notification property is unavailable.");
            property.SetValue(plain, property.GetValue(entity));
        }
        StoredContractEnvelopeAccessor<TContract>.SetEnvelope(plain, null);
        return plain;
    }
}
