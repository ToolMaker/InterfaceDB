using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstraction.Services
{
    public interface IEnvelopeLifecycle<TContract> where TContract : class, IStoredContract<TContract>
    {
        ValueTask<IObjectEnvelope<TContract>> WrapAsync(
        TContract entity
        //Func<TContract, ValueTask<IObjectEnvelope<TContractType>>> action
            );

        TContract Unwrap(IObjectEnvelope<TContract> envelope);
    }
}
