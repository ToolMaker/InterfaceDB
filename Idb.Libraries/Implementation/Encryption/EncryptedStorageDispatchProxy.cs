using System.Reflection;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;
using Idb.Libraries.Implementation.Indexes;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Libraries.Implementation.Encryption;

internal class EncryptedStorageDispatchProxy<TContract> : DispatchProxy, IWrappedContractInstance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private TContract? _inner;
    private IObjectEnvelope<TContract>? _envelope;
    private IEncryptor? _encryptor;
    private byte[] _recordContext = [];
    private EncryptedFieldWriteMode _writeMode;
    private static readonly IReadOnlyDictionary<string, EncryptedFieldDefinition> EmptyDefinitions =
        new Dictionary<string, EncryptedFieldDefinition>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, EncryptedFieldDefinition> _definitions = EmptyDefinitions;

    internal TContract Inner =>
        _inner ?? throw new InvalidOperationException("Encrypted storage proxy has not been initialized.");

    TContract IWrappedContractInstance<TContract>.Implementation => Inner;

    TContract IWrappedContractInstance<TContract>.CreateImplementationCopy()
    {
        var copy = EncryptedStorageFieldProcessor<TContract>.CloneForStorage((TContract)(object)this);
        var envelope = EnvelopeWriteCoordinator<TContract>.CopyEnvelope(_envelope!);
        envelope.ObjectInstance = copy;
        StoredContractEnvelopeAccessor<TContract>.SetEnvelope(copy, envelope);
        return copy;
    }

    public static TContract Create(
        TContract inner,
        IObjectEnvelope<TContract> envelope,
        IEncryptor encryptor,
        IReadOnlyDictionary<string, EncryptedFieldDefinition> definitions, byte[] recordContext, EncryptedFieldWriteMode writeMode)
    {
        var proxy = DispatchProxy.Create<TContract, EncryptedStorageDispatchProxy<TContract>>();
        var typedProxy = (EncryptedStorageDispatchProxy<TContract>)(object)proxy;
        typedProxy._recordContext = recordContext.ToArray();
        typedProxy._writeMode = writeMode;
        typedProxy._inner = inner ?? throw new ArgumentNullException(nameof(inner));
        typedProxy._envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
        typedProxy._encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        typedProxy._definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new ArgumentNullException(nameof(targetMethod));
        }

        var inner = Inner;
        var envelope = _envelope ?? throw new InvalidOperationException("Encrypted storage proxy envelope has not been initialized.");
        var encryptor = _encryptor ?? throw new InvalidOperationException("Encrypted storage proxy encryptor has not been initialized.");

        if (IsEnvelopeGetter(targetMethod))
        {
            return envelope;
        }

        if (TryGetPropertyName(targetMethod, "get_", out var getterName)
            && _definitions.TryGetValue(getterName, out var getterDefinition))
        {
            if (!envelope.EncryptedFields.TryGetValue(getterDefinition.Name, out var encryptedValue))
            {
                throw new System.Security.Cryptography.CryptographicException("A declared encrypted field is missing its ciphertext.");
            }

            var plain = StorageFieldEncryption.Decrypt(encryptor, encryptedValue.Ciphertext,
                StorageFieldEncryption.Context(_recordContext, getterDefinition.Name));
            return MessagePackSerializer.Deserialize(getterDefinition.PropertyType, plain, MessagePackSerializerOptions.Standard);
        }

        if (TryGetPropertyName(targetMethod, "set_", out var setterName)
            && _definitions.TryGetValue(setterName, out var setterDefinition))
        {
            var value = args is { Length: > 0 } ? args[0] : null;
            var plain = MessagePackSerializer.Serialize(setterDefinition.PropertyType, value, MessagePackSerializerOptions.Standard);
            envelope.EncryptedFields[setterDefinition.Name] = StorageFieldEncryption.Encrypt(encryptor, plain,
                StorageFieldEncryption.Context(_recordContext, setterDefinition.Name),
                setterDefinition.ProfileName, setterDefinition.TypeName, _writeMode);

            setterDefinition.Property.SetValue(inner, GetDefault(setterDefinition.PropertyType));
            return null;
        }

        return targetMethod.Invoke(inner, args);
    }

    private static bool IsEnvelopeGetter(MethodInfo method)
        => method.Name == "get_Envelope"
            && method.GetParameters().Length == 0
            && method.ReturnType == typeof(IObjectEnvelope<TContract>);

    private static bool TryGetPropertyName(MethodInfo method, string prefix, out string propertyName)
    {
        if (method.Name.StartsWith(prefix, StringComparison.Ordinal))
        {
            propertyName = method.Name[prefix.Length..];
            return true;
        }

        propertyName = string.Empty;
        return false;
    }

    private static object? GetDefault(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;
}
