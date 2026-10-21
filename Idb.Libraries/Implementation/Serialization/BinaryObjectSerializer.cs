using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using MessagePack.Resolvers;
using MessagePack.Formatters;
using System.Buffers;
using System.Reflection;
using System.Text;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Binary serializer (MessagePack-backed) that produces compact payloads compatible across contract versions.
/// </summary>
public sealed class BinaryObjectSerializer<TContract> : IRevisionObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private const string DefaultExtension = ".mpk";
    private const string CurrentMagic = "IDB6";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, TypeSerializationInfo> TypeSerializationCache = new();
    private readonly MessagePackSerializerOptions _options;
    private readonly MessagePackSerializerOptions _fastOptions;
    private readonly MessagePackSerializerOptions _extensionMetadataOptions;
    private readonly int _maximumPayloadBytes;
    private readonly bool _allowLegacyTypelessPayloads;
    private readonly StoredRuntimeTypeAllowlist<TContract> _runtimeTypeAllowlist;
    private CachedTypeName? _cachedReadTypeName;

    // One bounded, owned UTF-8 header per serializer. Never retain a slice of an entity payload.
    private sealed record CachedTypeName(byte[] Utf8, string Value);
    private const int MaximumCachedTypeNameBytes = 1024;

    internal int MaximumPayloadBytes => _maximumPayloadBytes;

    private delegate void SerializeTypedDelegate(
        Stream destination,
        object instance,
        MessagePackSerializerOptions options,
        CancellationToken cancellationToken);

    private delegate Task SerializeTypedAsyncDelegate(
        Stream destination,
        object instance,
        MessagePackSerializerOptions options,
        CancellationToken cancellationToken);

    private sealed record TypeSerializationInfo(
        string TypeName,
        SerializeTypedDelegate Serialize,
        SerializeTypedAsyncDelegate SerializeAsync,
        SerializeTypedBufferDelegate SerializeBuffer);

    private delegate void SerializeTypedBufferDelegate(
        ref MessagePackWriter writer, object instance, MessagePackSerializerOptions options);

    public BinaryObjectSerializer(
        MessagePackSerializerOptions? options = null,
        int maximumPayloadBytes = 256 * 1024 * 1024,
        bool allowLegacyTypelessPayloads = false,
        StoredRuntimeTypeAllowlist<TContract>? runtimeTypeAllowlist = null)
    {
        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        }

        _maximumPayloadBytes = maximumPayloadBytes;
        _allowLegacyTypelessPayloads = allowLegacyTypelessPayloads;
        _runtimeTypeAllowlist = runtimeTypeAllowlist ?? StoredRuntimeTypeAllowlist<TContract>.Default;
        _options = (options ?? MessagePackSerializerOptions.Standard)
            .WithSecurity(MessagePackSecurity.UntrustedData);
        // Use a faster resolver for typed serialization of the object instance
        // Composite resolver: treat any IObjectEnvelope<,> property as nil (ignored),
        // and serialize the rest with the fast contractless resolver.
        _fastOptions = MessagePackSerializerOptions.Standard.WithResolver(
                CompositeResolver.Create(EnvelopeNullResolver.Instance, ContractlessStandardResolver.Instance))
            .WithSecurity(MessagePackSecurity.UntrustedData);
        _extensionMetadataOptions = _fastOptions.WithResolver(
            CompositeResolver.Create(EnvelopeExtensionMetadataResolver.Instance, _fastOptions.Resolver));
    }

    //protected MessagePackSerializerOptions SerializerOptions { get; }
    //private static MessagePackSerializerOptions ResolveSerializerOptions(IObjectSerializer<TContract, string> serializer)
    //{
    //    if (serializer is BinaryObjectSerializer<TContract, string> binarySerializer)
    //    {
    //        var field = typeof(BinaryObjectSerializer<TContract, string>)
    //            .GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    //        if (field?.GetValue(binarySerializer) is MessagePackSerializerOptions options)
    //        {
    //            return options;
    //        }
    //    }

    //    return MessagePackSerializerOptions.Standard;
    //}

    public byte[] Serialize(IObjectEnvelope<TContract> obj)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        ValidateExtensionMetadataForWrite(obj.ExtensionMetadata);

        // Write magic + simple header (version, cacheKey, typeName, cache flag, encrypted fields,
        // extension metadata) + typed object payload.
        using var buffer = new PooledByteBufferWriter();
        "IDB6"u8.CopyTo(buffer.GetSpan(4));
        buffer.Advance(4);
        ArgumentOutOfRangeException.ThrowIfNegative(obj.RevisionTimestampUtcMicroseconds);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.GetSpan(8), obj.RevisionTimestampUtcMicroseconds);
        buffer.Advance(8);
        var writer = new MessagePackWriter(buffer);
        var version = obj.VersionTag ?? string.Empty;
        var cacheKey = obj.EnvelopeCacheKey ?? string.Empty;
        var instance = obj.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var runtimeType = instance.GetType();
        ValidateRuntimeTypeForWrite(runtimeType);
        var typeInfo = TypeSerializationCache.GetOrAdd(runtimeType, CreateTypeSerializationInfo);
        obj.ImplementationTypeName = typeInfo.TypeName;

        writer.Write(version);
        writer.Write(cacheKey);
        writer.Write(typeInfo.TypeName);
        writer.Write(obj.CacheObjectInstance);
        MessagePackSerializer.Serialize(ref writer, obj.EncryptedFields, _fastOptions);
        MessagePackSerializer.Serialize(ref writer, obj.ExtensionMetadata, _extensionMetadataOptions);
        typeInfo.SerializeBuffer(ref writer, instance, _fastOptions);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return DeserializeCore(data);
    }

    /// <summary>
    /// Deserializes borrowed input by first obtaining owned backing storage. MessagePack attribute
    /// formatters can retain input slices, so this serializer does not opt into pooled file reads.
    /// </summary>
    public IObjectEnvelope<TContract> DeserializeMemory(ReadOnlyMemory<byte> data)
        => DeserializeCore(data.ToArray());

    private IObjectEnvelope<TContract> DeserializeCore(ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            throw new ArgumentException("Serialized payload cannot be empty.", nameof(data));
        }
        if (data.Length > _maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Stored envelope exceeds the configured {_maximumPayloadBytes}-byte limit.");
        }

        // Detect new format by magic prefix; otherwise fall back for backward compatibility
        if (HasMagic(data.Span))
        {
            var format = data.Span[3]; // HasMagic validated the supported IDB2-IDB6 range.
            var revision = format == (byte)'6' ? ReadRevision(data) : 0;
            var reader = new MessagePackReader(data[(format == (byte)'6' ? 12 : 4)..]);
            var version = reader.ReadString();
            var cacheKey = reader.ReadString();
            var typeName = ReadTypeName(ref reader);
            var cacheObjectInstance = format >= (byte)'3'
                && reader.ReadBoolean();
            var encryptedFields = format >= (byte)'4'
                ? MessagePackSerializer.Deserialize<Dictionary<string, EncryptedFieldValue>>(ref reader, _fastOptions)
                : new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
            var extensionMetadata = format >= (byte)'5'
                ? MessagePackSerializer.Deserialize<byte[]?>(ref reader, _extensionMetadataOptions)
                : null;
            var type = ResolveAllowedRuntimeType(typeName);
            var instance = MessagePackSerializer.Deserialize(type, ref reader, _fastOptions);
            if (instance is not TContract contractInstance)
            {
                throw new InvalidOperationException($"Serialized payload type '{type.FullName}' is not assignable to '{typeof(TContract).FullName}'.");
            }

            var envelope = new PlainEnvelope
            {
                VersionTag = version ?? string.Empty,
                RevisionTimestampUtcMicroseconds = revision,
                EnvelopeCacheKey = cacheKey ?? string.Empty,
                ImplementationTypeName = typeName,
                CacheObjectInstance = cacheObjectInstance,
                EncryptedFields = encryptedFields,
                ExtensionMetadata = extensionMetadata,
                ObjectInstance = contractInstance,
                Location = default!
            };
            return envelope;
        }

        if (!_allowLegacyTypelessPayloads)
        {
            throw new InvalidDataException(
                "Legacy typeless envelope payloads are disabled. Convert the payload with the trusted migration tooling before opening it.");
        }

        // Custom legacy formatters may retain memory; isolate the borrowed input in that path.
        var legacyEnvelope = MessagePackSerializer.Deserialize<IObjectEnvelope<TContract>>(data.ToArray(), _options);
        ValidateExtensionMetadataFromStorage(legacyEnvelope.ExtensionMetadata);
        return legacyEnvelope;
    }

    private string? ReadTypeName(ref MessagePackReader reader)
    {
        var probe = reader;
        if (!probe.TryReadStringSpan(out var utf8))
        {
            return reader.ReadString();
        }

        var cached = Volatile.Read(ref _cachedReadTypeName);
        if (cached is not null && utf8.SequenceEqual(cached.Utf8))
        {
            reader = probe;
            return cached.Value;
        }

        // Preserve MessagePack's normal decoding and validation on misses. Publish only owned
        // immutable data. Keep the first hint rather than allocating replacements when a
        // polymorphic stream alternates types; other names follow the normal decoding path.
        var value = reader.ReadString();
        if (cached is null && value is not null && utf8.Length <= MaximumCachedTypeNameBytes)
        {
            Interlocked.CompareExchange(ref _cachedReadTypeName,
                new CachedTypeName(utf8.ToArray(), value), null);
        }
        return value;
    }

    public ValueTask SerializeAsync(
        IObjectEnvelope<TContract> envalope,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (envalope is null)
        {
            throw new ArgumentNullException(nameof(envalope));
        }

        return new ValueTask(SerializeCoreAsync(envalope, destination, cancellationToken));
    }

    public ValueTask<IObjectEnvelope<TContract>> DeserializeAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return DeserializeCoreAsync(source, cancellationToken);
    }

    private static void WriteMagic(Stream stream)
    {
        var magic = Encoding.ASCII.GetBytes(CurrentMagic);
        stream.Write(magic, 0, magic.Length);
    }

    private static bool HasMagic(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4
            && data[0] == (byte)'I'
            && data[1] == (byte)'D'
            && data[2] == (byte)'B'
            && data[3] is (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5' or (byte)'6';
    }

    private sealed class EnvelopeNullResolver : IFormatterResolver
    {
        public static readonly IFormatterResolver Instance = new EnvelopeNullResolver();
        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            var t = typeof(T);
            // We want to ignore envelope properties when serializing a contract instance.
            // The property type is `IObjectEnvelope<TContract>`; MessagePack asks the resolver for a formatter
            // by the declared field type (even when the runtime value is null). Two cases to handle:
            //  - Exact closed generic match: IObjectEnvelope<TContract>
            //  - Any IObjectEnvelope<> (defensive, should still be safe to treat as nil)
            if (t == typeof(IObjectEnvelope<TContract>))
            {
                return new EnvelopeNullFormatter<T>();
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IObjectEnvelope<>))
            {
                return new EnvelopeNullFormatter<T>();
            }

            return null;
        }
    }

    private sealed class EnvelopeNullFormatter<T> : IMessagePackFormatter<T>
    {
        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            writer.WriteNil();
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (!reader.TryReadNil())
            {
                // Skip if not nil
                reader.Skip();
            }
            return default!;
        }
    }

    private sealed class EnvelopeExtensionMetadataResolver : IFormatterResolver
    {
        internal static readonly IFormatterResolver Instance = new EnvelopeExtensionMetadataResolver();

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            if (typeof(T) == typeof(byte[]))
            {
                return (IMessagePackFormatter<T>)(object)EnvelopeExtensionMetadataFormatter.Instance;
            }

            return null;
        }
    }

    private sealed class EnvelopeExtensionMetadataFormatter : IMessagePackFormatter<byte[]?>
    {
        internal static readonly EnvelopeExtensionMetadataFormatter Instance = new();

        public void Serialize(
            ref MessagePackWriter writer,
            byte[]? value,
            MessagePackSerializerOptions options)
        {
            ValidateExtensionMetadataForWrite(value);
            writer.Write(value);
        }

        public byte[]? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options)
        {
            var bytes = reader.ReadBytes();
            if (bytes is null)
            {
                return null;
            }

            if (bytes.Value.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength)
            {
                throw new MessagePackSerializationException(
                    $"Envelope extension metadata cannot exceed {EnvelopeExtensionMetadataLimits.MaximumByteLength} bytes.");
            }

            return bytes.Value.ToArray();
        }
    }

    private static string ReadMagic(Stream stream)
    {
        var buffer = new byte[4];
        var read = stream.Read(buffer, 0, 4);
        if (read != 4)
        {
            throw new InvalidOperationException("Invalid envelope header.");
        }

        return Encoding.ASCII.GetString(buffer);
    }

    private static bool IsCurrentMagic(string magic)
        => string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static bool HasCacheFlag(string magic)
        => string.Equals(magic, "IDB3", StringComparison.Ordinal)
            || string.Equals(magic, "IDB4", StringComparison.Ordinal)
            || string.Equals(magic, "IDB5", StringComparison.Ordinal)
            || string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static bool HasEncryptedFields(string magic)
        => string.Equals(magic, "IDB4", StringComparison.Ordinal)
            || string.Equals(magic, "IDB5", StringComparison.Ordinal)
            || string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static bool HasExtensionMetadata(string magic)
        => magic is "IDB5" or CurrentMagic;

    private Type ResolveAllowedRuntimeType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)
            || !_runtimeTypeAllowlist.TryResolve(typeName, out var runtimeType))
        {
            throw new InvalidDataException(
                $"Serialized payload type '{typeName}' is not enrolled for contract '{typeof(TContract).FullName}'.");
        }

        return runtimeType;
    }

    private static TypeSerializationInfo CreateTypeSerializationInfo(Type runtimeType)
    {
        var typeName = runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name;
        var serialize = (SerializeTypedDelegate)Delegate.CreateDelegate(
            typeof(SerializeTypedDelegate),
            typeof(BinaryObjectSerializer<TContract>)
                .GetMethod(nameof(SerializeTyped), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(runtimeType));
        var serializeAsync = (SerializeTypedAsyncDelegate)Delegate.CreateDelegate(
            typeof(SerializeTypedAsyncDelegate),
            typeof(BinaryObjectSerializer<TContract>)
                .GetMethod(nameof(SerializeTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(runtimeType));

        var serializeBuffer = (SerializeTypedBufferDelegate)Delegate.CreateDelegate(
            typeof(SerializeTypedBufferDelegate),
            typeof(BinaryObjectSerializer<TContract>)
                .GetMethod(nameof(SerializeTypedBuffer), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(runtimeType));
        return new TypeSerializationInfo(typeName, serialize, serializeAsync, serializeBuffer);
    }

    private static void SerializeTypedBuffer<TRuntime>(
        ref MessagePackWriter writer, object instance, MessagePackSerializerOptions options)
        => MessagePackSerializer.Serialize(ref writer, (TRuntime)instance, options);

    private static void SerializeTyped<TRuntime>(
        Stream destination,
        object instance,
        MessagePackSerializerOptions options,
        CancellationToken cancellationToken)
    {
        MessagePackSerializer.Serialize(destination, (TRuntime)instance, options, cancellationToken);
    }

    private static Task SerializeTypedAsync<TRuntime>(
        Stream destination,
        object instance,
        MessagePackSerializerOptions options,
        CancellationToken cancellationToken)
        => MessagePackSerializer.SerializeAsync(destination, (TRuntime)instance, options, cancellationToken);

    private async Task SerializeCoreAsync(
        IObjectEnvelope<TContract> envelope,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ValidateExtensionMetadataForWrite(envelope.ExtensionMetadata);
        var instance = envelope.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var runtimeType = instance.GetType();
        ValidateRuntimeTypeForWrite(runtimeType);
        var typeInfo = TypeSerializationCache.GetOrAdd(runtimeType, CreateTypeSerializationInfo);

        WriteMagic(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(envelope.RevisionTimestampUtcMicroseconds);
        var revisionBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(revisionBytes, envelope.RevisionTimestampUtcMicroseconds);
        await destination.WriteAsync(revisionBytes, cancellationToken).ConfigureAwait(false);
        var version = envelope.VersionTag ?? string.Empty;
        var cacheKey = envelope.EnvelopeCacheKey ?? string.Empty;
        envelope.ImplementationTypeName = typeInfo.TypeName;

        await MessagePackSerializer.SerializeAsync(destination, version, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, cacheKey, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, typeInfo.TypeName, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, envelope.CacheObjectInstance, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, envelope.EncryptedFields, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, envelope.ExtensionMetadata, _extensionMetadataOptions, cancellationToken).ConfigureAwait(false);
        await typeInfo.SerializeAsync(destination, instance, _fastOptions, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IObjectEnvelope<TContract>> DeserializeCoreAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        // Admit the complete stored payload before parsing. This gives seekable and non-seekable
        // streams the same bounded allocation and detects a source that grows after reporting its
        // length before MessagePack can materialize any values.
        var payload = await BoundedStoredPayload
            .ReadStreamAsync(source, _maximumPayloadBytes, cancellationToken)
            .ConfigureAwait(false);
        return DeserializeCore(payload);
    }

    private static void ValidateExtensionMetadataForWrite(byte[]? extensionMetadata)
    {
        if (extensionMetadata?.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength)
        {
            throw new InvalidOperationException(
                $"Envelope extension metadata cannot exceed {EnvelopeExtensionMetadataLimits.MaximumByteLength} bytes.");
        }
    }

    private void ValidateRuntimeTypeForWrite(Type type)
    {
        if (!_runtimeTypeAllowlist.Contains(type))
        {
            throw new InvalidOperationException(
                $"Runtime type '{type.FullName}' is not enrolled for stored contract '{typeof(TContract).FullName}'.");
        }
    }

    private static void ValidateExtensionMetadataFromStorage(byte[]? extensionMetadata)
    {
        if (extensionMetadata?.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength)
        {
            throw new InvalidDataException(
                $"Stored envelope extension metadata exceeds the {EnvelopeExtensionMetadataLimits.MaximumByteLength}-byte limit.");
        }
    }

    private sealed class PlainEnvelope : IObjectEnvelope<TContract>, IEnvelopeCacheState<TContract>
    {
        private long _commitOrder;
        long IEnvelopeRuntimeState.CommitOrder { get => Volatile.Read(ref _commitOrder); set => Volatile.Write(ref _commitOrder, value); }
        ICommittedEnvelopeSnapshot<TContract>? IEnvelopeCacheState<TContract>.CommittedSnapshot { get; set; }
        TContract? IEnvelopeCacheState<TContract>.CachedOwner { get; set; }
        public long RevisionTimestampUtcMicroseconds { get; set; }
        public string EnvelopeCacheKey { get; set; } = string.Empty;
        public string VersionTag { get; set; } = string.Empty;
        public TContract? ObjectInstance { get; set; }
        public WeakReference<TContract>? ObjectInstanceWeakReference { get; set; }
        public string? ImplementationTypeName { get; set; }
        public bool CacheObjectInstance { get; set; }
        public required IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }
        public byte[]? ExtensionMetadata { get; set; }
        public string? Location { get; set; }
        public Idb.Libraries.Abstractions.Enums.StorageObjectState State { get; set; }
    }

    public long ReadRevision(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 4) throw new InvalidDataException("Truncated envelope header.");
        if (!payload.Span[..4].SequenceEqual("IDB6"u8))
        {
            if (payload.Span[..3].SequenceEqual("IDB"u8) && !HasMagic(payload.Span))
                throw new InvalidDataException("Unsupported envelope format.");
            return 0;
        }
        if (payload.Length < 12) throw new InvalidDataException("Truncated revision header.");
        var revision = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload.Span[4..]);
        if (revision < 0) throw new InvalidDataException("Invalid negative revision.");
        return revision;
    }

    public async ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var header = new byte[12];
        await source.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (header.AsSpan(0, 4).SequenceEqual("IDB6"u8))
            await source.ReadExactlyAsync(header.AsMemory(4), cancellationToken).ConfigureAwait(false);
        return ReadRevision(header);
    }
}
