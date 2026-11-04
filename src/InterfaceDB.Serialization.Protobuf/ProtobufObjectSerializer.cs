using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using ProtoBuf;
using ProtoBuf.Meta;

namespace Idb.Serialization.Protobuf;

/// <summary>Protobuf envelope with an explicitly mapped entity payload.
/// Configure the model completely before construction, which compiles and freezes it.</summary>
public sealed class ProtobufObjectSerializer<TContract> : IRevisionObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public const string EnvelopeFormat = "InterfaceDB.Protobuf.Envelope.v1";
    private static readonly RuntimeTypeModel EnvelopeModel = CreateEnvelopeModel();
    private readonly RuntimeTypeModel _model;
    private readonly Dictionary<string, Type> _types;
    private readonly Dictionary<Type, string> _ids;
    private readonly int _maximumBytes;

    public ProtobufObjectSerializer(RuntimeTypeModel model, IReadOnlyDictionary<string, Type> types,
        int maximumPayloadBytes = 4 * 1024 * 1024, int maximumDepth = 64)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        if (types.Count == 0) throw new ArgumentException("Enroll at least one concrete contract type.", nameof(types));
        _types = new(StringComparer.Ordinal);
        _ids = new();
        foreach (var (id, type) in types)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (type is null || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters || !typeof(TContract).IsAssignableFrom(type))
                throw new ArgumentException("Each registered type must be a concrete implementation of the stored contract.", nameof(types));
            if (!model.IsDefined(type)) throw new ArgumentException($"Configure Protobuf fields for {type.FullName} before registering it.", nameof(model));
            _types.Add(id, type);
            if (!_ids.TryAdd(type, id)) throw new ArgumentException("A runtime type may have only one persistent type ID.", nameof(types));
        }
        model.AutoAddMissingTypes = false;
        // Persist CLR DateTime.Kind alongside ticks in protobuf-net's date representation.
        model.IncludeDateTimeKind = true;
        model.MaxDepth = maximumDepth;
        model.CompileInPlace();
        model.Freeze();
        _model = model;
        _maximumBytes = maximumPayloadBytes;
    }

    private static RuntimeTypeModel CreateEnvelopeModel()
    {
        var model = RuntimeTypeModel.Create();
        model.MaxDepth = 8;
        model.Add(typeof(EnvelopeMessage), true);
        model.Add(typeof(EncryptedFieldMessage), true);
        model.CompileInPlace();
        model.Freeze();
        return model;
    }

    public byte[] Serialize(IObjectEnvelope<TContract> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var instance = envelope.ObjectInstance ?? throw new InvalidOperationException("The envelope has no entity.");
        if (!_ids.TryGetValue(instance.GetType(), out var id)) throw new InvalidOperationException("The runtime type is not enrolled for this serializer.");
        using var payload = new BoundedBuffer(_maximumBytes);
        _model.Serialize(payload, instance);
        var message = new EnvelopeMessage
        {
            FormatVersion = 1, Key = envelope.EnvelopeCacheKey, ContractVersion = envelope.VersionTag,
            Revision = envelope.RevisionTimestampUtcMicroseconds, TypeId = id,
            CacheObject = envelope.CacheObjectInstance, Payload = payload.ToArray(),
            HasExtensionMetadata = envelope.ExtensionMetadata is not null,
            ExtensionMetadata = envelope.ExtensionMetadata ?? []
        };
        foreach (var (name, field) in envelope.EncryptedFields.OrderBy(x => x.Key, StringComparer.Ordinal))
            message.EncryptedFields.Add(new() { Name = name, ProfileName = field.ProfileName,
                PropertyTypeName = field.PropertyTypeName, Ciphertext = field.Ciphertext });
        Validate(message);
        using var output = new BoundedBuffer(_maximumBytes);
        EnvelopeModel.Serialize(output, message);
        envelope.ImplementationTypeName = instance.GetType().AssemblyQualifiedName;
        return output.ToArray();
    }

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var message = ReadMessage(data);
        var type = _types[message.TypeId];
        TContract instance;
        try
        {
            instance = _model.Deserialize(type, message.Payload.AsMemory()) as TContract
                ?? throw new InvalidDataException("The Protobuf entity has an invalid type.");
        }
        catch (ProtoException error) { throw new InvalidDataException("Malformed Protobuf entity.", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid Protobuf entity data.", error); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Protobuf entity.", error); }
        if (instance.GetType() != type) throw new InvalidDataException("The Protobuf entity type differs from its registered ID.");
        return new FileObjectEnvelope<TContract>
        {
            EnvelopeCacheKey = message.Key, VersionTag = message.ContractVersion,
            RevisionTimestampUtcMicroseconds = message.Revision, ObjectInstance = instance,
            ImplementationTypeName = type.AssemblyQualifiedName, CacheObjectInstance = message.CacheObject,
            ExtensionMetadata = message.HasExtensionMetadata ? message.ExtensionMetadata : null,
            EncryptedFields = message.EncryptedFields.ToDictionary(x => x.Name, x => new EncryptedFieldValue
            { ProfileName = x.ProfileName, PropertyTypeName = x.PropertyTypeName, Ciphertext = x.Ciphertext }, StringComparer.Ordinal)
        };
    }

    private EnvelopeMessage ReadMessage(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty || data.Length > _maximumBytes) throw new InvalidDataException("Invalid Protobuf envelope size.");
        try
        {
            var message = (EnvelopeMessage)EnvelopeModel.Deserialize(typeof(EnvelopeMessage), data);
            Validate(message);
            return message;
        }
        catch (ProtoException error) { throw new InvalidDataException("Malformed Protobuf envelope.", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid Protobuf envelope data.", error); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated Protobuf envelope.", error); }
    }

    private void Validate(EnvelopeMessage message)
    {
        if (message.FormatVersion != 1 || message.Revision < 0 || string.IsNullOrEmpty(message.Key)
            || string.IsNullOrEmpty(message.ContractVersion) || !_types.ContainsKey(message.TypeId))
            throw new InvalidDataException("Unsupported Protobuf envelope version, identity, revision or type ID.");
        if (message.ExtensionMetadata.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength
            || (!message.HasExtensionMetadata && message.ExtensionMetadata.Length != 0))
            throw new InvalidDataException("Invalid envelope extension metadata.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in message.EncryptedFields)
            if (string.IsNullOrEmpty(field.Name) || !names.Add(field.Name))
                throw new InvalidDataException("Encrypted field names must be nonempty and unique.");
    }

    public long ReadRevision(ReadOnlyMemory<byte> payload) => ReadMessage(payload).Revision;

    public async ValueTask SerializeAsync(IObjectEnvelope<TContract> envelope, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Serialize(envelope);
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new BoundedBuffer(_maximumBytes);
        var chunk = new byte[Math.Min(_maximumBytes, 8192)];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    public async ValueTask<IObjectEnvelope<TContract>> DeserializeAsync(Stream source, CancellationToken cancellationToken = default)
        => Deserialize(await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false));

    public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
        => ReadRevision(await ReadBoundedAsync(source, cancellationToken).ConfigureAwait(false));
}
