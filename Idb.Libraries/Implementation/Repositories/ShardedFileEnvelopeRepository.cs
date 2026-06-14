using Idb.Libraries.Abstraction.Services.Cache;
using Idb.Libraries.Abstraction.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Sharded file-system backed envelope repository.
/// </summary>
public sealed class ShardedFileEnvelopeRepository<TContract> : FileEnvelopeRepositoryBase<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public ShardedFileEnvelopeRepository(
        IDbContractConfigDto<TContract> contractConfig,
        IServiceProvider serviceProvider,
        IReaderAndWriter<TContract> readerAndWriter,
        IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>> envelopes)
        : base(contractConfig, serviceProvider, readerAndWriter, envelopes, precreateShardFolders: true)
    {
    }
}
