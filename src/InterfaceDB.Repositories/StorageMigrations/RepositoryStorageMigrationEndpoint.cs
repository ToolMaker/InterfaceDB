using System.Runtime.CompilerServices;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Cache;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Committed read adapter; source and destination responsibilities compose this independently.</summary>
public sealed class RepositoryStorageMigrationEndpoint<T>(string identity, string contractVersion, string storageDirectory,
    IRepository<T> repository, IEnvelopeRepository<T> envelopes,
    IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<T>> catalog,
    ICommittedEnvelopeSnapshotSource<T> snapshots,
    string? serviceRegistrationKey,
    ICommittedEnvelopeInventory<T>? inventory = null,
    Func<Type, object?>? resolveService = null) : IStorageMigrationEndpoint<T>
    where T : class, IStoredContract<T>
{
    public string Identity { get; } = identity;
    public string ContractVersion { get; } = contractVersion;
    public string? StorageDirectory { get; } = Path.GetFullPath(storageDirectory);
    public string? ServiceRegistrationKey => serviceRegistrationKey;
    public IRepository<T> Repository => repository;
    public object? GetService(Type type) => resolveService?.Invoke(type);
    private Dictionary<string, IObjectEnvelope<T>>? _snapshotIdentities;

    public async ValueTask InitializeSnapshotAsync(CancellationToken cancellationToken)
    {
        if (inventory is null) { await envelopes.InitializeAsync(cancellationToken).ConfigureAwait(false); return; }
        var identities = new Dictionary<string, IObjectEnvelope<T>>(StringComparer.Ordinal);
        await foreach (var identity in inventory.EnumerateCommittedIdentitiesAsync(cancellationToken).ConfigureAwait(false))
            if (!identities.TryAdd(identity.EnvelopeCacheKey, identity))
                throw new InvalidDataException("Source contains duplicate physical keys.");
        _snapshotIdentities = identities;
    }

    public async IAsyncEnumerable<StorageMigrationRecord<T>> ReadAllAsync(int maximumBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Enumerate identities, not materialized reads: reads can rotate old encryption keys and write the source.
        foreach (var key in _snapshotIdentities?.Keys.ToArray() ?? catalog.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await ReadAsync(key, maximumBytes, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A committed migration record disappeared.");
        }
    }

    public async ValueTask<StorageMigrationRecord<T>?> ReadAsync(string key, int maximumBytes, CancellationToken cancellationToken)
    {
        IObjectEnvelope<T>? identity;
        if (_snapshotIdentities is { } identities)
        { if (!identities.TryGetValue(key, out identity)) return null; }
        else if (!catalog.TryGetValue(key, out identity)) return null;
        var snapshot = await snapshots.CaptureCommittedAsync(identity, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        var entity = snapshot.Materialize();
        var envelope = entity.Envelope ?? throw new InvalidDataException("Committed snapshot has no envelope.");
        if (envelope.EnvelopeCacheKey != key) throw new InvalidDataException("Committed snapshot key mismatch.");
        return new(key, envelope.VersionTag, envelope.RevisionTimestampUtcMicroseconds,
            envelope.ExtensionMetadata?.ToArray(), () => snapshot.Materialize(attachEnvelope: false));
    }

}

public sealed class RepositoryStorageMigrationSource<T>(IStorageMigrationEndpoint<T> reader,
    IStorageMigrationSourceGuard guard, IStorageMigrationRevisionClock clock) : IStorageMigrationSource<T>
    where T : class, IStoredContract<T>
{
    public string Identity => reader.Identity;
    public string ContractVersion => reader.ContractVersion;
    public string? StorageDirectory => reader.StorageDirectory;
    public string? ServiceRegistrationKey => reader.ServiceRegistrationKey;
    public IRepository<T> Repository => reader.Repository;
    public object? GetService(Type type) => reader.GetService(type);
    public ValueTask InitializeSnapshotAsync(CancellationToken ct) => reader.InitializeSnapshotAsync(ct);
    public IAsyncEnumerable<StorageMigrationRecord<T>> ReadAllAsync(int maximumBytes, CancellationToken ct) => reader.ReadAllAsync(maximumBytes, ct);
    public ValueTask<StorageMigrationRecord<T>?> ReadAsync(string key, int maximumBytes, CancellationToken ct) => reader.ReadAsync(key, maximumBytes, ct);
    public ValueTask<long> ReadRevisionFloorAsync(CancellationToken ct) => clock.ReadAsync(ct);
    public async ValueTask<IAsyncDisposable> AcquireSourceLeaseAsync(CancellationToken ct)
    {
        var lease = await guard.AcquireAsync(ct).ConfigureAwait(false);
        if (Repository is IRevisionEditingRepository<T> { RetentionStatistics.ActiveSessions: > 0 })
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Storage migration runs before application use; close active edit sessions first.");
        }
        return lease;
    }
}

public sealed class RepositoryStorageMigrationDestination<T>(IStorageMigrationEndpoint<T> reader,
    IStorageMigrationRevisionClock clock, Func<CancellationToken, ValueTask> verifyDestination,
    IStorageMigrationDestinationBinding? binding = null) : IStorageMigrationDestination<T>
    where T : class, IStoredContract<T>
{
    private readonly IEnvelopeMetadataRepository<T> _repository = reader.Repository as IEnvelopeMetadataRepository<T>
        ?? throw new NotSupportedException("Destination must support conditional metadata imports.");
    private readonly IEnvelopeImportRepository<T> _imports = reader.Repository as IEnvelopeImportRepository<T>
        ?? throw new NotSupportedException("Destination must support imports without application notifications.");
    public string Identity => reader.Identity;
    public string ContractVersion => reader.ContractVersion;
    public string? StorageDirectory => reader.StorageDirectory;
    public string? ServiceRegistrationKey => reader.ServiceRegistrationKey;
    public IRepository<T> Repository => reader.Repository;
    public object? GetService(Type type) => reader.GetService(type);
    public IStorageMigrationDestinationBinding Binding => binding
        ?? throw new NotSupportedException("Destination must supply durable migration identity binding.");
    public ValueTask<long> ReadRevisionFloorAsync(CancellationToken cancellationToken) => clock.ReadAsync(cancellationToken);
    public ValueTask InitializeSnapshotAsync(CancellationToken ct) => reader.InitializeSnapshotAsync(ct);
    public IAsyncEnumerable<StorageMigrationRecord<T>> ReadAllAsync(int maximumBytes, CancellationToken ct) => reader.ReadAllAsync(maximumBytes, ct);
    public ValueTask<StorageMigrationRecord<T>?> ReadAsync(string key, int maximumBytes, CancellationToken ct) => reader.ReadAsync(key, maximumBytes, ct);

    public ValueTask PrepareDestinationAsync(long revisionFloor, CancellationToken cancellationToken)
    {
        if (_repository.CommitDurability != StorageDurabilityMode.Durable)
            throw new InvalidOperationException("Storage migration requires durable destination commits.");
        return clock.SeedAsync(revisionFloor, cancellationToken);
    }

    public async ValueTask ImportAsync(StorageMigrationRecord<T> record, CancellationToken cancellationToken)
    {
        if (record.Version != ContractVersion) throw new InvalidOperationException("Import contract version mismatch.");
        await _imports.ImportWithoutNotificationsAsync(record.Materialize(), record.Key, record.Revision, record.Metadata?.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask VerifyDestinationAsync(CancellationToken cancellationToken) => verifyDestination(cancellationToken);

    public async ValueTask ValidateCompletedDestinationAsync(CancellationToken cancellationToken)
    {
        if (!clock.Exists) throw new InvalidDataException("Completed migration destination is missing its revision clock. Restore the complete database, including migration state.");
        await clock.ReadAsync(cancellationToken).ConfigureAwait(false);
        await verifyDestination(cancellationToken).ConfigureAwait(false);
    }
}
