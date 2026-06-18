using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using System.Collections.Concurrent;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectWriter<TContract> : IObjectWriter<TContract>
        where TContract : class, IStoredContract<TContract>
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ensuredDirs = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _identitySemaphores = new(StringComparer.OrdinalIgnoreCase);
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

            var identitySemaphore = isNewStorageObject
                ? await AcquireIdentitySemaphoreAsync(envelope, cancellationToken).ConfigureAwait(false)
                : null;
            var semaphore = _writeSemaphore;
            if (semaphore is not null)
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (isNewStorageObject)
                {
                    ResolveNewStorageIdentity(envelope);
                }

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
                        isNewStorageObject,
                        cancellationToken).ConfigureAwait(false);

                    UpdateEnvelopeCache(envelope);
                    _envelopeLifecycle.Unwrap(envelope);
                    return objectInstance;
                }

                var proposalIsCanonical = await _fileWriteCoordinator.PublishAndCommitAsync(
                    path,
                    requireNewPayload: isNewStorageObject,
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
                identitySemaphore?.Release();
            }

        }

        public async ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
            IEnumerable<TContract> objectInstances,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(objectInstances);

            var materialized = objectInstances as IReadOnlyList<TContract> ?? objectInstances.ToArray();
            if (materialized.Count == 0)
            {
                return Array.Empty<TContract>();
            }

            var results = new TContract?[materialized.Count];
            var failures = new ConcurrentBag<StorageBulkOperationFailure>();
            var batchSize = Math.Min(_storagePolicy.NormalizedBulkWriteBatchSize, materialized.Count);
            var concurrency = Math.Min(_storagePolicy.NormalizedConcurrency, batchSize);

            for (var batchStart = 0; batchStart < materialized.Count; batchStart += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchEnd = Math.Min(batchStart + batchSize, materialized.Count);
                await WriteBatchWindowAsync(
                    materialized,
                    results,
                    failures,
                    batchStart,
                    batchEnd,
                    Math.Min(concurrency, batchEnd - batchStart),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!failures.IsEmpty)
            {
                var orderedFailures = failures
                    .OrderBy(static failure => failure.Index)
                    .ToArray();
                var succeededIndexes = results
                    .Select((item, index) => item is null ? -1 : index)
                    .Where(static index => index >= 0)
                    .ToArray();

                throw new StorageBulkOperationException(
                    StorageOperation.Write,
                    succeededIndexes,
                    orderedFailures);
            }

            var completed = new TContract[materialized.Count];
            for (var index = 0; index < results.Length; index++)
            {
                completed[index] = results[index]!;
            }

            return completed;
        }

        private async ValueTask WriteBatchWindowAsync(
            IReadOnlyList<TContract> materialized,
            TContract?[] results,
            ConcurrentBag<StorageBulkOperationFailure> failures,
            int batchStart,
            int batchEnd,
            int concurrency,
            CancellationToken cancellationToken)
        {
            if (concurrency <= 1)
            {
                for (var index = batchStart; index < batchEnd; index++)
                {
                    await TryWriteBatchItemAsync(materialized, results, failures, index, cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            var workers = new Task[concurrency];
            var chunkSize = ((batchEnd - batchStart) + concurrency - 1) / concurrency;
            for (var worker = 0; worker < workers.Length; worker++)
            {
                var start = batchStart + (worker * chunkSize);
                var end = Math.Min(start + chunkSize, batchEnd);
                workers[worker] = Task.Run(async () =>
                {
                    for (var index = start; index < end; index++)
                    {
                        await TryWriteBatchItemAsync(materialized, results, failures, index, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private async ValueTask TryWriteBatchItemAsync(
            IReadOnlyList<TContract> materialized,
            TContract?[] results,
            ConcurrentBag<StorageBulkOperationFailure> failures,
            int index,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = materialized[index];
            try
            {
                results[index] = await WriteAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(CreateFailure(index, instance, ex));
            }
        }

        private static StorageBulkOperationFailure CreateFailure(
            int index,
            TContract instance,
            Exception exception)
        {
            var envelope = instance.Envelope;
            return new StorageBulkOperationFailure(
                index,
                envelope?.EnvelopeCacheKey,
                envelope?.Location,
                exception);
        }

        private async ValueTask<SemaphoreSlim?> AcquireIdentitySemaphoreAsync(
            IObjectEnvelope<TContract> envelope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                return null;
            }

            var logicalKey = EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey);
            var semaphoreKey = typeof(TContract).FullName + "|" + logicalKey;
            var semaphore = _identitySemaphores.GetOrAdd(semaphoreKey, static _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return semaphore;
        }

        private void ResolveNewStorageIdentity(IObjectEnvelope<TContract> envelope)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                return;
            }

            var logicalKey = EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey);
            var existingCount = _envelopes.GetGroupCount(logicalKey);
            if (_storageIdentityFactory.ObjectCardinality == StorageObjectCardinality.Single)
            {
                if (existingCount > 0)
                {
                    throw new StorageObjectAlreadyExistsException(envelope.Location ?? envelope.EnvelopeCacheKey);
                }

                return;
            }

            var physicalKey = EnvelopeCacheKey.ToPhysicalKey(logicalKey, existingCount);
            if (string.Equals(physicalKey, envelope.EnvelopeCacheKey, StringComparison.Ordinal))
            {
                return;
            }

            envelope.EnvelopeCacheKey = physicalKey;
            if (!string.IsNullOrWhiteSpace(envelope.Location))
            {
                var directory = Path.GetDirectoryName(envelope.Location);
                var extension = Path.GetExtension(envelope.Location);
                envelope.Location = string.IsNullOrWhiteSpace(directory)
                    ? physicalKey + extension
                    : Path.Combine(directory, physicalKey + extension);
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
                _envelopes.AddOrUpdate(
                    envelope.EnvelopeCacheKey,
                    EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey),
                    envelope);
                return;
            }

            if (_envelopes.TryGetValue(envelope.EnvelopeCacheKey, out var cached) && cached is not null)
            {
                cached.VersionTag = envelope.VersionTag;
                cached.Location = envelope.Location;
                cached.State = envelope.State;
                cached.ObjectInstance = null;
                return;
            }

            var skeleton = new FileObjectEnvelope<TContract>(_contractConfig)
            {
                EnvelopeCacheKey = envelope.EnvelopeCacheKey,
                VersionTag = envelope.VersionTag,
                Location = envelope.Location,
                ObjectInstance = null,
                State = envelope.State
            };
            _envelopes.AddOrUpdate(
                envelope.EnvelopeCacheKey,
                EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey),
                skeleton);
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
