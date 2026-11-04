using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Checked by the provider's identity factory after resolving new physical keys.
/// The logical facade checks existing/explicit keys under the same migration gate.</summary>
internal sealed class StorageMigrationWriteReservations : IStorageMigrationWriteReservations
{
    private sealed record Reservations(HashSet<string> Physical, HashSet<string> Logical);
    private Reservations _pending = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
    public void SetPendingKeys(IReadOnlyCollection<string> keys)
    {
        var physical = new HashSet<string>(keys, StringComparer.Ordinal);
        var logical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in physical)
        {
            var separator = key.LastIndexOf('-');
            if (separator > 0 && int.TryParse(key.AsSpan(separator + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)) logical.Add(key[..separator]);
        }
        Volatile.Write(ref _pending, new(physical, logical));
    }
    public void ValidateMutation(string? key)
    {
        if (key is not null && Volatile.Read(ref _pending).Physical.Contains(key))
            throw new InvalidOperationException("This storage key is reserved for a record awaiting migration repair.");
    }
    internal void CheckNewIdentity(string key, Idb.Libraries.Abstractions.Enums.StorageObjectCardinality cardinality)
    {
        if (cardinality != Idb.Libraries.Abstractions.Enums.StorageObjectCardinality.Multiple) { ValidateMutation(key); return; }
        // Supplied local providers append a numeric suffix after identity resolution.
        // Reserve that logical group until its pending physical keys have been imported.
        if (Volatile.Read(ref _pending).Logical.Contains(key))
            throw new InvalidOperationException("This logical storage key has records awaiting migration repair.");
    }
}

internal sealed class MigrationStorageIdentityFactory<T>(IEnvelopeStorageIdentityFactory<T> inner,
    StorageMigrationWriteReservations reservations) : IEnvelopeStorageIdentityFactory<T> where T : class, IStoredContract<T>
{
    public Idb.Libraries.Abstractions.Enums.StorageObjectCardinality ObjectCardinality => inner.ObjectCardinality;
    public void InitEnvelope(IObjectEnvelope<T> envelope)
    {
        inner.InitEnvelope(envelope);
        reservations.CheckNewIdentity(envelope.EnvelopeCacheKey, ObjectCardinality);
    }
}

internal interface IStorageMigrationAccess
{
    void EnsureReadable();
    ValueTask<IAsyncDisposable> AcquireWriteAsync(IEnumerable<string?> keys, CancellationToken cancellationToken);
}

// The selected provider is never returned raw, including to direct OpenAsync callers.
internal sealed class SelectedStorageMigration<T>(IRepository<T> repository, IStorageMigrationAccess access,
    IStorageMigrationProgress progress) : IStorageMigration<T>, IStorageMigrationAccess, IStorageMigrationProgress,
    IRepositoryDataAvailability, IStorageMigrationCapabilities where T : class, IStoredContract<T>
{
    public Idb.Libraries.Abstractions.Enums.ContractRepositoryType RepositoryType => repository.RepositoryType;
    public string? RunningVersion => repository.RunningVersion;
    public Idb.Libraries.Abstractions.Enums.StorageDurabilityMode CommitDurability => ((IEnvelopeMetadataRepository<T>)repository).CommitDurability;
    public RetainedVersionCapability RetainedVersions => ((IRevisionEditingRepository<T>)repository).RetainedVersions;
    public RetainedVersionStatistics RetentionStatistics => ((IRevisionEditingRepository<T>)repository).RetentionStatistics;
    public bool IsDataComplete => Progress.Phase == StorageMigrationPhase.Complete;
    public StorageMigrationProgress Progress => progress.Progress;
    public ValueTask RetryAsync(CancellationToken cancellationToken = default) => progress.RetryAsync(cancellationToken);
    public ValueTask<IRepository<T>> OpenAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); access.EnsureReadable(); return ValueTask.FromResult(repository); }
    public void EnsureReadable() => access.EnsureReadable();
    public ValueTask<IAsyncDisposable> AcquireWriteAsync(IEnumerable<string?> keys, CancellationToken cancellationToken)
        => access.AcquireWriteAsync(keys, cancellationToken);
}
