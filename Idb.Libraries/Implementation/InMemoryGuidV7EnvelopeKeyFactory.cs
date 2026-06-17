using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

public sealed class InMemoryGuidV7EnvelopeKeyFactory<TContract> : IEnvelopeKeyFactory<TContract, Guid>
    where TContract : class, IStoredContract<TContract>
{
    public Guid CreateKey(TContract instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Guid.CreateVersion7();
    }
}
