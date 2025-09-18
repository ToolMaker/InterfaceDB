using TransactionManager.Implementation.Dtos;

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
