using TransactionManager.Abstraction.Repository;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Implementation.Repository
{
    public sealed class FileSystemRepository : IRepository
    {
        private readonly IObjectWriter _writer;
        private readonly IObjectReader _reader;

        public FileSystemRepository(IObjectWriter writer, IObjectReader reader)
        {
            _writer = writer;
            _reader = reader;
        }

        public ValueTask<ObjectWriteResult> SaveAsync<TContract>(
            TContract entity,
            string? id = null,
            CancellationToken cancellationToken = default)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            EnsureContractIsInterface<TContract>();

            return _writer.WriteAsync(entity, id, cancellationToken);
        }

        public ValueTask<ObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            EnsureContractIsInterface<TContract>();
            return _reader.ReadAsync<TContract>(id, versionTag, cancellationToken);
        }

        public ValueTask<bool> ExistsAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default)
        {
            EnsureContractIsInterface<TContract>();
            return _reader.ExistsAsync<TContract>(id, versionTag, cancellationToken);
        }

        private static void EnsureContractIsInterface<TContract>()
        {
            var contractType = typeof(TContract);
            if (!contractType.IsInterface)
            {
                throw new InvalidOperationException($"Storage contract '{contractType.FullName}' must be an interface marked with [StorageScope].");
            }
        }
    }
}
