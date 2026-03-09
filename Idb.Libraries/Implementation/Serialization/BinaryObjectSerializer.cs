using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using MessagePack.Resolvers;
using MessagePack.Formatters;
using System.Text;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>
/// Binary serializer (MessagePack-backed) that produces compact payloads compatible across contract versions.
/// </summary>
public sealed class BinaryObjectSerializer<TContract> : IObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private const string DefaultExtension = ".mpk";
    private readonly MessagePackSerializerOptions _options;
    private readonly MessagePackSerializerOptions _fastOptions;

    public BinaryObjectSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard;
        // Use a faster resolver for typed serialization of the object instance
        // Composite resolver: treat any IObjectEnvelope<,> property as nil (ignored),
        // and serialize the rest with the fast contractless resolver.
        _fastOptions = MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(EnvelopeNullResolver.Instance, ContractlessStandardResolver.Instance));
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

        // Write magic + simple header (version, cacheKey, typeName) + typed object payload
        using var ms = new MemoryStream();
        WriteMagic(ms);
        var version = obj.VersionTag ?? string.Empty;
        var cacheKey = obj.EnvelopeCacheKey ?? string.Empty;
        var instance = obj.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var type = instance.GetType();
        var typeName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

        MessagePackSerializer.Serialize(ms, version, _fastOptions);
        MessagePackSerializer.Serialize(ms, cacheKey, _fastOptions);
        MessagePackSerializer.Serialize(ms, typeName, _fastOptions);
        MessagePackSerializer.Serialize(type, ms, instance, _fastOptions);
        return ms.ToArray();
    }

    public IObjectEnvelope<TContract> Deserialize(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (data.Length == 0)
        {
            throw new ArgumentException("Serialized payload cannot be empty.", nameof(data));
        }

        // Detect new format by magic prefix; otherwise fall back for backward compatibility
        if (HasMagic(data))
        {
            var ms = new MemoryStream(data, writable: false);
            SkipMagic(ms);
            var version = MessagePackSerializer.Deserialize<string>(ms, _fastOptions);
            var cacheKey = MessagePackSerializer.Deserialize<string>(ms, _fastOptions);
            var typeName = MessagePackSerializer.Deserialize<string>(ms, _fastOptions);
            var type = ResolveType(typeName);
            var instance = MessagePackSerializer.Deserialize(type!, ms, _fastOptions);

            var envelope = new PlainEnvelope
            {
                VersionTag = version,
                EnvelopeCacheKey = cacheKey,
                ObjectInstance = (TContract)instance,
                Location = default!
            };
            return envelope;
        }

        return MessagePackSerializer.Deserialize<IObjectEnvelope<TContract>>(data, _options);
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
        var magic = Encoding.ASCII.GetBytes("IDB2");
        stream.Write(magic, 0, magic.Length);
    }

    private static bool HasMagic(byte[] data)
    {
        return data.Length >= 4 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'B' && data[3] == (byte)'2';
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

    private static void SkipMagic(Stream stream)
    {
        var buffer = new byte[4];
        var read = stream.Read(buffer, 0, 4);
        if (read != 4)
        {
            throw new InvalidOperationException("Invalid envelope header.");
        }
    }

    private static Type? ResolveType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        return Type.GetType(typeName, throwOnError: false);
    }

    private async Task SerializeCoreAsync(
        IObjectEnvelope<TContract> envelope,
        Stream destination,
        CancellationToken cancellationToken)
    {
        WriteMagic(destination);
        var version = envelope.VersionTag ?? string.Empty;
        var cacheKey = envelope.EnvelopeCacheKey ?? string.Empty;
        var instance = envelope.ObjectInstance ?? throw new InvalidOperationException("Envelope does not contain an object instance.");
        var type = instance.GetType();
        var typeName = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

        await MessagePackSerializer.SerializeAsync(destination, version, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, cacheKey, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(destination, typeName, _fastOptions, cancellationToken).ConfigureAwait(false);
        await MessagePackSerializer.SerializeAsync(type, destination, instance, _fastOptions, cancellationToken).ConfigureAwait(false);
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

        if (prefix[0] == (byte)'I' && prefix[1] == (byte)'D' && prefix[2] == (byte)'B' && prefix[3] == (byte)'2')
        {
            // New format
            var version = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var cacheKey = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var typeName = await MessagePackSerializer.DeserializeAsync<string>(source, _fastOptions, cancellationToken).ConfigureAwait(false);
            var type = ResolveType(typeName);
            var instanceObj = await MessagePackSerializer.DeserializeAsync(type!, source, _fastOptions, cancellationToken).ConfigureAwait(false);

            var env = new PlainEnvelope
            {
                VersionTag = version,
                EnvelopeCacheKey = cacheKey,
                ObjectInstance = (TContract)instanceObj,
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
                return await MessagePackSerializer.DeserializeAsync<IObjectEnvelope<TContract>>(ms, _options, cancellationToken).ConfigureAwait(false);
            }

            return await MessagePackSerializer.DeserializeAsync<IObjectEnvelope<TContract>>(source, _options, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PlainEnvelope : IObjectEnvelope<TContract>
    {
        public string EnvelopeCacheKey { get; set; } = string.Empty;
        public string VersionTag { get; set; } = string.Empty;
        public TContract? ObjectInstance { get; set; }
        public string? Location { get; set; }
    }
}
