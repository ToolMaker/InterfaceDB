using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Exceptions;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;
using System.Collections.Concurrent;

namespace Idb.Libraries.Implementation.Serialization
{
    public partial class FileObjectWriter<TContract> : IObjectWriter<TContract>, IEnvelopeDeleteWriter<TContract>, IRetainedVersionStore<TContract>, IDisposable
        where TContract : class, IStoredContract<TContract>
    {
        private static readonly ConcurrentDictionary<string, byte> _ensuredDirs = new();
        private static readonly ConcurrentDictionary<string, IdentitySemaphoreEntry> _identitySemaphores = new(
            StringComparer.Ordinal);
        private IObjectSerializer<TContract> _serializer;
        private readonly IEnvelopeLifecycle<TContract> _envelopeLifecycle;
        private readonly SemaphoreSlim? _writeSemaphore;
        private readonly StoragePolicy _storagePolicy;
        private readonly IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> _envelopes;
        private readonly IDbContractConfigDto<TContract> _contractConfig;
        private readonly IFileWriteCoordinator _fileWriteCoordinator;
        private readonly IEnvelopeStorageIdentityFactory<TContract> _storageIdentityFactory;
        private readonly string _identityLockScope;
        private readonly StringComparer _pathComparer;
        private readonly EnvelopeWriteCoordinator<TContract> _revisions;

        public FileObjectWriter(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IDbContractConfigDto<TContract> contractConfig,
            StoragePolicy storagePolicy,
            IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
            IFileWriteCoordinator fileWriteCoordinator,
            IEnvelopeStorageIdentityFactory<TContract> storageIdentityFactory,
            IWriteConflictStrategy<TContract>? writeConflictStrategy = null)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
            _fileWriteCoordinator = fileWriteCoordinator ?? throw new ArgumentNullException(nameof(fileWriteCoordinator));
            _storageIdentityFactory = storageIdentityFactory ?? throw new ArgumentNullException(nameof(storageIdentityFactory));
            _storagePolicy = storagePolicy ?? throw new ArgumentNullException(nameof(storagePolicy));
            _identityLockScope = Path.GetFullPath(_contractConfig.DataRoot
                ?? throw new InvalidOperationException("Contract configuration must supply a DataRoot directory."));
            _pathComparer = FileSystemPathIdentity.ResolveComparer(_identityLockScope);
            if (storagePolicy.RetainedVersions is { } retention)
            {
                retention.Validate(); _editing = new(serializer, envelopeLifecycle);
                _retention = new(_identityLockScope, retention, _ => ValueTask.FromResult<IDisposable>(HistoryRead.Instance),
                    (_, _, _) => throw new InvalidOperationException("A File baseline must be archived before reading."));
            }
            _mayResolve = writeConflictStrategy is not null and not StrictTimestampMatchStrategy<TContract>;
            _revisions = new(_identityLockScope, serializer, envelopeLifecycle, writeConflictStrategy, _editing);
            var normalizedConcurrency = _storagePolicy.NormalizedConcurrency;
            _writeSemaphore = (normalizedConcurrency > 1 && _storagePolicy.UseWriterSemaphore)
                ? new SemaphoreSlim(normalizedConcurrency, normalizedConcurrency)
                : null;
        }

        public ValueTask<TContract> WriteAsync(
        TContract objectInstance,
        CancellationToken cancellationToken = default)
            => WriteCoreAsync(objectInstance, batchScope: null, cancellationToken);

        private async ValueTask<TContract> WriteCoreAsync(
            TContract objectInstance,
            CoalescingFileWriteCoordinator<TContract>.WriterBatchScope? batchScope,
            CancellationToken cancellationToken)
        {
            EnvelopeResolverScope.ThrowIfActive();
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

            var isNewStorageObject = objectInstance.Envelope is null
                || string.IsNullOrWhiteSpace(objectInstance.Envelope.EnvelopeCacheKey);
            var envelope = await _envelopeLifecycle
            .WrapAsync(objectInstance)
            .ConfigureAwait(false);

            var identityLease = isNewStorageObject
                ? await AcquireIdentitySemaphoreAsync(envelope, cancellationToken).ConfigureAwait(false)
                : null;
            var semaphore = _writeSemaphore;
            var ownsWriteSemaphore = false;

            try
            {
                if (semaphore is not null)
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    ownsWriteSemaphore = true;
                }

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
                var relative = Path.GetRelativePath(_identityLockScope, Path.GetFullPath(path));
                if (RetainedHistoryNamespace.ContainsRelative(relative) || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidStorageEnvelopeException("The file provider locator is outside the configured storage root.", envelope.EnvelopeCacheKey, path);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && _ensuredDirs.TryAdd(dir, 0))
                {
                    Directory.CreateDirectory(dir);
                }

                async ValueTask WriteProposalAsync(string proposalPath, CancellationToken token)
                {
                    if (_storagePolicy.UseSingleWriteBuffer)
                    {
                        // WriteThrough already directs the write through intermediate caches.
                        var payload = _serializer.Serialize(envelope);
                        CommittedWriteCapture<TContract>.Remember(envelope, payload, _serializer, _envelopeLifecycle);
                        using var handle = File.OpenHandle(
                            proposalPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            options: FileOptions.Asynchronous | FileOptions.SequentialScan |
                                (_storagePolicy.DurabilityMode == StorageDurabilityMode.Performance ? FileOptions.None : FileOptions.WriteThrough));
                        await RandomAccess.WriteAsync(handle, payload, 0, token).ConfigureAwait(false);
                    }
                    else
                    {
                        var fso = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan |
                                (_storagePolicy.DurabilityMode == StorageDurabilityMode.Performance ? FileOptions.None : FileOptions.WriteThrough),
                            BufferSize = _storagePolicy.NormalizedBufferSize
                        };

                        await using var stream = new FileStream(proposalPath, fso);
                        await _serializer.SerializeAsync(envelope, stream, token).ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                    }
                }

                var coordinator = _fileWriteCoordinator as IConditionalFileWriteCoordinator
                    ?? throw new NotSupportedException("File writes require conditional commit coordination.");
                TContract saved = null!;
                async ValueTask Prepare(string? currentPath, string stagingPath, CancellationToken token)
                {
                    var current = await ReadRevisionAsync(currentPath, token).ConfigureAwait(false);
                    envelope = await _revisions.PrepareAsync(envelope, isNewStorageObject, current,
                        ct => ReadPayloadAsync(path, ct), token).ConfigureAwait(false);
                    if (EnvelopeWriteOutcome.IsNoChange(envelope)) return;
                    await WriteProposalAsync(stagingPath, token).ConfigureAwait(false);
                    if (!_storagePolicy.UseSingleWriteBuffer && CommittedWriteCapture<TContract>.Enabled)
                        CommittedWriteCapture<TContract>.Remember(envelope, await File.ReadAllBytesAsync(stagingPath, token).ConfigureAwait(false), _serializer, _envelopeLifecycle);
                }
                void Publish()
                {
                    saved = _revisions.Publish(objectInstance, envelope);
                    UpdateEnvelopeCache(envelope, saved);
                }
                if (batchScope is null)
                    await coordinator.CommitEnvelopeAsync(path, false, Prepare, cancellationToken, Publish,
                        _mayResolve ? () => !EnvelopeWriteOutcome.IsNoChange(envelope) : null).ConfigureAwait(false);
                else
                    await batchScope.CommitEnvelopeAsync(path, false, Prepare, cancellationToken, Publish,
                        _mayResolve ? () => !EnvelopeWriteOutcome.IsNoChange(envelope) : null).ConfigureAwait(false);
                if (EnvelopeWriteOutcome.IsNoChange(envelope)) return _revisions.Publish(objectInstance, envelope);
                return saved;
            }
            finally
            {
                if (ownsWriteSemaphore) { semaphore!.Release(); }
                identityLease?.Dispose();
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
            var batchKeys = new HashSet<string>(StringComparer.Ordinal);
            var batchInstances = new HashSet<TContract>(ReferenceEqualityComparer.Instance);
            foreach (var item in materialized)
            {
                if (!batchInstances.Add(item) || (item.Envelope is { EnvelopeCacheKey.Length: > 0 } existing && !batchKeys.Add(existing.EnvelopeCacheKey)))
                    throw new InvalidOperationException("A file batch cannot contain duplicate instances or physical keys.");
            }
            var failures = new ConcurrentBag<StorageBulkOperationFailure>();
            var batchSize = Math.Min(_storagePolicy.NormalizedBulkWriteBatchSize, materialized.Count);
            var concurrency = Math.Min(_storagePolicy.NormalizedConcurrency, batchSize);

            try
            {
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

            }
            catch (OperationCanceledException exception)
            {
                throw new StorageBulkOperationCanceledException(CommittedItems(), exception, cancellationToken);
            }

            IReadOnlyDictionary<int, object> CommittedItems() => results
                .Select((item, index) => (item, index)).Where(x => x.item is not null)
                .ToDictionary(x => x.index, x => (object)x.item!);

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
                    orderedFailures,
                    CommittedItems());
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
            EnvelopeResolverScope.ThrowIfActive();
            CoalescingFileWriteCoordinator<TContract>.WriterBatchScope? acquiredScope = null;
            if (batchEnd - batchStart > 1
                && _storagePolicy.DurabilityMode != StorageDurabilityMode.Performance
                && _fileWriteCoordinator is CoalescingFileWriteCoordinator<TContract> coordinator)
            {
                try
                {
                    acquiredScope = await coordinator.AcquireWriterBatchScopeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    for (var index = batchStart; index < batchEnd; index++)
                    {
                        failures.Add(CreateFailure(index, materialized[index], exception));
                    }
                    return;
                }
            }

            await using var batchScope = acquiredScope;
            if (concurrency <= 1)
            {
                for (var index = batchStart; index < batchEnd; index++)
                {
                    await TryWriteBatchItemAsync(materialized, results, failures, index, batchScope, cancellationToken)
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
                workers[worker] = WriteBatchRangeAsync(
                    materialized,
                    results,
                    failures,
                    start,
                    end,
                    batchScope,
                    cancellationToken);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }

        private async Task WriteBatchRangeAsync(
            IReadOnlyList<TContract> materialized,
            TContract?[] results,
            ConcurrentBag<StorageBulkOperationFailure> failures,
            int start,
            int end,
            CoalescingFileWriteCoordinator<TContract>.WriterBatchScope? batchScope,
            CancellationToken cancellationToken)
        {
            for (var index = start; index < end; index++)
            {
                await TryWriteBatchItemAsync(materialized, results, failures, index, batchScope, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async ValueTask TryWriteBatchItemAsync(
            IReadOnlyList<TContract> materialized,
            TContract?[] results,
            ConcurrentBag<StorageBulkOperationFailure> failures,
            int index,
            CoalescingFileWriteCoordinator<TContract>.WriterBatchScope? batchScope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = materialized[index];
            try
            {
                results[index] = await WriteCoreAsync(instance, batchScope, cancellationToken).ConfigureAwait(false);
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
            var envelope = instance?.Envelope;
            return new StorageBulkOperationFailure(
                index,
                envelope?.EnvelopeCacheKey,
                envelope?.Location,
                exception);
        }

        private async ValueTask<IdentitySemaphoreLease?> AcquireIdentitySemaphoreAsync(
            IObjectEnvelope<TContract> envelope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                return null;
            }

            var logicalKey = EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey);
            var semaphoreKey = FileSystemPathIdentity.Normalize(
                _identityLockScope + "|" + typeof(TContract).FullName + "|" + logicalKey,
                _pathComparer);

            while (true)
            {
                var entry = _identitySemaphores.GetOrAdd(
                    semaphoreKey,
                    static _ => new IdentitySemaphoreEntry());
                var ownsReference = false;
                lock (entry.Gate)
                {
                    if (!entry.Retired)
                    {
                        entry.References++;
                        ownsReference = true;
                    }
                }

                if (!ownsReference)
                {
                    TryRemoveIdentitySemaphore(semaphoreKey, entry);
                    continue;
                }

                try
                {
                    await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return new IdentitySemaphoreLease(semaphoreKey, entry);
                }
                catch
                {
                    ReleaseIdentityReference(semaphoreKey, entry);
                    throw;
                }
            }
        }

        internal static int RetainedIdentitySemaphoreCount => _identitySemaphores.Count;

        private static void ReleaseIdentityReference(string key, IdentitySemaphoreEntry entry)
        {
            var retire = false;
            lock (entry.Gate)
            {
                entry.References--;
                if (entry.References == 0)
                {
                    entry.Retired = true;
                    retire = true;
                }
            }

            if (retire)
            {
                TryRemoveIdentitySemaphore(key, entry);
                entry.Semaphore.Dispose();
            }
        }

        private static void TryRemoveIdentitySemaphore(string key, IdentitySemaphoreEntry entry)
            => ((ICollection<KeyValuePair<string, IdentitySemaphoreEntry>>)_identitySemaphores)
                .Remove(new KeyValuePair<string, IdentitySemaphoreEntry>(key, entry));

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

        public async ValueTask<bool> DeleteAsync(TContract entity, CancellationToken cancellationToken = default)
        {
            EnvelopeResolverScope.ThrowIfActive();
            var envelope = entity.Envelope ?? throw new InvalidOperationException("An existing envelope is required.");
            var path = envelope.Location ?? throw new InvalidStorageEnvelopeException("The file provider locator is missing.", envelope.EnvelopeCacheKey, null);
            var relative = Path.GetRelativePath(_identityLockScope, Path.GetFullPath(path));
            if (RetainedHistoryNamespace.ContainsRelative(relative) || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidStorageEnvelopeException("The file provider locator is outside the configured storage root.", envelope.EnvelopeCacheKey, path);
            var coordinator = _fileWriteCoordinator as IConditionalFileWriteCoordinator
                ?? throw new NotSupportedException("File deletes require conditional commit coordination.");
            return await coordinator.CommitEnvelopeAsync(path, true, async (currentPath, staging, token) =>
            {
                var current = await ReadRevisionAsync(currentPath, token).ConfigureAwait(false);
                await _revisions.PrepareAsync(envelope, false, current, ct => ReadPayloadAsync(path, ct), token, deleting: true).ConfigureAwait(false);
            }, cancellationToken, () => EnvelopeCommitOrder.Stamp(envelope)).ConfigureAwait(false);
        }

        private async ValueTask<long?> ReadRevisionAsync(string? path, CancellationToken token)
        {
            if (path is null) return null;
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ((IRevisionObjectSerializer<TContract>)_serializer).ReadRevisionAsync(source, token).ConfigureAwait(false);
        }

        private static async ValueTask<byte[]?> ReadPayloadAsync(string path, CancellationToken token)
        {
            try
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var bytes = new byte[checked((int)source.Length)];
                await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                return bytes;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private void UpdateEnvelopeCache(IObjectEnvelope<TContract> envelope, TContract? instance)
        {
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                return;
            }

            var cacheMode = StorageObjectCachePolicy.ResolveObjectCacheMode(envelope, _storagePolicy);
            if (cacheMode is not StorageObjectCacheMode.MetadataOnly)
            {
                StorageObjectCachePolicy.StoreObjectReference(envelope, instance, _storagePolicy);
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
                cached.ImplementationTypeName = envelope.ImplementationTypeName;
                cached.CacheObjectInstance = envelope.CacheObjectInstance;
                cached.EncryptedFields = envelope.EncryptedFields;
                cached.ExtensionMetadata = envelope.ExtensionMetadata?.ToArray();
                cached.RevisionTimestampUtcMicroseconds = envelope.RevisionTimestampUtcMicroseconds;
                StorageObjectCachePolicy.StoreObjectReference(cached, envelope.ObjectInstance, _storagePolicy);
                return;
            }

            var skeleton = new FileObjectEnvelope<TContract>(_contractConfig)
            {
                EnvelopeCacheKey = envelope.EnvelopeCacheKey,
                VersionTag = envelope.VersionTag,
                Location = envelope.Location,
                ImplementationTypeName = envelope.ImplementationTypeName,
                CacheObjectInstance = envelope.CacheObjectInstance,
                EncryptedFields = envelope.EncryptedFields,
                ExtensionMetadata = envelope.ExtensionMetadata?.ToArray(),
                RevisionTimestampUtcMicroseconds = envelope.RevisionTimestampUtcMicroseconds,
                ObjectInstance = null,
                State = envelope.State
            };
            StorageObjectCachePolicy.StoreObjectReference(skeleton, envelope.ObjectInstance, _storagePolicy);
            _envelopes.AddOrUpdate(
                envelope.EnvelopeCacheKey,
                EnvelopeCacheKey.ToLogicalKey(envelope.EnvelopeCacheKey),
                skeleton);
        }

        private sealed class IdentitySemaphoreEntry
        {
            public object Gate { get; } = new();
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int References { get; set; }
            public bool Retired { get; set; }
        }

        private sealed class IdentitySemaphoreLease : IDisposable
        {
            private readonly string _key;
            private readonly IdentitySemaphoreEntry _entry;
            private int _disposed;

            public IdentitySemaphoreLease(string key, IdentitySemaphoreEntry entry)
            {
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                _entry.Semaphore.Release();
                ReleaseIdentityReference(_key, _entry);
            }
        }

    }
}
