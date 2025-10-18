using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    public interface IObjectWriter
    {
        ValueTask<IObjectWriteResult<TContract>> WriteAsync<TContract>(
            TContract obj,
            IObjectEnvelope<TContract> envelope,
            CancellationToken cancellationToken = default) 
            where TContract : class;
    }
}

