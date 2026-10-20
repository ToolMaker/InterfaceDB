using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Migrations;

namespace Idb.Libraries.Implementation.Repositories;

/// <summary>
/// Flat file-system backed envelope repository.
/// </summary>
public sealed class FileEnvelopeRepository<TContract> : FileEnvelopeRepositoryBase<TContract>
    where TContract : class, IStoredContract<TContract>
{
    public FileEnvelopeRepository(
        IDbContractConfigDto<TContract> contractConfig,
        IReaderAndWriter<TContract> readerAndWriter,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
        IFileWriteCoordinator fileWriteCoordinator,
        StoragePolicy storagePolicy,
        Func<IObjectEnvelope<TContract>> envelopeFactory,
        IMigrationStrategy<TContract>? migrationStrategy = null,
        IEnvelopeStorageChangeSource<TContract>? storageChangeSource = null,
        IEnvelopeStorageChangeHandler<TContract>? storageChangeHandler = null)
        : this(contractConfig, readerAndWriter, envelopes, fileWriteCoordinator, storagePolicy, envelopeFactory, migrationStrategy, storageChangeSource, storageChangeHandler, null)
    {
    }

    public FileEnvelopeRepository(
        IDbContractConfigDto<TContract> contractConfig,
        IReaderAndWriter<TContract> readerAndWriter,
        IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>> envelopes,
        IFileWriteCoordinator fileWriteCoordinator,
        StoragePolicy storagePolicy,
        Func<IObjectEnvelope<TContract>> envelopeFactory,
        IMigrationStrategy<TContract>? migrationStrategy,
        IEnvelopeStorageChangeSource<TContract>? storageChangeSource,
        IEnvelopeStorageChangeHandler<TContract>? storageChangeHandler,
        IEnvelopeStorageIdentityFactory<TContract>? storageIdentityFactory)
        : base(
            contractConfig,
            readerAndWriter,
            envelopes,
            fileWriteCoordinator,
            storagePolicy,
            envelopeFactory,
            migrationStrategy,
            storageChangeSource,
            storageChangeHandler,
            storageIdentityFactory)
    {
    }
}
