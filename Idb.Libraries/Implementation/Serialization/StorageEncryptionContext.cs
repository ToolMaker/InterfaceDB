using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization;

/// <summary>Builds a stable, non-secret repository context for authenticated storage encryption.</summary>
public static class StorageEncryptionContext
{
    public static byte[] Create<TContract>(
        IDbContractConfigDto<TContract> configuration,
        string provider,
        string physicalKey)
        where TContract : class, IStoredContract<TContract>
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalKey);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "InterfaceDB.StorageEncryptionContext.v1");
        Append(hash, provider);
        Append(hash, typeof(TContract).Assembly.GetName().Name ?? string.Empty);
        Append(hash, typeof(TContract).FullName ?? typeof(TContract).Name);
        Append(hash, configuration.EnvelopeDICacheKey ?? string.Empty);
        Append(hash, configuration.ContractName ?? string.Empty);
        Append(hash, physicalKey);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

/// <summary>Applies authenticated storage context when the configured serializer encrypts payloads.</summary>
public static class ContextualObjectSerialization
{
    public static byte[] Serialize<TContract>(
        IObjectSerializer<TContract> serializer,
        IObjectEnvelope<TContract> envelope,
        ReadOnlySpan<byte> associatedData)
        where TContract : class, IStoredContract<TContract>
        => serializer is EncryptingObjectSerializer<TContract> encrypting
            ? encrypting.Serialize(envelope, associatedData)
            : serializer.Serialize(envelope);

    public static IObjectEnvelope<TContract> Deserialize<TContract>(
        IObjectSerializer<TContract> serializer,
        byte[] payload,
        ReadOnlySpan<byte> associatedData)
        where TContract : class, IStoredContract<TContract>
        => serializer is EncryptingObjectSerializer<TContract> encrypting
            ? encrypting.Deserialize(payload, associatedData)
            : serializer.Deserialize(payload);

    public static RotationAwareDeserializationResult<TContract> DeserializeForRead<TContract>(
        IObjectSerializer<TContract> serializer,
        byte[] payload,
        ReadOnlySpan<byte> associatedData)
        where TContract : class, IStoredContract<TContract>
    {
        if (serializer is EncryptingObjectSerializer<TContract> encrypting)
        {
            return encrypting.DeserializeForRead(payload, associatedData);
        }

        if (serializer is IRotationAwareObjectSerializer<TContract> rotationAware)
        {
            return rotationAware.DeserializeForRead(payload);
        }

        return new RotationAwareDeserializationResult<TContract>(serializer.Deserialize(payload), null);
    }

    public static long ReadRevision<TContract>(
        IObjectSerializer<TContract> serializer,
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> associatedData)
        where TContract : class, IStoredContract<TContract>
        => serializer is EncryptingObjectSerializer<TContract> encrypting
            ? encrypting.ReadRevision(payload, associatedData)
            : ((IRevisionObjectSerializer<TContract>)serializer).ReadRevision(payload);
}
