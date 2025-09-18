using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Repository
{
    public interface IRepository
    {
        ValueTask<ObjectWriteResult> SaveAsync<TContract>(
            TContract entity,
            string? id = null,
            CancellationToken cancellationToken = default);

        ValueTask<ObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);

        ValueTask<bool> ExistsAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);
    }
}
