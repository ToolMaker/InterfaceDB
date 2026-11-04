using MessagePack;

namespace Idb.Commercial.Repositories;

internal static class ResilientEnvelopeMetadataCodec
{
    private const int MaximumMetadataLength = 32 * 1024;
    private static readonly byte[] Magic = "IDBR"u8.ToArray();
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    internal static ResilientEnvelopeMetadata Decode<T>(Idb.Libraries.Abstractions.Services.Serialization.IObjectEnvelope<T> envelope)
        where T : class, Idb.Libraries.Abstractions.Dtos.IStoredContract<T>
    {
        var metadata = Decode(envelope.ExtensionMetadata);
        return metadata with { RevisionTimestampUtcMicroseconds = envelope.RevisionTimestampUtcMicroseconds > 0
            ? envelope.RevisionTimestampUtcMicroseconds : metadata.RevisionTimestampUtcMicroseconds };
    }

    internal static byte[] Encode(ResilientEnvelopeMetadata metadata)
    {
        Validate(metadata);
        var payload = MessagePackSerializer.Serialize(metadata, SerializerOptions);
        if (payload.Length > MaximumMetadataLength - Magic.Length)
        {
            throw new InvalidDataException("Resilient envelope metadata exceeds its size limit.");
        }

        var result = GC.AllocateUninitializedArray<byte>(Magic.Length + payload.Length);
        Magic.CopyTo(result, 0);
        payload.CopyTo(result, Magic.Length);
        return result;
    }

    internal static ResilientEnvelopeMetadata Decode(byte[]? extensionMetadata)
    {
        if (extensionMetadata is null || extensionMetadata.Length == 0)
        {
            return ResilientEnvelopeMetadata.Empty;
        }

        if (extensionMetadata.Length > MaximumMetadataLength)
        {
            throw new InvalidDataException("Resilient envelope metadata exceeds its size limit.");
        }

        if (extensionMetadata.Length <= Magic.Length
            || !extensionMetadata.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The envelope extension metadata is not a resilient-repository record.");
        }

        try
        {
            var metadata = MessagePackSerializer.Deserialize<ResilientEnvelopeMetadata>(
                extensionMetadata.AsMemory(Magic.Length),
                SerializerOptions);
            Validate(metadata);
            return metadata;
        }
        catch (MessagePackSerializationException exception)
        {
            throw new InvalidDataException("The resilient envelope metadata is invalid.", exception);
        }
    }

    private static void Validate(ResilientEnvelopeMetadata? metadata)
    {
        if (metadata is null)
        {
            throw new InvalidDataException("The resilient envelope metadata cannot be null.");
        }

        if (metadata.FormatVersion is not (1 or 2 or ResilientEnvelopeMetadata.CurrentFormatVersion))
        {
            throw new InvalidDataException(
                $"Unsupported resilient envelope metadata version '{metadata.FormatVersion}'.");
        }

        if (metadata.BasePeerRevision < -1 || metadata.CacheScope?.Length > 256 || metadata.RemoteIdentity?.Length > 256 || metadata.RemoteStorageKey?.Length > 4096) throw new InvalidDataException("Invalid synchronization scope or base revision.");

        if (metadata.RevisionTimestampUtcMicroseconds < 0)
        {
            throw new InvalidDataException("A revision timestamp cannot be negative.");
        }

        if (!Enum.IsDefined(metadata.PendingMutationKind))
        {
            throw new InvalidDataException("The pending mutation kind is invalid.");
        }

        var hasPendingId = metadata.PendingMutationId != Guid.Empty;
        var hasPendingKind = metadata.PendingMutationKind != PendingMutationKind.None;
        if (hasPendingId != hasPendingKind)
        {
            throw new InvalidDataException("A pending mutation requires both an identifier and a kind.");
        }

        if (metadata.PendingMutationKind == PendingMutationKind.Delete && !metadata.IsTombstone)
        {
            throw new InvalidDataException("A pending delete must be represented by a tombstone.");
        }

        if (metadata.PendingMutationKind == PendingMutationKind.Upsert && metadata.IsTombstone)
        {
            throw new InvalidDataException("A pending upsert cannot be represented by a tombstone.");
        }
    }
}
