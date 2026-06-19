using System.Reflection;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Encryption;

internal sealed class EncryptedStorageFieldProcessor<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private static readonly IReadOnlyList<EncryptedFieldDefinition> Definitions = DiscoverDefinitions(typeof(TContract));
    private readonly IEncryptor _encryptor;

    public EncryptedStorageFieldProcessor(IEncryptor encryptor)
    {
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        if (RequiresEncryption && encryptor is EncryptionNone)
        {
            throw new InvalidOperationException(
                $"Contract '{typeof(TContract).FullName}' declares encrypted storage requirements but no encryption provider is configured.");
        }
    }

    public static bool RequiresEncryption =>
        HasEncryptedStorageAttribute(typeof(TContract))
        || Definitions.Count > 0;

    public bool HasEncryptedFields => Definitions.Count > 0;

    public TContract PrepareForStorage(TContract entity, IObjectEnvelope<TContract> envelope)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(envelope);

        if (Definitions.Count == 0)
        {
            return UnwrapProxy(entity);
        }

        var source = UnwrapProxy(entity);
        var runtimeProperties = ResolveRuntimeProperties(source.GetType());
        envelope.EncryptedFields ??= new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            var property = runtimeProperties.GetValueOrDefault(definition.Name) ?? definition.Property;
            var value = property.GetValue(source);
            if (value is null && envelope.EncryptedFields.ContainsKey(definition.Name))
            {
                continue;
            }

            var plain = MessagePackSerializer.Serialize(definition.PropertyType, value, MessagePackSerializerOptions.Standard);
            envelope.EncryptedFields[definition.Name] = new EncryptedFieldValue
            {
                ProfileName = definition.ProfileName,
                PropertyTypeName = definition.TypeName,
                Ciphertext = _encryptor.Encrypt(plain)
            };

            if (property.SetMethod is not null)
            {
                property.SetValue(source, GetDefault(definition.PropertyType));
            }
        }

        return source;
    }

    public TContract WrapForRead(TContract entity, IObjectEnvelope<TContract> envelope)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(envelope);

        return Definitions.Count == 0
            ? entity
            : EncryptedStorageDispatchProxy<TContract>.Create(entity, envelope, _encryptor, Definitions);
    }

    private static TContract UnwrapProxy(TContract entity)
        => entity is EncryptedStorageDispatchProxy<TContract> proxy
            ? proxy.Inner
            : entity;

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static IReadOnlyDictionary<string, PropertyInfo> ResolveRuntimeProperties(Type runtimeType)
        => runtimeType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.GetMethod is not null)
            .GroupBy(static property => property.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);

    private static bool HasEncryptedStorageAttribute(Type type)
        => GetContractTypes(type)
            .Any(static contractType => contractType.IsDefined(typeof(EncryptedStorageAttribute), inherit: false));

    private static IReadOnlyList<EncryptedFieldDefinition> DiscoverDefinitions(Type type)
    {
        var definitions = new List<EncryptedFieldDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contractType in GetContractTypes(type))
        {
            foreach (var property in contractType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!seen.Add(property.Name))
                {
                    continue;
                }

                var attribute = property.GetCustomAttribute<EncryptedStorageFieldAttribute>(inherit: true);
                if (attribute is null)
                {
                    continue;
                }

                if (property.GetMethod is null || property.SetMethod is null)
                {
                    throw new InvalidOperationException(
                        $"Encrypted storage field '{contractType.FullName}.{property.Name}' must have public get and set accessors.");
                }

                definitions.Add(new EncryptedFieldDefinition(
                    property.Name,
                    property,
                    property.PropertyType,
                    property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.FullName ?? property.PropertyType.Name,
                    attribute.ProfileName));
            }
        }

        return definitions;
    }

    private static IEnumerable<Type> GetContractTypes(Type type)
    {
        if (type.IsInterface)
        {
            yield return type;
        }

        foreach (var contractType in type.GetInterfaces())
        {
            yield return contractType;
        }

        if (!type.IsInterface)
        {
            yield return type;
        }
    }
}

internal sealed record EncryptedFieldDefinition(
    string Name,
    PropertyInfo Property,
    Type PropertyType,
    string TypeName,
    string ProfileName);
