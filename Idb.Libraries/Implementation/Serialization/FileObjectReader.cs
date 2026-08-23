using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectReader<TContract> : IObjectReader<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        private IObjectSerializer<TContract> _serializer;
        private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
        private readonly IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> _envelopes;
        private readonly IDbContractConfigDto<TContract> _contractConfig;
        private readonly StoragePolicy _storagePolicy;
        private readonly IFileWriteCoordinator _fileWriteCoordinator;
        private readonly IEnvelopeStorageChangeSource<TContract> _storageChangeSource;

        public FileObjectReader(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes,
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
            _storageChangeSource = storageChangeSource ?? throw new ArgumentNullException(nameof(storageChangeSource));
        }

        public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            // Try cache by conventional key (file name without extension)
            var candidateKey = Path.GetFileNameWithoutExtension(file.Name);
            if (!string.IsNullOrWhiteSpace(candidateKey) && _envelopes.TryGetValue(candidateKey, out var cached) && cached is not null)
            {
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
                if (string.IsNullOrWhiteSpace(deserialized.EnvelopeCacheKey))
                {
                    deserialized.EnvelopeCacheKey = candidateKey;
                }

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
                    StorageObjectCachePolicy.StoreObjectReference(cached, unwrapped, _storagePolicy);
                    return unwrapped;
                }

                return _envelopeLifecycle.Unwrap(deserialized);
            }

            // Fallback: no cache entry, hydrate directly
            var envelope = await ReadEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);

            envelope.Location = file.FullName;
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                envelope.EnvelopeCacheKey = candidateKey;
            }

            return _envelopeLifecycle.Unwrap(envelope);
        }

        private async ValueTask<IObjectEnvelope<TContract>> ReadEnvelopeAsync(
            FileInfo file,
            CancellationToken cancellationToken)
        {
            var payload = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            if (_serializer is not IRotationAwareObjectSerializer<TContract> rotationAwareSerializer)
            {
                return _serializer.Deserialize(payload);
            }

            var result = rotationAwareSerializer.DeserializeForRead(payload);
            if (result.ReplacementPayload is not null)
            {
                var replaced = await _fileWriteCoordinator.ReplaceIfUnchangedAsync(
                    file.FullName,
                    payload,
                    result.ReplacementPayload,
                    cancellationToken).ConfigureAwait(false);
                if (replaced)
                {
                    _storageChangeSource.AcknowledgeLocalChange(file.FullName);
                }
            }

            return result.Envelope;
        }
    }
}
