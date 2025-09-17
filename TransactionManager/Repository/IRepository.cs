using System.Threading;
using System.Threading.Tasks;
using TransactionManager.Dtos;

namespace TransactionManager.Repository
{
    public interface IRepository
    {
        ValueTask<ObjectWriteResult> SaveAsync<TContract>(
            TContract entity,
            string? id = null,
            short schemaVersion = 1,
            CancellationToken cancellationToken = default);

        ValueTask<ObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default);

        ValueTask<bool> ExistsAsync<TContract>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default);
    }
}
