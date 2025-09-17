using System;
using System.Threading;
using System.Threading.Tasks;
using TransactionManager.Abstraction.Serialization;
using TransactionManager.Dtos;

namespace TransactionManager.Repository
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
            short schemaVersion = 1,
            CancellationToken cancellationToken = default)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            return _writer.WriteAsync(entity, id, schemaVersion, cancellationToken);
        }

        public ValueTask<ObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default)
        {
            return _reader.ReadAsync<TContract>(id, schemaVersion, cancellationToken);
        }

        public ValueTask<bool> ExistsAsync<TContract>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default)
        {
            return _reader.ExistsAsync<TContract>(id, schemaVersion, cancellationToken);
        }
    }
}
