using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Options;
using Idb.Libraries.Abstractions.Services.Indexes;
using Idb.Libraries.Abstractions.Services.PubSub;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Services.Maintenance;

namespace Idb.Storage.SegmentCore;

/// <summary>
/// Non-owning view of the provider singleton. DI aliases must not return the disposable storage
/// instance directly because each alias would otherwise become an independent disposal owner.
/// </summary>
internal sealed class SegmentCoreRepositoryFacade<TContract> :
    IEnvelopeRepository<TContract>,
    IPhysicalEnvelopeLookup,
    ICachedEnvelopeSource<TContract>,
    IBatchedEnvelopeRepository<TContract>,
    ICommittedEnvelopeSnapshotSource<TContract>,
    IRetainedVersionStore<TContract>,
    IEnvelopeRepositoryInitializationStream<TContract>,
    IPersistentSearchIndexCatalogRepository,
    ISegmentCoreRepositoryMaintenance<TContract>,
    ICompactionMaintenance<TContract>
    where TContract : class, IStoredContract<TContract>
{
    private readonly IEnvelopeRepository<TContract> _repository;
    private readonly IBatchedEnvelopeRepository<TContract> _batchRepository;
    private readonly IEnvelopeRepositoryInitializationStream<TContract> _initializationStream;
    private readonly IPersistentSearchIndexCatalogRepository _persistentCatalog;
    private readonly ISegmentCoreRepositoryMaintenance<TContract> _maintenance;

    public SegmentCoreRepositoryFacade(IEnvelopeRepository<TContract> repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _batchRepository = repository as IBatchedEnvelopeRepository<TContract>
            ?? throw new InvalidOperationException("SegmentCore repository has no batch read capability.");
        _initializationStream = repository as IEnvelopeRepositoryInitializationStream<TContract>
            ?? throw new InvalidOperationException("SegmentCore repository has no initialization stream.");
        _persistentCatalog = repository as IPersistentSearchIndexCatalogRepository
            ?? throw new InvalidOperationException("SegmentCore repository has no persistent index catalog.");
        _maintenance = repository as ISegmentCoreRepositoryMaintenance<TContract>
            ?? throw new InvalidOperationException("SegmentCore repository has no maintenance capability.");
    }

    public IReaderAndWriter<TContract> ReaderAndWriter => _repository.ReaderAndWriter;
    public RetainedVersionCapability RetainedVersions => ((IRetainedVersionStore<TContract>)_repository).RetainedVersions;
    public RetainedVersionStatistics RetentionStatistics => ((IRetainedVersionStore<TContract>)_repository).RetentionStatistics;
    public ValueTask<IRevisionEditSession<TContract>> BeginEditAsync(string physicalKey,
        Func<TContract, CancellationToken, ValueTask<TContract>> saveAsync, CancellationToken cancellationToken = default)
        => ((IRetainedVersionStore<TContract>)_repository).BeginEditAsync(physicalKey, saveAsync, cancellationToken);

    public ValueTask<ICommittedEnvelopeSnapshot<TContract>?> CaptureCommittedAsync(
        IObjectEnvelope<TContract> identity, int maximumBytes, CancellationToken cancellationToken = default) =>
        ((ICommittedEnvelopeSnapshotSource<TContract>)_repository).CaptureCommittedAsync(identity, maximumBytes, cancellationToken);

    public string SearchIndexCatalogDirectory => _persistentCatalog.SearchIndexCatalogDirectory;

    public bool PersistentSearchIndexCatalogEnabled => _persistentCatalog.PersistentSearchIndexCatalogEnabled;

    public StoragePolicy StoragePolicy => _maintenance.StoragePolicy;

    public SegmentCoreRepositoryOptions Options => _maintenance.Options;

    public SegmentCoreProviderStatus Status => _maintenance.Status;

    public IMaintenanceSubscription SubscribeMaintenance(
        Func<MaintenanceNotification, CancellationToken, ValueTask> handler, CancellationToken lifetime = default) =>
        ((ICompactionMaintenance<TContract>)_repository).SubscribeMaintenance(handler, lifetime);

    public ValueTask<CompactionEstimate> EstimateCompactionAsync(CancellationToken cancellationToken = default) =>
        ((ICompactionMaintenance<TContract>)_repository).EstimateCompactionAsync(cancellationToken);

    public ValueTask<CompactionRunResult> RunCompactionAsync(bool manual = false, CancellationToken cancellationToken = default) =>
        ((ICompactionMaintenance<TContract>)_repository).RunCompactionAsync(manual, cancellationToken);

    public IEnumerable<KeyValuePair<string, TContract>> EnumerateCachedObjects()
        => ((ICachedEnvelopeSource<TContract>)_repository).EnumerateCachedObjects();

    public ValueTask<bool> ExistsPhysicalAsync(string physicalKey, CancellationToken cancellationToken = default)
        => ((IPhysicalEnvelopeLookup)_repository).ExistsPhysicalAsync(physicalKey, cancellationToken);

    public bool Exists(string storageKey) => _repository.Exists(storageKey);

    public bool TryGet(
        string storageKey,
        out IReadOnlyList<IObjectEnvelope<TContract>> envelopes) =>
        _repository.TryGet(storageKey, out envelopes);

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        _repository.InitializeAsync(cancellationToken);

    public ValueTask InitializeForSearchIndexCatalogAsync(CancellationToken cancellationToken = default) =>
        _persistentCatalog.InitializeForSearchIndexCatalogAsync(cancellationToken);

    public ValueTask<SearchIndexStorageIdentity> GetSearchIndexStorageIdentityAsync(
        CancellationToken cancellationToken = default) =>
        _persistentCatalog.GetSearchIndexStorageIdentityAsync(cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> InitializeAndEnumerateAsync(
        CancellationToken cancellationToken = default) =>
        _initializationStream.InitializeAndEnumerateAsync(cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> GetAsync(
        string storageKey,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(storageKey, cancellationToken);

    public ValueTask<IObjectEnvelope<TContract>?> GetPhysicalAsync(
        string physicalStorageKey,
        CancellationToken cancellationToken = default) =>
        _repository.GetPhysicalAsync(physicalStorageKey, cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateAsync(
        CancellationToken cancellationToken = default) =>
        _repository.EnumerateAsync(cancellationToken);

    public ValueTask<IReadOnlyList<IObjectEnvelope<TContract>>> GetPhysicalManyAsync(
        IReadOnlyList<string> physicalStorageKeys,
        CancellationToken cancellationToken = default) =>
        _batchRepository.GetPhysicalManyAsync(physicalStorageKeys, cancellationToken);

    public IAsyncEnumerable<IObjectEnvelope<TContract>> EnumerateByImplementationTypeAsync<TImplementation>(
        CancellationToken cancellationToken = default)
        where TImplementation : class, TContract =>
        _repository.EnumerateByImplementationTypeAsync<TImplementation>(cancellationToken);

    public ValueTask<TContract> WriteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default) =>
        _repository.WriteAsync(contractInstance, cancellationToken);

    public ValueTask<IReadOnlyList<TContract>> WriteManyAsync(
        IEnumerable<TContract> contractInstances,
        CancellationToken cancellationToken = default) =>
        _repository.WriteManyAsync(contractInstances, cancellationToken);

    public ValueTask<bool> DeleteAsync(
        TContract contractInstance,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(contractInstance, cancellationToken);

    public IContractSubscription SubscribeToAdded(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToAdded(handler, keepAlive);

    public IContractSubscription SubscribeToRemoved(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToRemoved(handler, keepAlive);

    public IContractSubscription SubscribeToStored(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToStored(handler, keepAlive);

    public IContractSubscription SubscribeToDeleted(
        Func<IObjectEnvelope<TContract>, ValueTask> handler,
        Func<ValueTask<bool>>? keepAlive = null) =>
        _repository.SubscribeToDeleted(handler, keepAlive);

    public ValueTask ContractAdded(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractAdded(@event, cancellationToken);

    public ValueTask ContractRemoved(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractRemoved(@event, cancellationToken);

    public ValueTask ContractStored(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractStored(@event, cancellationToken);

    public ValueTask ContractDeleted(
        IObjectEnvelope<TContract> @event,
        CancellationToken cancellationToken = default) =>
        _repository.ContractDeleted(@event, cancellationToken);

    public ValueTask<SegmentCoreCheckpointResult> CheckpointAsync(
        CancellationToken cancellationToken = default) =>
        _maintenance.CheckpointAsync(cancellationToken);

    public ValueTask<SegmentCoreCompactionResult> CompactAsync(
        CancellationToken cancellationToken = default) =>
        _maintenance.CompactAsync(cancellationToken);

    public ValueTask<SegmentCoreIntegrityResult> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default) =>
        _maintenance.VerifyIntegrityAsync(cancellationToken);

    public ValueTask<SegmentCoreBackupResult> BackupAsync(
        string destinationDirectory,
        SegmentCoreBackupOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _maintenance.BackupAsync(destinationDirectory, options, cancellationToken);

    public ValueTask<SegmentCoreMigrationResult> MigrateFromAsync(
        string sourceStoreDirectory,
        string sourceVersion,
        SegmentCoreMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _maintenance.MigrateFromAsync(sourceStoreDirectory, sourceVersion, options, cancellationToken);
}
