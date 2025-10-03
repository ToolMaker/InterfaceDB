using TransactionManager.Abstraction.Dtos;

namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectWriter
    {
        ValueTask<IObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            CancellationToken cancellationToken = default);
    }
}
