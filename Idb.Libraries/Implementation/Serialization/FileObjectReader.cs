using Idb.Libraries.Abstraction.Services;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Serialization
{
    public class FileObjectReader<TContract> : IObjectReader<TContract, string>
        where TContract : class, IRepoDto<TContract, string>
    {
        private IObjectSerializer<TContract, string> _serializer;
        private readonly IEnvelopeLifecycle<TContract, string> _envelopeLifecycle;

        public FileObjectReader(IObjectSerializer<TContract, string> serializer,
            IEnvelopeLifecycle<TContract, string> envelopeLifecycle)
        {
            _serializer = serializer;
            _envelopeLifecycle = envelopeLifecycle ?? throw new ArgumentNullException(nameof(envelopeLifecycle));
        }

        public IAsyncEnumerable<TContract> ReadAsync(string rootLocationOfContract, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public async Task<TContract> ReadAsync(FileInfo file, CancellationToken cancellationToken = default)
        {
            var payload = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            var envelope = _serializer.Deserialize(payload);
            return _envelopeLifecycle.Unwrap(envelope);
        }
    }
}
