using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly IReadOnlyDictionary<string, EncryptedFieldDefinition> DefinitionsByName = Definitions.ToDictionary(
        static definition => definition.Name, StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Type, Lazy<PropertyInfo[]>> RuntimeProperties = new();
    private readonly IEncryptor _encryptor;
    private readonly Func<string, byte[]> _contextFactory;
    private readonly EncryptedFieldWriteMode _writeMode;

    public EncryptedStorageFieldProcessor(IEncryptor encryptor, Func<string, byte[]>? contextFactory,
        EncryptedFieldWriteMode writeMode)
    {
        if (!Enum.IsDefined(writeMode)) throw new ArgumentOutOfRangeException(nameof(writeMode));
        _writeMode = writeMode;
        if (Definitions.Count != 0 && writeMode == EncryptedFieldWriteMode.ContextBound && encryptor is not IContextualEncryptor)
            throw new InvalidOperationException("Context-bound fields require an IContextualEncryptor.");
        if (Definitions.Count != 0 && contextFactory is null)
            throw new ArgumentException("Encrypted fields require a trusted repository context factory.", nameof(contextFactory));
        _contextFactory = contextFactory ?? (_ => []);
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
        var context = _contextFactory(envelope.EnvelopeCacheKey);
        envelope.EncryptedFields ??= new Dictionary<string, EncryptedFieldValue>(StringComparer.Ordinal);
        for (var index = 0; index < Definitions.Count; index++)
        {
            var definition = Definitions[index];
            var property = runtimeProperties[index];
            // A proxy's backing fields are scrubbed. Read its logical value, including
            // an explicit null, rather than mistaking the scrubbed default for an edit.
            var value = entity is EncryptedStorageDispatchProxy<TContract>
                ? definition.Property.GetValue(entity)
                : property.GetValue(source);

            var plain = MessagePackSerializer.Serialize(definition.PropertyType, value, MessagePackSerializerOptions.Standard);
            envelope.EncryptedFields[definition.Name] = StorageFieldEncryption.Encrypt(_encryptor, plain,
                StorageFieldEncryption.Context(context, definition.Name),
                definition.ProfileName, definition.TypeName, _writeMode);

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

        if (Definitions.Count == 0) return entity;
        foreach (var definition in Definitions)
            if (!envelope.EncryptedFields.TryGetValue(definition.Name, out var field) || field?.Ciphertext is not { Length: > 0 })
                throw new System.Security.Cryptography.CryptographicException("A declared encrypted field is missing its ciphertext.");
        return EncryptedStorageDispatchProxy<TContract>.Create(UnwrapProxy(entity), envelope, _encryptor,
            DefinitionsByName, _contextFactory(envelope.EnvelopeCacheKey), _writeMode);
    }

    internal void BindFields(IObjectEnvelope<TContract> envelope)
    {
        if (Definitions.Count == 0) return;
        var context = _contextFactory(envelope.EnvelopeCacheKey);
        foreach (var definition in Definitions)
            if (envelope.EncryptedFields.TryGetValue(definition.Name, out var value))
                envelope.EncryptedFields[definition.Name] = StorageFieldEncryption.Bind(_encryptor, value,
                    StorageFieldEncryption.Context(context, definition.Name), _writeMode);
    }

    private static TContract UnwrapProxy(TContract entity)
        => entity is EncryptedStorageDispatchProxy<TContract> proxy
            ? proxy.Inner
            : entity;

    private static readonly Func<object, object> Clone = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<object, object>>();

    internal static TContract CloneForStorage(TContract entity)
    {
        var copy = (TContract)Clone(UnwrapProxy(entity));
        if (entity is EncryptedStorageDispatchProxy<TContract>)
        {
            var properties = ResolveRuntimeProperties(copy.GetType());
            for (var index = 0; index < Definitions.Count; index++)
                properties[index].SetValue(copy, Definitions[index].Property.GetValue(entity));
        }
        return copy;
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static PropertyInfo[] ResolveRuntimeProperties(Type runtimeType)
        => RuntimeProperties.GetValue(runtimeType, static type => new(() =>
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(static property => property.GetMethod is not null)
                .GroupBy(static property => property.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            return Definitions.Select(definition => properties.GetValueOrDefault(definition.Name) ?? definition.Property).ToArray();
        })).Value;

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
