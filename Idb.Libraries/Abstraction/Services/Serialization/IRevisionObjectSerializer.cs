using Idb.Libraries.Abstractions.Dtos;

namespace Idb.Libraries.Abstractions.Services.Serialization;

/// <summary>Explicit opt-in to round-tripping revisions and reading committed metadata without materializing an entity.</summary>
public interface IRevisionObjectSerializer<TContract> : IObjectSerializer<TContract>
    where TContract : class, IStoredContract<TContract>
{
    long ReadRevision(ReadOnlyMemory<byte> payload);
    ValueTask<long> ReadRevisionAsync(Stream source, CancellationToken cancellationToken = default);
}
