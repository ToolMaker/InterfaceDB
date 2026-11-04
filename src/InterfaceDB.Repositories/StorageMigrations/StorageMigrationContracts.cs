using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Commercial.Repositories.StorageMigrations;

/// <summary>Commercial startup operation. Only the returned repository may be exposed to application code.</summary>
public interface IStorageMigration<T> where T : class, IStoredContract<T>
{
    ValueTask<IRepository<T>> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Configuration-time capabilities for composing a gated repository before startup.</summary>
public interface IStorageMigrationCapabilities
{
    ContractRepositoryType RepositoryType { get; }
    string? RunningVersion { get; }
    StorageDurabilityMode CommitDurability { get; }
    RetainedVersionCapability RetainedVersions { get; }
    RetainedVersionStatistics RetentionStatistics { get; }
}

public enum StorageMigrationTrigger { Explicit, ContractVersion }

public enum LegacyRevisionHandling { Reject, RepairOnStartup }
public enum IncompleteMigrationBehavior { Block, ContinueWithAvailableRecords }
public enum StorageMigrationPhase { NotStarted, Running, Incomplete, Complete, Faulted }
public enum StorageMigrationRetirement { NotReady, Pending, Retired }

public sealed record StorageMigrationProgress(StorageMigrationPhase Phase, int TotalRecords, int AvailableRecords, int PendingRecords)
{
    public StorageMigrationRetirement Retirement { get; init; }
    public int RetainedSourceRecords { get; init; }
    public string? RetirementIssue { get; init; }
}

public interface IStorageMigrationProgress
{
    StorageMigrationProgress Progress { get; }
    ValueTask RetryAsync(CancellationToken cancellationToken = default);
}

/// <summary>Contract-specific DI identity; repository keys remain independent across contracts.</summary>
public interface IStorageMigrationProgress<T> : IStorageMigrationProgress where T : class, IStoredContract<T> { }

/// <summary>Provider mutation admission during partial migration. Implementations must
/// also reject newly generated identities that would occupy reserved physical keys.</summary>
public interface IStorageMigrationWriteReservations
{
    void SetPendingKeys(IReadOnlyCollection<string> physicalKeys);
    void ValidateMutation(string? physicalKey);
}

/// <summary>Return null only for a deliberately deferred repair. Exceptions are fatal,
/// not permission to skip a record. The coordinator durably records assigned revisions
/// before import; implementations must not mutate the source or destination.</summary>
public interface IStorageMigrationLegacyRevisionRepair<T> where T : class, IStoredContract<T>
{
    ValueTask<long?> AssignAsync(StorageMigrationRecord<T> record, long observedFloor, CancellationToken cancellationToken);
}

public sealed class TimestampStorageMigrationRevisionRepair<T>(Idb.Libraries.Abstractions.Services.Serialization.IRevisionTimestampGenerator clock)
    : IStorageMigrationLegacyRevisionRepair<T> where T : class, IStoredContract<T>
{
    public ValueTask<long?> AssignAsync(StorageMigrationRecord<T> record, long observedFloor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<long?>(clock.Next(observedFloor));
    }
}

public sealed class StorageMigrationIncompleteException(int pendingRecords)
    : InvalidOperationException($"Storage migration has {pendingRecords} records awaiting revision repair.")
{
    public int PendingRecords { get; } = pendingRecords;
}

/// <summary>Version limits are inclusive. A null maximum has no upper limit.</summary>
public sealed record StorageMigrationOptions
{
    public required string MigrationId { get; init; }
    public required string RunningVersion { get; init; }
    public bool RunOnStartup { get; init; } = true;
    public StorageMigrationTrigger Trigger { get; init; } = StorageMigrationTrigger.Explicit;
    public string? SourceMaxVersion { get; init; }
    public string? DestinationMaxVersion { get; init; }
    public int MaximumRecords { get; init; } = 1_000_000;
    public int MaximumRecordBytes { get; init; } = 64 * 1024 * 1024;
    public int CheckpointEveryRecords { get; init; } = 128;
    public LegacyRevisionHandling LegacyRevisions { get; init; } = LegacyRevisionHandling.Reject;
    public IncompleteMigrationBehavior IncompleteBehavior { get; init; } = IncompleteMigrationBehavior.Block;
    public int MaximumRevisionRepairsPerAttempt { get; init; } = int.MaxValue;
    /// <summary>Deactivate this installation's completed handover and retain a completion marker.</summary>
    public bool RetireAfterCompletion { get; init; } = true;
    /// <summary>Explicitly remove verified records from the original source after full completion.
    /// Does not remove historical destination stores, directories or unrelated files.</summary>
    public bool CleanupOriginalSourceAfterCompletion { get; init; }
}

/// <summary>Committed read capabilities shared by migration sources and destinations.</summary>
public interface IStorageMigrationEndpoint<T> where T : class, IStoredContract<T>
{
    string Identity { get; }
    string ContractVersion { get; }
    string? StorageDirectory { get; }
    /// <summary>The exact keyed DI registration owned by this endpoint, or null for an
    /// endpoint with no DI registration. Decorators must explicitly forward this identity.</summary>
    string? ServiceRegistrationKey { get; }
    IRepository<T> Repository { get; }
    object? GetService(Type serviceType) => null;
    ValueTask InitializeSnapshotAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<StorageMigrationRecord<T>> ReadAllAsync(int maximumBytes, CancellationToken cancellationToken);
    ValueTask<StorageMigrationRecord<T>?> ReadAsync(string key, int maximumBytes, CancellationToken cancellationToken);
}

/// <summary>The source owns exclusion and exports its revision floor.</summary>
public interface IStorageMigrationSource<T> : IStorageMigrationEndpoint<T> where T : class, IStoredContract<T>
{
    ValueTask<IAsyncDisposable> AcquireSourceLeaseAsync(CancellationToken cancellationToken);
    ValueTask<long> ReadRevisionFloorAsync(CancellationToken cancellationToken);
}

public sealed record StorageMigrationCleanupRecord(string Key, string Fingerprint, string Locator);
public sealed record StorageMigrationCleanupResult(int DeletedRecords, int RetainedRecords);

/// <summary>Provider-owned cleanup. Capture runs under the source's migration lease.
/// Execute must acquire that lease, revalidate each record against its captured fingerprint,
/// preserve changed/unknown records, and durably publish each deletion. Locators must be relocatable.</summary>
public interface IStorageMigrationSourceCleanup<T> where T : class, IStoredContract<T>
{
    ValueTask<IReadOnlyList<StorageMigrationCleanupRecord>> CaptureAsync(
        IReadOnlyDictionary<string, string> sourceFingerprints, CancellationToken cancellationToken);
    ValueTask<StorageMigrationCleanupResult> ExecuteAsync(IReadOnlyList<StorageMigrationCleanupRecord> records,
        IStorageMigrationFingerprint<T> fingerprint, int maximumRecordBytes, int maximumRecords,
        CancellationToken cancellationToken);
}

/// <summary>The destination owns conditional imports, durability and integrity verification.</summary>
public interface IStorageMigrationDestination<T> : IStorageMigrationEndpoint<T> where T : class, IStoredContract<T>
{
    IStorageMigrationDestinationBinding Binding { get; }
    ValueTask<long> ReadRevisionFloorAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("Partial migration recovery requires the destination's current revision floor.");
    ValueTask PrepareDestinationAsync(long revisionFloor, CancellationToken cancellationToken);
    ValueTask ImportAsync(StorageMigrationRecord<T> record, CancellationToken cancellationToken);
    ValueTask VerifyDestinationAsync(CancellationToken cancellationToken);
    ValueTask ValidateCompletedDestinationAsync(CancellationToken cancellationToken);
}

/// <summary>Owns an immutable snapshot; materialization must return a fresh, detached object on every call.</summary>
public sealed record StorageMigrationRecord<T>(string Key, string Version, long Revision, byte[]? Metadata,
    Func<T> Materialize) where T : class, IStoredContract<T>;

/// <summary>Explicit schema conversion. Storage movement itself never guesses property mappings.</summary>
public interface IStorageMigrationTransform<T> where T : class, IStoredContract<T>
{
    string Identity { get; }
    T Transform(T detachedSource, string sourceVersion, string destinationVersion);
}

public sealed class UnchangedStorageMigrationTransform<T> : IStorageMigrationTransform<T> where T : class, IStoredContract<T>
{
    public string Identity => "unchanged-v1";
    public T Transform(T detachedSource, string sourceVersion, string destinationVersion)
    {
        if (!string.IsNullOrWhiteSpace(sourceVersion) && string.Equals(sourceVersion, destinationVersion, StringComparison.Ordinal))
            return detachedSource;
        if (StorageMigrationVersions.ParseOrdered(sourceVersion, "Source version")
            == StorageMigrationVersions.ParseOrdered(destinationVersion, "Destination version"))
            return detachedSource;
        throw new InvalidOperationException("A contract version change requires an explicit storage migration transform.");
    }
}

internal static class StorageMigrationVersions
{
    internal static Version ParseOrdered(string label, string role)
        => Version.TryParse(label, out var version) ? version
            : throw new ArgumentException($"{role} must be numeric for an ordered or different-version migration. Non-numeric labels require identical running, source and destination labels, the Explicit trigger and no version ceilings.");
}

/// <summary>Hashes canonical logical content, including key, version, revision and extension metadata.</summary>
public interface IStorageMigrationFingerprint<T> where T : class, IStoredContract<T>
{
    string Identity { get; }
    string Compute(StorageMigrationRecord<T> record);
}

/// <summary>The lease must exclude other coordinators across processes for this logical database.</summary>
public interface IStorageMigrationStateStore
{
    string? StoragePath { get; }
    ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken);
    ValueTask<StorageMigrationState?> ReadAsync(CancellationToken cancellationToken);
    /// <summary>Validates receipt capacity, including encoding/encryption overhead, without
    /// publishing state. Decorators must forward this preflight before destination mutation.</summary>
    ValueTask ValidateCapacityAsync(StorageMigrationState state, CancellationToken cancellationToken);
    ValueTask WriteAsync(StorageMigrationState state, CancellationToken cancellationToken);
}

public sealed record StorageMigrationState(string Plan, string SourceDigest, int RecordCount,
    long RevisionFloor, int VerifiedRecords, bool Completed)
{
    public int Format { get; init; } = 2;
    public string? TargetVersion { get; init; }
    public string? SourceVersion { get; init; }
    public bool SourceIsDestination { get; init; }
    public string? SourceInstance { get; init; }
    public string? DestinationInstance { get; init; }
    public string? TransformIdentity { get; init; }
    public long? SourceRevisionFloor { get; init; }
    public Dictionary<string, long> RepairedRevisions { get; init; } = new(StringComparer.Ordinal);
    public string[] PendingKeys { get; init; } = [];
    // Published records become application-owned: edits/deletes must survive resume.
    public string[] PublishedKeys { get; init; } = [];
    public bool PublishedPartial { get; init; }
    public string? MigrationId { get; init; }
    public bool Retired { get; init; }
    public bool CleanupRequired { get; init; }
    public StorageMigrationCleanupRecord[]? CleanupRecords { get; init; }
    public int RetainedSourceRecords { get; init; }
    public string? RetirementIssue { get; init; }
}

/// <summary>Durable identity stored with the destination, independent of its physical path.</summary>
public interface IStorageMigrationDestinationBinding
{
    /// <summary>Reads an existing identity or proposes a new one without writing to the destination.</summary>
    ValueTask<string> ProposeAsync(string plan, CancellationToken cancellationToken);
    /// <summary>Durably binds the exact proposed identity, rejecting an intervening different binding.</summary>
    ValueTask BindAsync(string plan, string instance, CancellationToken cancellationToken);
    ValueTask ValidateAsync(string plan, string instance, CancellationToken cancellationToken);
}

/// <summary>Opens an earlier destination through DI when resuming or upgrading. Each handle
/// owns its provider and must exclude other writers until disposed.</summary>
public interface IStorageMigrationHistory<T> where T : class, IStoredContract<T>
{
    ValueTask<IStorageMigrationHistoryHandle<T>> OpenAsync(string version, CancellationToken cancellationToken);
}

public interface IStorageMigrationHistoryHandle<T> : IAsyncDisposable where T : class, IStoredContract<T>
{
    IStorageMigrationSource<T> Source { get; }
    IStorageMigrationDestination<T> Destination { get; }
}

public interface IStorageMigrationServiceAccess
{
    bool OwnsServiceRegistration(string databaseKey);
    object GetReadyService(Type serviceType);
}

/// <summary>Provider-specific revision reservation storage; seed before opening a destination for writes.</summary>
public interface IStorageMigrationRevisionClock
{
    ValueTask<long> ReadAsync(CancellationToken cancellationToken);
    ValueTask SeedAsync(long floor, CancellationToken cancellationToken);
    bool Exists { get; }
}

public interface IStorageMigrationSourceGuard
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}
