using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Indexes;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectWriter<TContract> : IObjectWriter<TContract, string>
        where TContract : class, IRepoDto<TContract, string>
    {
        private IObjectSerializer<TContract, string> _serializer;
        private readonly IEnvelopeLifecycle<TContract, string> _envelopeLifecycle;
        private readonly SemaphoreSlim? _writeSemaphore;
        private readonly StoragePolicy _storagePolicy;

        public FileObjectWriter(IObjectSerializer<TContract, string> serializer,
            IEnvelopeLifecycle<TContract, string> envelopeLifecycle,
            IDbContractConfigDto<TContract, string> contractConfig)
        {
            _serializer = serializer;
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
            _storagePolicy = contractConfig.StoragePolicy ?? StoragePolicy.Default;
            var normalizedConcurrency = _storagePolicy.NormalizedConcurrency;
            _writeSemaphore = normalizedConcurrency > 1 ? new SemaphoreSlim(normalizedConcurrency, normalizedConcurrency) : null;
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
                var payload = _serializer.Serialize(envelope);

                FileInfo targetFileInfo = new FileInfo(envelope.Location);
                if (targetFileInfo.Directory is not null && !targetFileInfo.Directory.Exists)
                {
                    targetFileInfo.Directory.Create();
                }

                await File.WriteAllBytesAsync(targetFileInfo.FullName, payload, cancellationToken).ConfigureAwait(false);

                return _envelopeLifecycle.Unwrap(envelope);
            }
            finally
            {
                semaphore?.Release();
            }
        }
    }
}
