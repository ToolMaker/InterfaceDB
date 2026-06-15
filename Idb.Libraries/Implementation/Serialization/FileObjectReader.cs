using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
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

        public FileObjectReader(IObjectSerializer<TContract> serializer,
            IEnvelopeLifecycle<TContract> envelopeLifecycle,
            IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes,
            IDbContractConfigDto<TContract> contractConfig)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        }

        public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            // Try cache by conventional key (file name without extension)
            var candidateKey = Path.GetFileNameWithoutExtension(file.Name);
            if (!string.IsNullOrWhiteSpace(candidateKey) && _envelopes.TryGetValue(candidateKey, out var cached) && cached is not null)
            {
                if (cached.ObjectInstance is not null)
                {
                    return _envelopeLifecycle.Unwrap(cached);
                }

                // Hydrate the cached envelope's ObjectInstance from disk without replacing the envelope instance
                var fso = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = _contractConfig.StoragePolicy?.NormalizedBufferSize ?? StoragePolicy.DefaultBufferSize
                };

                using (var stream = new FileStream(file.FullName, fso))
                {
                    var deserialized = await _serializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
                    cached.ObjectInstance = deserialized.ObjectInstance;
                }
                return _envelopeLifecycle.Unwrap(cached);
            }

            // Fallback: no cache entry, hydrate directly
            var fso2 = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = _contractConfig.StoragePolicy?.NormalizedBufferSize ?? StoragePolicy.DefaultBufferSize
            };

            IObjectEnvelope<TContract> envelope;
            using (var stream = new FileStream(file.FullName, fso2))
            {
                envelope = await _serializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            envelope.Location = file.FullName;
            if (string.IsNullOrWhiteSpace(envelope.EnvelopeCacheKey))
            {
                envelope.EnvelopeCacheKey = candidateKey;
            }

            return _envelopeLifecycle.Unwrap(envelope);
        }
    }
}
