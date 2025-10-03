using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Serialization
{
    public interface IObjectWriter
    {
        ValueTask<IObjectWriteResult> WriteAsync<T>(
            T obj,
            string? id = null,
            CancellationToken cancellationToken = default);
    }
}
