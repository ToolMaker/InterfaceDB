using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Serialization
{
    public interface IObjectReader
    {
        ValueTask<bool> ExistsAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);

        ValueTask<IObjectReadResult<T>> ReadAsync<T>(
            string id,
            string? versionTag = null,
            CancellationToken cancellationToken = default);
    }
}
