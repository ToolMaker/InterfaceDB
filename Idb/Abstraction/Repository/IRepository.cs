using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Abstraction.Repository
{
    public interface IRepository
    {
        ValueTask<IObjectWriteResult> SaveAsync<TContract>(
            TContract entity,
            string? id = null,
            CancellationToken cancellationToken = default);

        ValueTask<IObjectReadResult<TContract>> LoadAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);

        ValueTask<bool> ExistsAsync<TContract>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);
    }
}
