using System.Threading;
using System.Threading.Tasks;
using TransactionManager.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectReader
    {
        ValueTask<bool> ExistsAsync<T>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default);

        ValueTask<ObjectReadResult<T>> ReadAsync<T>(
            string id,
            short? schemaVersion = null,
            CancellationToken cancellationToken = default);
    }
}
