using System.Buffers;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectReader<TContract> : IObjectReader<TContract>, ICommittedEnvelopeSnapshotSource<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        private IObjectSerializer<TContract> _serializer;
        private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
        private readonly IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> _envelopes;
        private readonly IDbContractConfigDto<TContract> _contractConfig;
        private readonly StoragePolicy _storagePolicy;
        private readonly IFileWriteCoordinator _fileWriteCoordinator;

        public FileObjectReader(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
            IDbContractConfigDto<TContract> contractConfig,
            StoragePolicy storagePolicy,
            IFileWriteCoordinator fileWriteCoordinator,
            IEnvelopeStorageChangeSource<TContract> storageChangeSource)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
            _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
            _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
            _ = storageChangeSource ?? throw new ArgumentNullException(nameof(storageChangeSource));
        }

        public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            // Try cache by conventional key (file name without extension)
            var candidateKey = Path.GetFileNameWithoutExtension(file.Name);
            if (!string.IsNullOrWhiteSpace(candidateKey) && _envelopes.TryGetValue(candidateKey, out var cached) && cached is not null)
            {
                // Cache membership is keyed by the provider locator. Keep the envelope itself
                // canonical too, including when a strong cached object survived a file rename.
                cached.EnvelopeCacheKey = candidateKey;
                cached.Location = file.FullName;
                if (StorageObjectCachePolicy.TryGetObjectInstance(cached, out var cachedInstance) && cachedInstance is not null)
                {
                    cached.ObjectInstance = cachedInstance;
                    var unwrapped = _envelopeLifecycle.Unwrap(cached);
                    StorageObjectCachePolicy.StoreObjectReference(cached, unwrapped, _storagePolicy);
                    return unwrapped;
                }

                // Hydrate from disk without retaining the object unless the cache policy allows it.
                var deserialized = await ReadEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);
                deserialized.Location = file.FullName;
                // The provider locator, rather than serialized metadata, owns physical identity.
                // A copied or externally renamed payload can legitimately carry its former key.
                deserialized.EnvelopeCacheKey = candidateKey;

                if (deserialized.CacheObjectInstance)
                {
                    cached.CacheObjectInstance = true;
                }

                var cacheMode = StorageObjectCachePolicy.ResolveObjectCacheMode(deserialized, _storagePolicy);
                if (cacheMode is not StorageObjectCacheMode.MetadataOnly)
                {
                    var unwrapped = _envelopeLifecycle.Unwrap(deserialized);
                    cached.VersionTag = deserialized.VersionTag;
                    cached.Location = deserialized.Location;
                    cached.State = deserialized.State;
                    cached.ImplementationTypeName = deserialized.ImplementationTypeName;
                    cached.CacheObjectInstance = deserialized.CacheObjectInstance;
                    cached.EncryptedFields = deserialized.EncryptedFields;
                    cached.ExtensionMetadata = deserialized.ExtensionMetadata?.ToArray();
                    cached.RevisionTimestampUtcMicroseconds = deserialized.RevisionTimestampUtcMicroseconds;
                    StorageObjectCachePolicy.StoreObjectReference(cached, unwrapped, _storagePolicy);
                    return unwrapped;
                }

                return _envelopeLifecycle.Unwrap(deserialized);
            }

            // Fallback: no cache entry, hydrate directly
            var envelope = await ReadEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);

            envelope.Location = file.FullName;
            envelope.EnvelopeCacheKey = candidateKey;

            return _envelopeLifecycle.Unwrap(envelope);
        }

        public async ValueTask<ICommittedEnvelopeSnapshot<TContract>?> CaptureCommittedAsync(
            IObjectEnvelope<TContract> identity, int maximumBytes, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var stream = new FileStream(identity.Location!, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > maximumBytes)
                    throw new InvalidDataException("Notification snapshot exceeds the configured byte limit.");
                var payload = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
                return new CommittedEnvelopeSnapshot<TContract>(payload, _serializer, _envelopeLifecycle, identity.EnvelopeCacheKey, identity.Location);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private async ValueTask<IObjectEnvelope<TContract>> ReadEnvelopeAsync(
            FileInfo file,
            CancellationToken cancellationToken)
        {
            if (_serializer is IMemoryObjectSerializer<TContract> memorySerializer
                && _serializer is not IRotationAwareObjectSerializer<TContract>)
            {
                await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = checked((int)stream.Length);
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
                try
                {
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    return memorySerializer.DeserializeMemory(buffer.AsMemory(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }

            byte[] payload;
            await using (var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                payload = new byte[checked((int)source.Length)];
                await source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            if (_serializer is not IRotationAwareObjectSerializer<TContract> rotationAwareSerializer)
            {
                return _serializer.Deserialize(payload);
            }

            var result = rotationAwareSerializer.DeserializeForRead(payload);
            if (result.ReplacementPayload is not null)
            {
                _ = await _fileWriteCoordinator.ReplaceIfUnchangedAsync(
                    file.FullName,
                    payload,
                    result.ReplacementPayload,
                    cancellationToken).ConfigureAwait(false);
            }

            return result.Envelope;
        }
    }
}
