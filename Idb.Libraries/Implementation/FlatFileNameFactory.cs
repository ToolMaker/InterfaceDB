using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;

public class FlatFileNameFactory<TContract> :
    FlatFileStorageIdentityFactory<TContract, Guid>
    where TContract : class, IStoredContract<TContract>
{
    public FlatFileNameFactory(IDbContractConfigDto<TContract> config)
        : base(config, new InMemoryGuidV7EnvelopeKeyFactory<TContract>())
    {
    }
}
