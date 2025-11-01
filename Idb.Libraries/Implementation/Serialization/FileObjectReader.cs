using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectReader<TContract> : IObjectReader<TContract, string>
        where TContract : class, IRepoDto<TContract, string>
    {
        private IObjectSerializer<TContract, string> _serializer;
        private readonly IEnvelopeLifecycle<TContract, string> _envelopeLifecycle;
        private readonly IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract, string>> _envelopes;
        private readonly IDbContractConfigDto<TContract, string> _contractConfig;

        public FileObjectReader(IObjectSerializer<TContract, string> serializer,
            IEnvelopeLifecycle<TContract, string> envelopeLifecycle,
            IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract, string>> envelopes,
            IDbContractConfigDto<TContract, string> contractConfig)
        {
            _serializer = serializer;
            _envelopes = envelopes ?? throw new ArgumentNullException(nameof(envelopes));
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _contractConfig = contractConfig ?? throw new ArgumentNullException(nameof(contractConfig));
        }

        public IAsyncEnumerable<TContract> ReadAsync(string rootLocationOfContract, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
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

            IObjectEnvelope<TContract, string> envelope;
            using (var stream = new FileStream(file.FullName, fso2))
            {
                envelope = await _serializer.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            return _envelopeLifecycle.Unwrap(envelope);
        }
    }
}
