using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    public interface IObjectWriter
    {
        ValueTask<IObjectWriteResult> WriteAsync<T>(
            T obj,
            IObjectEnvelope? envelope = null,
            string? id = null,
            CancellationToken cancellationToken = default) where T : class;
    }
}

