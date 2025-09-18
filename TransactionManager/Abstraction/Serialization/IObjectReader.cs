using System.Threading;
using System.Threading.Tasks;
using TransactionManager.Implementation.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectReader
    {
        ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);

        ValueTask<ObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);
    }
}
