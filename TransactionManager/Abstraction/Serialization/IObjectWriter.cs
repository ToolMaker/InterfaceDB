using System.Threading;
using System.Threading.Tasks;
using TransactionManager.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectWriter
    {
        ValueTask<ObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            CancellationToken cancellationToken = default);
    }
}
