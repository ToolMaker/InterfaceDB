using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using MessagePack.Resolvers;
using MessagePack.Formatters;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Binary serializer (MessagePack-backed) that produces compact payloads compatible across contract versions.
/// </summary>
public sealed class BinaryObjectSerializer<TContract> : IObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private const string DefaultExtension = ".mpk";
    private const string CurrentMagic = "IDB5";
    private static readonly ConcurrentDictionary<Type, TypeSerializationInfo> TypeSerializationCache = new();
    private static readonly ConcurrentDictionary<string, Type?> TypeResolutionCache = new(StringComparer.Ordinal);
    private readonly MessagePackSerializerOptions _options;
    private readonly MessagePackSerializerOptions _fastOptions;
    private readonly MessagePackSerializerOptions _extensionMetadataOptions;

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

    public BinaryObjectSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard;
        // Use a faster resolver for typed serialization of the object instance
        // Composite resolver: treat any IObjectEnvelope<,> property as nil (ignored),
        // and serialize the rest with the fast contractless resolver.
        _fastOptions = MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(EnvelopeNullResolver.Instance, ContractlessStandardResolver.Instance));
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
        "IDB5"u8.CopyTo(buffer.GetSpan(4));
        buffer.Advance(4);
        var writer = new MessagePackWriter(buffer);
        var version = obj.VersionTag ?? string.Empty;
        var cacheKey = obj.EnvelopeCacheKey ?? string.Empty;
        var instance = obj.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var typeInfo = TypeSerializationCache.GetOrAdd(instance.GetType(), CreateTypeSerializationInfo);
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

        // Detect new format by magic prefix; otherwise fall back for backward compatibility
        if (HasMagic(data.Span))
        {
            var magic = Encoding.ASCII.GetString(data.Span[..4]);
            var reader = new MessagePackReader(data[4..]);
            var version = reader.ReadString();
            var cacheKey = reader.ReadString();
            var typeName = reader.ReadString();
            var cacheObjectInstance = HasCacheFlag(magic)
                && reader.ReadBoolean();
            var encryptedFields = HasEncryptedFields(magic)
                ? MessagePackSerializer.Deserialize<Dictionary<string, EncryptedFieldValue>>(ref reader, _fastOptions)
                : new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
            var extensionMetadata = HasExtensionMetadata(magic)
                ? MessagePackSerializer.Deserialize<byte[]?>(ref reader, _extensionMetadataOptions)
                : null;
            var type = ResolveType(typeName)
                ?? throw new InvalidOperationException($"Unable to resolve serialized contract type '{typeName}'.");
            var instance = MessagePackSerializer.Deserialize(type, ref reader, _fastOptions);
            if (instance is not TContract contractInstance)
            {
                throw new InvalidOperationException($"Serialized payload type '{type.FullName}' is not assignable to '{typeof(TContract).FullName}'.");
            }

            var envelope = new PlainEnvelope
            {
                VersionTag = version ?? string.Empty,
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

        // Custom legacy formatters may retain memory; isolate the borrowed input in that path.
        var legacyEnvelope = MessagePackSerializer.Deserialize<IObjectEnvelope<TContract>>(data.ToArray(), _options);
        ValidateExtensionMetadataFromStorage(legacyEnvelope.ExtensionMetadata);
        return legacyEnvelope;
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
            && data[3] is (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5';
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
            || string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static bool HasEncryptedFields(string magic)
        => string.Equals(magic, "IDB4", StringComparison.Ordinal)
            || string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static bool HasExtensionMetadata(string magic)
        => string.Equals(magic, CurrentMagic, StringComparison.Ordinal);

    private static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        return TypeResolutionCache.GetOrAdd(typeName, static key => Type.GetType(key, throwOnError: false));
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
        WriteMagic(destination);
        var version = envelope.VersionTag ?? string.Empty;
        var cacheKey = envelope.EnvelopeCacheKey ?? string.Empty;
        var instance = envelope.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var typeInfo = TypeSerializationCache.GetOrAdd(instance.GetType(), CreateTypeSerializationInfo);
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
        // Peek first 4 bytes
        var prefix = new byte[4];
        var read = await source.ReadAsync(prefix.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (read != 4)
        {
            throw new InvalidOperationException("Unexpected end of stream.");
        }

        if (prefix[0] == (byte)'I'
            && prefix[1] == (byte)'D'
            && prefix[2] == (byte)'B'
            && prefix[3] is (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5')
        {
            // New format
            var magic = Encoding.ASCII.GetString(prefix);
            var version = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var cacheKey = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var typeName = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var cacheObjectInstance = HasCacheFlag(magic)
                && await MessagePackSerializer.DeserializeAsync<bool>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var encryptedFields = HasEncryptedFields(magic)
                ? await MessagePackSerializer.DeserializeAsync<Dictionary<string, EncryptedFieldValue>>(source, _fastOptions, cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
            var extensionMetadata = HasExtensionMetadata(magic)
                ? await MessagePackSerializer.DeserializeAsync<byte[]?>(source, _extensionMetadataOptions, cancellationToken).ConfigureAwait(false)
                : null;
            var type = ResolveType(typeName)
                ?? throw new InvalidOperationException($"Unable to resolve serialized contract type '{typeName}'.");
            var instanceObj = await MessagePackSerializer.DeserializeAsync(type, source, _fastOptions, cancellationToken).ConfigureAwait(false);
            if (instanceObj is not TContract contractInstance)
            {
                throw new InvalidOperationException($"Serialized payload type '{type.FullName}' is not assignable to '{typeof(TContract).FullName}'.");
            }

            var env = new PlainEnvelope
            {
                VersionTag = version,
                EnvelopeCacheKey = cacheKey,
                ImplementationTypeName = typeName,
                CacheObjectInstance = cacheObjectInstance,
                EncryptedFields = encryptedFields,
                ExtensionMetadata = extensionMetadata,
                ObjectInstance = contractInstance,
                Location = default!
            };
            return env;
        }
        else
        {
            // Old format: rewind and use legacy typeless
            if (source.CanSeek)
            {
                source.Seek(-4, SeekOrigin.Current);
            }
            else
            {
                // Reconstruct prefixed stream
                var ms = new MemoryStream();
                await ms.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                await source.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                var bufferedLegacyEnvelope = await MessagePackSerializer
                    .DeserializeAsync<IObjectEnvelope<TContract>>(ms, _options, cancellationToken)
                    .ConfigureAwait(false);
                ValidateExtensionMetadataFromStorage(bufferedLegacyEnvelope.ExtensionMetadata);
                return bufferedLegacyEnvelope;
            }

            var legacyEnvelope = await MessagePackSerializer
                .DeserializeAsync<IObjectEnvelope<TContract>>(source, _options, cancellationToken)
                .ConfigureAwait(false);
            ValidateExtensionMetadataFromStorage(legacyEnvelope.ExtensionMetadata);
            return legacyEnvelope;
        }
    }

    private static void ValidateExtensionMetadataForWrite(byte[]? extensionMetadata)
    {
        if (extensionMetadata?.Length > EnvelopeExtensionMetadataLimits.MaximumByteLength)
        {
            throw new InvalidOperationException(
                $"Envelope extension metadata cannot exceed {EnvelopeExtensionMetadataLimits.MaximumByteLength} bytes.");
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

    private sealed class PlainEnvelope : IObjectEnvelope<TContract>
    {
        public string EnvelopeCacheKey { get; set; } = string.Empty;
        public string VersionTag { get; set; } = string.Empty;
        public TContract? ObjectInstance { get; set; }
        public WeakReference<TContract>? ObjectInstanceWeakReference { get; set; }
        public string? ImplementationTypeName { get; set; }
        public bool CacheObjectInstance { get; set; }
        public IDictionary<string, EncryptedFieldValue> EncryptedFields { get; set; }
            = new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
        public byte[]? ExtensionMetadata { get; set; }
        public string? Location { get; set; }
        public Idb.Libraries.Abstractions.Enums.StorageObjectState State { get; set; }
    }
}
