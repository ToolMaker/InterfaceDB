using Idb.Libraries.Abstractions.Dtos;
namespace Idb.Libraries.Abstractions.Services.Serialization;

public interface IEnvelopeDeleteWriter<TContract> where TContract : class, IStoredContract<TContract>
{
    ValueTask<bool> DeleteAsync(TContract entity, CancellationToken cancellationToken = default);
}
