using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Services.Encryption;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Encryption;

internal static class StorageFieldEncryption
{
    // Only ciphertext created in this process may be rebound after identity allocation. Never
    // persist or trust a claimed source context from the envelope being read.
    private sealed record Binding(byte[] Context, bool Legacy);
    private static readonly ConditionalWeakTable<EncryptedFieldValue, Binding> Bindings = new();

    internal static byte[] Context(ReadOnlySpan<byte> recordContext, string name)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("InterfaceDB.EncryptedStorageField.v1"u8);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, recordContext.Length);
        hash.AppendData(length); hash.AppendData(recordContext);
        var field = Encoding.UTF8.GetBytes(name);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, field.Length);
        hash.AppendData(length); hash.AppendData(field);
        return hash.GetHashAndReset();
    }

    internal static byte[] Decrypt(IEncryptor encryptor, byte[] ciphertext, byte[] context)
        => encryptor is IContextualEncryptor contextual ? contextual.Decrypt(ciphertext, context) : encryptor.Decrypt(ciphertext);

    internal static EncryptedFieldValue Encrypt(IEncryptor encryptor, byte[] plaintext,
        byte[] context, string profile, string type, EncryptedFieldWriteMode mode)
    {
        var legacy = mode == EncryptedFieldWriteMode.LegacyContextless;
        var cipher = legacy ? encryptor.Encrypt(plaintext)
            : (encryptor as IContextualEncryptor ?? throw new InvalidOperationException(
                "Context-bound fields require an IContextualEncryptor.")).Encrypt(plaintext, context);
        var value = new EncryptedFieldValue { Ciphertext = cipher, ProfileName = profile, PropertyTypeName = type };
        Bindings.Add(value, new(context.ToArray(), legacy));
        return value;
    }

    internal static EncryptedFieldValue Bind(IEncryptor encryptor, EncryptedFieldValue value,
        byte[] target, EncryptedFieldWriteMode mode)
    {
        if (!Bindings.TryGetValue(value, out var source)) return value;
        if (source.Context.AsSpan().SequenceEqual(target)) return value;
        var plain = source.Legacy ? encryptor.Decrypt(value.Ciphertext) : Decrypt(encryptor, value.Ciphertext, source.Context);
        return Encrypt(encryptor, plain, target, value.ProfileName, value.PropertyTypeName, mode);
    }
}
