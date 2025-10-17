using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Services.Serializers;

public sealed class MessagePackObjectChangeDetector : IObjectChangeDetector
{
    private delegate void MessagePackSerializerDelegate(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options);

    private readonly MessagePackSerializerOptions _options;
    private static readonly ConcurrentDictionary<Type, ContractDescriptor> DescriptorCache = new();

    public MessagePackObjectChangeDetector()
        : this(MessagePackConfiguration.Options)
    {
    }

    public MessagePackObjectChangeDetector(MessagePackSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ObjectFingerprint ComputeFingerprint<T>(T value)
        => ComputeFingerprint(value, typeof(T));

    public ObjectFingerprint ComputeFingerprint(object? value, Type valueType)
    {
        if (valueType is null)
        {
            throw new ArgumentNullException(nameof(valueType));
        }

        var descriptor = GetDescriptor(valueType);
        var payloadHash = HashPayload(value, descriptor);
        return new ObjectFingerprint(descriptor.SchemaHash, payloadHash);
    }

    public bool HasChanged(ObjectFingerprint previous, ObjectFingerprint current)
        => previous.SchemaHash != current.SchemaHash || previous.PayloadHash != current.PayloadHash;

    private ulong HashPayload(object? value, ContractDescriptor descriptor)
    {
        using var writer = new HashingBufferWriter();
        var mpWriter = new MessagePackWriter(writer);

        if (!descriptor.HasProperties)
        {
            if (value is null)
            {
                mpWriter.WriteNil();
            }
            else
            {
                descriptor.TypeSerializer(ref mpWriter, value, _options);
            }

            mpWriter.Flush();
            return writer.Hash;
        }

        if (value is null)
        {
            mpWriter.WriteNil();
            mpWriter.Flush();
            return writer.Hash;
        }

        mpWriter.WriteMapHeader(descriptor.Properties.Length);

        foreach (var property in descriptor.Properties)
        {
            property.WriteKey(ref mpWriter);
            var propertyValue = property.Getter(value);
            property.Serializer(ref mpWriter, propertyValue, _options);
        }

        mpWriter.Flush();
        return writer.Hash;
    }

    private ContractDescriptor GetDescriptor(Type type)
        => DescriptorCache.GetOrAdd(type, static (t, options) => ContractDescriptor.Create(t, options), _options);

    private sealed class ContractDescriptor
    {
        private ContractDescriptor(ulong schemaHash, PropertyDescriptor[] properties, MessagePackSerializerDelegate typeSerializer)
        {
            SchemaHash = schemaHash;
            Properties = properties;
            TypeSerializer = typeSerializer;
        }

        public ulong SchemaHash { get; }
        public PropertyDescriptor[] Properties { get; }
        public MessagePackSerializerDelegate TypeSerializer { get; }
        public bool HasProperties => Properties.Length > 0;

        public static ContractDescriptor Create(Type type, MessagePackSerializerOptions options)
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                 .Where(static p => p.CanRead)
                                 .Where(static p => p.GetIndexParameters().Length == 0)
                                 .Where(static p => p.GetCustomAttribute<HashIgnoreAttribute>() is null)
                                 .Select(p => PropertyDescriptor.Create(p, options))
                                 .Where(static p => p is not null)
                                 .Select(static p => p!)
                                 .OrderBy(static p => p.Key, StringComparer.Ordinal)
                                 .ToArray();

            var typeSerializer = BuildSerializerDelegate(options.Resolver.GetFormatterDynamic(type), type);
            var schemaHash = ComputeSchemaHash(type, properties);
            return new ContractDescriptor(schemaHash, properties, typeSerializer);
        }

        private static ulong ComputeSchemaHash(Type type, PropertyDescriptor[] properties)
        {
            var hash = new XxHash64();
            AppendString(hash, type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

            var scopeAttribute = type.GetCustomAttribute<StorageScopeAttribute>();
            if (!string.IsNullOrWhiteSpace(scopeAttribute?.VersionTag))
            {
                AppendString(hash, scopeAttribute!.VersionTag!);
            }

            foreach (var property in properties)
            {
                AppendString(hash, property.Key);
                AppendString(hash, property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.FullName ?? property.PropertyType.Name);
            }

            return hash.GetCurrentHashAsUInt64();
        }

        private static void AppendString(XxHash64 hash, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Span<byte> marker = stackalloc byte[1];
                marker[0] = 0;
                hash.Append(marker);
                return;
            }

            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount <= 256)
            {
                Span<byte> buffer = stackalloc byte[byteCount];
                Encoding.UTF8.GetBytes(value, buffer);
                hash.Append(buffer);
            }
            else
            {
                var array = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    var span = array.AsSpan(0, byteCount);
                    Encoding.UTF8.GetBytes(value, span);
                    hash.Append(span);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(array);
                }
            }
        }
    }

    private sealed class PropertyDescriptor
    {
        private PropertyDescriptor(string key, Type propertyType, Func<object, object?> getter, MessagePackSerializerDelegate serializer)
        {
            Key = key;
            PropertyType = propertyType;
            Getter = getter;
            Serializer = serializer;
        }

        public string Key { get; }
        public Type PropertyType { get; }
        public Func<object, object?> Getter { get; }
        public MessagePackSerializerDelegate Serializer { get; }

        public void WriteKey(ref MessagePackWriter writer)
            => writer.Write(Key);

        public static PropertyDescriptor? Create(PropertyInfo property, MessagePackSerializerOptions options)
        {
            var getter = CreateGetter(property);
            if (getter is null)
            {
                return null;
            }

            var key = ResolveKey(property);
            var formatter = options.Resolver.GetFormatterDynamic(property.PropertyType);
            var serializer = BuildSerializerDelegate(formatter, property.PropertyType);
            return new PropertyDescriptor(key, property.PropertyType, getter, serializer);
        }

        private static Func<object, object?>? CreateGetter(PropertyInfo property)
        {
            var getMethod = property.GetGetMethod();
            if (getMethod is null)
            {
                return null;
            }

            var instance = Expression.Parameter(typeof(object), "instance");
            var typedInstance = Expression.Convert(instance, property.DeclaringType!);
            var access = Expression.Property(typedInstance, property);
            var convertResult = Expression.Convert(access, typeof(object));
            var lambda = Expression.Lambda<Func<object, object?>>(convertResult, instance);
            return lambda.Compile();
        }

        private static string ResolveKey(PropertyInfo property)
        {
            var name = property.Name;
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            if (name.Length == 1)
            {
                return name.ToLowerInvariant();
            }

            return char.ToLowerInvariant(name[0]) + name[1..];
        }
    }

    private static MessagePackSerializerDelegate BuildSerializerDelegate(object? formatter, Type type)
    {
        if (formatter is null)
        {
            return CreateFallbackSerializer(type);
        }

        var formatterType = formatter.GetType();
        var serializeMethod = formatterType.GetMethod(
            "Serialize",
            new[]
            {
                typeof(MessagePackWriter).MakeByRefType(),
                type,
                typeof(MessagePackSerializerOptions),
            });

        if (serializeMethod is null)
        {
            return CreateFallbackSerializer(type);
        }

        var writerParam = Expression.Parameter(typeof(MessagePackWriter).MakeByRefType(), "writer");
        var valueParam = Expression.Parameter(typeof(object), "value");
        var optionsParam = Expression.Parameter(typeof(MessagePackSerializerOptions), "options");

        var formatterConstant = Expression.Constant(formatter);
        var castValue = Expression.Convert(valueParam, type);

        var body = Expression.Call(formatterConstant, serializeMethod, writerParam, castValue, optionsParam);
        var lambda = Expression.Lambda<MessagePackSerializerDelegate>(body, writerParam, valueParam, optionsParam);
        return lambda.Compile();
    }

    private static MessagePackSerializerDelegate CreateFallbackSerializer(Type type)
    {
        return delegate (ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options)
        {
            var bytes = MessagePackSerializer.Serialize(type, value, options);
            writer.WriteRaw(bytes);
        };
    }
}

