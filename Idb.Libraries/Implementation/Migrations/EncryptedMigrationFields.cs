using System.Reflection;
using Idb.Libraries.Implementation.Encryption;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using MessagePack;

namespace Idb.Libraries.Implementation.Migrations;

internal static class EncryptedMigrationFields
{
    private static readonly MessagePackSerializerOptions FieldSerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    internal static void Restore(IMigration migration, IDictionary<string, EncryptedFieldValue>? fields, IEncryptor encryptor, byte[]? recordContext = null)
    {
        if (fields is not { Count: > 0 }) return;

        // Use only properties declared by the registered migration type and its interfaces.
        // Persisted type-name metadata must never select a runtime type for deserialization.
        var type = migration.GetType();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Concat(type.GetInterfaces().SelectMany(static contract => contract.GetProperties()))
            .Where(static property => property.GetIndexParameters().Length == 0
                && property.GetMethod is { IsPublic: true }
                && property.SetMethod is { IsPublic: true })
            .ToLookup(static property => property.Name, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var candidates = properties[field.Key].ToArray();
            if (candidates.Length == 0
                || candidates.Select(static property => property.PropertyType).Distinct().Count() != 1
                || candidates.Select(property => ResolveSetter(type, property)).Distinct().Count() != 1)
            {
                throw new InvalidDataException(
                    $"Encrypted source field '{field.Key}' requires an unambiguous writable property on migration '{type.FullName}'.");
            }
            if (field.Value?.Ciphertext is not { Length: > 0 } ciphertext)
                throw new InvalidDataException($"Encrypted source field '{field.Key}' has no ciphertext.");

            // Restore under the SOURCE record context before Up() changes fields or identity.
            var plain = recordContext is null ? encryptor.Decrypt(ciphertext)
                : StorageFieldEncryption.Decrypt(encryptor, ciphertext, StorageFieldEncryption.Context(recordContext, field.Key));
            var value = MessagePackSerializer.Deserialize(candidates[0].PropertyType, plain, FieldSerializerOptions);
            candidates[0].SetValue(migration, value);
        }
    }

    private static MethodInfo ResolveSetter(Type implementationType, PropertyInfo property)
    {
        var setter = property.SetMethod!;
        if (property.DeclaringType!.IsInterface)
        {
            var map = implementationType.GetInterfaceMap(property.DeclaringType);
            setter = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, setter)];
        }
        return setter.GetBaseDefinition();
    }

}
