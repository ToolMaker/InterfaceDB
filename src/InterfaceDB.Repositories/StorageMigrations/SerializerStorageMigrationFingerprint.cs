using System.Security.Cryptography;
using System.Text.Json;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Use a deterministic logical serializer, before its encryption decorator. No payload bytes are persisted here.</summary>
public sealed class SerializerStorageMigrationFingerprint<T>(IObjectSerializer<T> logicalSerializer,
    string identity) : IStorageMigrationFingerprint<T> where T : class, IStoredContract<T>
{
    public string Identity { get; } = identity + ":canonical-v2";
    public string Compute(StorageMigrationRecord<T> record)
    {
        var plain = record.Materialize();
        if (plain.Envelope is not null) throw new InvalidDataException("Fingerprint requires a detached value.");
        var envelope = new FileObjectEnvelope<T>
        {
            EnvelopeCacheKey = record.Key, VersionTag = record.Version,
            RevisionTimestampUtcMicroseconds = record.Revision, ExtensionMetadata = record.Metadata?.ToArray(),
            ObjectInstance = plain, ImplementationTypeName = plain.GetType().AssemblyQualifiedName
        };
        var bytes = (logicalSerializer as ICanonicalObjectSerializer<T>
            ?? throw new NotSupportedException("Logical integrity serialization requires ICanonicalObjectSerializer; supply that capability or an IStorageMigrationFingerprint implementation."))
            .SerializeCanonical(envelope);
        try
        {
            // Some object serializers omit identity metadata; verify it independently as well.
            var header = JsonSerializer.SerializeToUtf8Bytes(new { record.Key, record.Version, record.Revision, record.Metadata });
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header);
            hash.AppendData(bytes);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
