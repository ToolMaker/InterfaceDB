using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization
{
    public interface IObjectReader<TContract>
    {
        ValueTask<IObjectReadResult<TContract>> ReadAsync<TContract>(
            CancellationToken cancellationToken = default)
            where TContract : class;
    }
}

