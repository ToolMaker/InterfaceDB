using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectWriter<TContract> : IObjectWriter<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ensuredDirs = new();
        private IObjectSerializer<TContract> _serializer;
        private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
        private readonly SemaphoreSlim? _writeSemaphore;
        private readonly StoragePolicy _storagePolicy;
        private readonly IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> _envelopes;
        private readonly IDbContractConfigDto<TContract> _contractConfig;
        private readonly IFileWriteCoordinator _fileWriteCoordinator;
        private readonly IEnvelopeStorageIdentityFactory<TContract> _storageIdentityFactory;

        public FileObjectWriter(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IDbContractConfigDto<TContract> contractConfig,
            StoragePolicy storagePolicy,
            IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes,
            IFileWriteCoordinator fileWriteCoordinator,
            IEnvelopeStorageIdentityFactory<TContract> storageIdentityFactory)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
            _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
            _storageIdentityFactory = storageIdentityFactory ?? throw new ArgumentNullException(nameof(storageIdentityFactory));
            _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
            var normalizedConcurrency = _storagePolicy.NormalizedConcurrency;
            _writeSemaphore = (normalizedConcurrency > 1 && _storagePolicy.UseWriterSemaphore)
                ? new SemaphoreSlim(normalizedConcurrency, normalizedConcurrency)
                : null;
        }

        public async ValueTask<TContract> WriteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
        {
            if (objectInstance is null)
            {
                throw new ArgumentNullException(nameof(objectInstance));
            }

            if (objectInstance.Envelope?.State == StorageObjectState.Deleted)
            {
                throw new StorageObjectDeletedException(
                    objectInstance.Envelope.EnvelopeCacheKey,
                    objectInstance.Envelope.Location);
            }

            var isNewStorageObject = objectInstance.Envelope is null;
            var envelope = await _envelopeLifecycle
            .WrapAsync(objectInstance)
            .ConfigureAwait(false);

            var semaphore = _writeSemaphore;
            if (semaphore is not null)
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // Ensure directory exists once per target path (reduce repeated OS calls)
                var path = envelope.Location;
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new InvalidOperationException("Envelope location must be initialized before writing.");
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && _ensuredDirs.TryAdd(dir, 0))
                {
                    Directory.CreateDirectory(dir);
                }

                if (_storagePolicy.DurabilityMode == StorageDurabilityMode.Performance)
                {
                    await WritePerformanceAsync(
                        envelope,
                        path,
                        isNewStorageObject
                            && _storageIdentityFactory.ObjectCardinality == StorageObjectCardinality.Single,
                        cancellationToken).ConfigureAwait(false);

                    UpdateEnvelopeCache(envelope);
                    _envelopeLifecycle.Unwrap(envelope);
                    return objectInstance;
                }

                var proposalIsCanonical = await _fileWriteCoordinator.PublishAndCommitAsync(
                    path,
                    requireNewPayload: isNewStorageObject
                        && _storageIdentityFactory.ObjectCardinality == StorageObjectCardinality.Single,
                    async (proposalPath, token) =>
                    {
                        if (_storagePolicy.UseSingleWriteBuffer)
                        {
                            // Publish only after a complete payload has been flushed to disk.
                            var payload = _serializer.Serialize(envelope);
                            using var handle = File.OpenHandle(
                                proposalPath,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None,
                                options: FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                            await RandomAccess.WriteAsync(handle, payload, 0, token).ConfigureAwait(false);
                            RandomAccess.FlushToDisk(handle);
                        }
                        else
                        {
                            var fso = new FileStreamOptions
                            {
                                Mode = FileMode.CreateNew,
                                Access = FileAccess.Write,
                                Share = FileShare.None,
                                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                                BufferSize = _storagePolicy.NormalizedBufferSize
                            };

                            await using var stream = new FileStream(proposalPath, fso);
                            await _serializer.SerializeAsync(envelope, stream, token).ConfigureAwait(false);
                            await stream.FlushAsync(token).ConfigureAwait(false);
                            stream.Flush(flushToDisk: true);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                var canonicalEnvelope = proposalIsCanonical
                    ? envelope
                    : await ReadCanonicalEnvelopeAsync(
                        path,
                        envelope.EnvelopeCacheKey,
                        cancellationToken).ConfigureAwait(false);

                UpdateEnvelopeCache(canonicalEnvelope);

                _envelopeLifecycle.Unwrap(envelope);
                return ReferenceEquals(canonicalEnvelope, envelope)
                    ? objectInstance
                    : _envelopeLifecycle.Unwrap(canonicalEnvelope);
            }
            finally
            {
                semaphore?.Release();
            }

        }

        private async ValueTask WritePerformanceAsync(
            IObjectEnvelope<TContract> envelope,
            string path,
            bool requireNewPayload,
            CancellationToken cancellationToken)
        {
            if (requireNewPayload && File.Exists(path))
            {
                throw new StorageObjectAlreadyExistsException(path);
            }

            var fileMode = requireNewPayload ? FileMode.CreateNew : FileMode.Create;
            if (_storagePolicy.UseSingleWriteBuffer)
            {
                var payload = _serializer.Serialize(envelope);
                using var handle = File.OpenHandle(
                    path,
                    fileMode,
                    FileAccess.Write,
                    FileShare.None,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await RandomAccess.WriteAsync(handle, payload, 0, cancellationToken).ConfigureAwait(false);
                return;
            }

            var fso = new FileStreamOptions
            {
                Mode = fileMode,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = _storagePolicy.NormalizedBufferSize
            };

            await using var stream = new FileStream(path, fso);
            await _serializer.SerializeAsync(envelope, stream, cancellationToken).ConfigureAwait(false);
        }

        private void UpdateEnvelopeCache(IObjectEnvelope<TContract> envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                return;
            }

            if (_storagePolicy.KeepObjectInstanceInCacheOnWrite)
            {
                _envelopes[envelope.EnvelopeCacheKey] = envelope;
                return;
            }

            var skeleton = new FileObjectEnvelope<TContract>(_contractConfig)
            {
                EnvelopeCacheKey = envelope.EnvelopeCacheKey,
                VersionTag = envelope.VersionTag,
                Location = envelope.Location,
                ObjectInstance = null
            };
            _envelopes[envelope.EnvelopeCacheKey] = skeleton;
        }

        private async ValueTask<IObjectEnvelope<TContract>> ReadCanonicalEnvelopeAsync(
            string path,
            string expectedCacheKey,
            CancellationToken cancellationToken)
        {
            const int attempts = 5;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        _storagePolicy.NormalizedBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var canonical = await _serializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
                    canonical.Location = path;
                    canonical.EnvelopeCacheKey = expectedCacheKey;
                    return canonical;
                }
                catch (IOException) when (attempt < attempts)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException($"Unable to read canonical payload '{path}' after coalescing.");
        }

    }
}
