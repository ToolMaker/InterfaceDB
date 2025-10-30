using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstraction.Services
{
    public interface IEnvelopeLifecycle<TContract, TLocationType> where TContract : class, IRepoDto<TContract, TLocationType>
    {
        ValueTask<IObjectEnvelope<TContract, TLocationType>> WrapAsync(
        TContract entity
        //Func<TContract, ValueTask<IObjectEnvelope<TContract, TLocationType>>> action
            );

        TContract Unwrap(IObjectEnvelope<TContract, TLocationType> envelope);
    }
}