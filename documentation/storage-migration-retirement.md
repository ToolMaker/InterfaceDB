# Per-installation migration retirement and cleanup

Available under MIT in `InterfaceDB.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

## Agreed behavior

Ernie authorized removing completed migration configuration and cleaning its migration
data, while keeping anything not successfully migrated. Retirement is consequently
per installation: an updated device can retire its handover, while an unfinished or
skipped-release device retains its migration path. Runtime does not edit application
configuration files. Healthy records remain readable and writable under the existing
opt-in partial-availability mode.

The feature uses focused `IStorageMigrationSourceCleanup<T>` and existing endpoint,
fingerprint, state-store and publication abstractions. The coordinator does not select
provider classes. The supplied File cleanup adapter handles the original File source;
additional providers need their own implementation and qualification.

## Configuration and recovery

In the full `StorageMigrationOptions` registration:

```csharp
RetireAfterCompletion = true,                 // Default.
CleanupOriginalSourceAfterCompletion = true   // Explicit opt-in; default false.
```

The durable cleanup plan records each original source key, fingerprint and relative
location before destination imports. Cleanup begins only after full verification and
durable completion. It reacquires source exclusion, validates paths and compares each
remaining source record with its original fingerprint before durable deletion. It
does not compare against mutable destination records: later edits and deletes there
are legitimate and must survive cleanup or retry.

| Condition | Behavior |
| --- | --- |
| Migration incomplete, including deferred revision repair | Retain all source payloads and all recovery information. Healthy destination access follows partial-availability configuration. |
| Full completion; original payload still matches | Delete that payload when cleanup is enabled. |
| Changed, new or unreadable source record | Preserve it; report retained records and leave retirement pending. Do not silently merge it into the completed destination. |
| Source unavailable, deletion barrier fails or retirement publication fails | Keep the verified destination usable, retain durable recovery state and retry on startup or explicit `RetryAsync`. |
| Subsequent contract upgrade while original cleanup is pending | Carry the original cleanup plan forward; use configured version history to upgrade destination data. |
| Cleanup complete | Compact per-record recovery/cleanup lists and retain a small completion marker plus the destination's identity binding. |

Use keyed `IStorageMigrationProgress<T>` or the repository's `IStorageMigrationProgress`
capability. `Phase = Complete` means the destination dataset is available even if
`Retirement = Pending`. `RetirementIssue` contains a safe reason code; no paths or
payloads are included. `RetainedSourceRecords` is a count from the last successful
inventory, not a guarantee that an unavailable source contains no records. Explicit
`RetryAsync` does not require reopening a healthy completed repository.

Cleanup removes original File payloads only. It leaves directories, clocks, provider
recovery files, unrelated files and historical SegmentCore version stores untouched.
There is no recursive source deletion. Old writers must remain stopped; a receipt
cannot control an old binary that does not understand it.

Choose cleanup before retirement. Retiring without cleanup preserves the source
backup; enabling cleanup afterward does not reinterpret the compact marker as a new
deletion instruction. To delay retirement, set `RetireAfterCompletion = false` with
cleanup disabled. A completed, unretired older receipt can acquire a cleanup plan
only when its frozen source still matches its recorded manifest.

Cleanup plans share `FileStorageMigrationStateStore`'s configurable 16 MiB default
limit. Size the bound for the dataset. A plan is written before imports; recovery
verifies actual commits instead of rewriting this potentially large plan after each
record. No speed or memory improvement is claimed.

## Removing source configuration

For an installation with `Retirement = Retired`, replace the full coordinator with
`RetiredStorageMigration<T>` under the same logical key. Keep the destination, receipt
identity/encryptor and destination binding. No File registration, source transform or
source encryption keys are needed by this minimal registration.

```csharp
// destinationConfig, segmentOptions and destinationEncryptor are the same
// application-owned destination settings used by the full migration.
services.AddSegmentCoreRepository<IMyContract>(destinationConfig,
    segmentOptions, destinationConfig.StoragePolicy ?? StoragePolicy.Default,
    encryptor: destinationEncryptor);

services.AddStorageMigration<IMyContract>("messages", sp =>
    new RetiredStorageMigration<IMyContract>("messages-file-to-segment-v1",
        StorageMigrationEndpointFactory.LocalDestination<IMyContract>(
            sp, "new-segment", destinationRoot, publication, async ct =>
            {
                var maintenance = sp.GetRequiredKeyedService<
                    ISegmentCoreRepositoryMaintenance<IMyContract>>("new-segment");
                await maintenance.CheckpointAsync(ct);
                if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid)
                    throw new InvalidDataException("Destination integrity check failed.");
            }),
        new FileStorageMigrationStateStore(receiptPath,
            "MyApplication:IMyContract:messages", destinationEncryptor, publication)));
```

This example uses the identities and composition dependencies from the
[main integration guide](storage-migration.md). Await logical repository initialization
before accepting application work.

The minimal registration verifies retirement, migration identity, contract version,
destination-instance binding and destination integrity before opening. Missing,
unfinished, wrong-version or swapped markers fail; there is no empty-store fallback.
The application may inspect the marker to choose its DI registration, but the minimal
coordinator still revalidates it under the state lease. Keep the full upgrade path
available in deployments that must handle pending or skipped-release installations.
Later schema upgrades still need their supported migration/history configuration;
the minimal registration does not discover or convert older schemas.

## Review recommendations revisited

Per-installation retirement and verified source cleanup are now adopted. Removing
the upgrade path globally after one release, deleting completion evidence, deleting
unverified source data, automatic fallback and abort/quarantine remain excluded.
Read-back, completed-startup integrity checks, durable writes, positive revisions,
conflict protection and encryption remain intact. See the
[remaining review decisions](storage-migration-legacy-repair.md#review-items-retained-for-decision).
