using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;
using System.Runtime.ExceptionServices;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Minimal registration for an already retired installation. It cannot create
/// a new database, abandon an unfinished migration or infer a contract upgrade.</summary>
public sealed class RetiredStorageMigration<T>(string migrationId, IStorageMigrationDestination<T> destination,
    IStorageMigrationStateStore stateStore) : IStorageMigration<T>, IStorageMigrationCapabilities,
    IStorageMigrationServiceAccess, IStorageMigrationProgress, IRepositoryDataAvailability where T : class, IStoredContract<T>
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IRepository<T>? _ready;
    private ExceptionDispatchInfo? _failure;
    private StorageMigrationProgress _progress = new(StorageMigrationPhase.NotStarted, 0, 0, 0);
    public StorageMigrationProgress Progress => Volatile.Read(ref _progress);
    public bool IsDataComplete => Volatile.Read(ref _ready) is not null;
    public ContractRepositoryType RepositoryType => destination.Repository.RepositoryType;
    public string? RunningVersion => destination.ContractVersion;
    public StorageDurabilityMode CommitDurability => Capability<IEnvelopeMetadataRepository<T>>().CommitDurability;
    public RetainedVersionCapability RetainedVersions => Capability<IRevisionEditingRepository<T>>().RetainedVersions;
    public RetainedVersionStatistics RetentionStatistics => Capability<IRevisionEditingRepository<T>>().RetentionStatistics;
    private TC Capability<TC>() where TC : class => destination.Repository as TC
        ?? throw new NotSupportedException($"Destination does not provide {typeof(TC).Name}.");
    public bool OwnsServiceRegistration(string databaseKey)
        => string.Equals(destination.ServiceRegistrationKey, databaseKey, StringComparison.Ordinal);
    public object GetReadyService(Type serviceType) => Volatile.Read(ref _ready) is not null
        ? destination.GetService(serviceType) ?? throw new NotSupportedException("Destination does not supply this service.")
        : throw new InvalidOperationException("Initialize the retired repository before resolving infrastructure services.");
    public async ValueTask RetryAsync(CancellationToken cancellationToken = default) => await OpenAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<IRepository<T>> OpenAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var providerStarted = false;
        try
        {
            _failure?.Throw();
            if (_ready is not null) return _ready;
            await using var lease = await stateStore.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            var state = await stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (state is not { Format: 2, Completed: true, Retired: true } || state.MigrationId != migrationId
                || state.TargetVersion != destination.ContractVersion || state.DestinationInstance is null
                || state.PendingKeys.Length != 0 || state.CleanupRecords is not null)
                throw new InvalidOperationException("This installation has no matching retired completion marker. Keep its migration registration until all pending work completes.");
            providerStarted = true;
            await destination.Binding.ValidateAsync(state.Plan, state.DestinationInstance, cancellationToken).ConfigureAwait(false);
            await destination.ValidateCompletedDestinationAsync(cancellationToken).ConfigureAwait(false);
            await destination.Repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _progress, new(StorageMigrationPhase.Complete, state.RecordCount, state.VerifiedRecords, 0)
            { Retirement = StorageMigrationRetirement.Retired });
            return _ready = destination.Repository;
        }
        catch (Exception error) when (providerStarted) { _failure ??= ExceptionDispatchInfo.Capture(error); throw; }
        finally { _gate.Release(); }
    }
}
