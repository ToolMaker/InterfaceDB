using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
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

        public FileObjectWriter(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IDbContractConfigDto<TContract> contractConfig,
            IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
            _storagePolicy = contractConfig.StoragePolicy ?? StoragePolicy.Default;
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
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && _ensuredDirs.TryAdd(dir, 0))
                {
                    Directory.CreateDirectory(dir);
                }

                if (_storagePolicy.UseSingleWriteBuffer)
                {
                    // Pre-serialize to one buffer and write once; avoids per-file FileStream internal buffer allocations
                    var payload = _serializer.Serialize(envelope);
                    using var handle = File.OpenHandle(path!, FileMode.Create, FileAccess.Write, FileShare.None,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await RandomAccess.WriteAsync(handle, payload, 0, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Stream payload directly to disk to avoid large intermediate arrays
                    var fso = new FileStreamOptions
                    {
                        Mode = FileMode.Create,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        BufferSize = _storagePolicy.NormalizedBufferSize
                    };

                    using (var stream = new FileStream(path, fso))
                    {
                        await _serializer.SerializeAsync(envelope, stream, cancellationToken).ConfigureAwait(false);
                        // Rely on FileStream.Dispose to flush buffers; skipping per-write flush improves throughput under load.
                    }
                }

                // Update envelope cache after successful persistence (policy-driven)
                if (_storagePolicy.CacheEnvelopeOnWrite && !string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
                {
                    if (_storagePolicy.KeepObjectInstanceInCacheOnWrite)
                    {
                        _envelopes[envelope.EnvelopeCacheKey] = envelope;
                    }
                    else
                    {
                        // Cache a lightweight skeleton to avoid retaining the full object graph in memory
                        var skeleton = new FileObjectEnvelope<TContract>(_contractConfig)
                        {
                            EnvelopeCacheKey = envelope.EnvelopeCacheKey,
                            VersionTag = envelope.VersionTag,
                            Location = envelope.Location,
                            ObjectInstance = null
                        };
                        _envelopes[envelope.EnvelopeCacheKey] = skeleton;
                    }
                }

                return _envelopeLifecycle.Unwrap(envelope);
            }
            finally
            {
                semaphore?.Release();
            }
        }
    }
}
