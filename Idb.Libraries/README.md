# InterfaceDB

Interface-first object repositories and embedded object storage for **.NET 10**, licensed under **MIT**. Application code uses versioned interfaces and `IRepository<T>`; InterfaceDB handles serialization, storage, indexes, migrations, encryption and repository lifecycle.

`InterfaceDB` provides typed `IRepository<T>` APIs, MessagePack envelope serialization, file-per-object storage, indexes, repository notifications, revision-based write-conflict checks and optional retained editing. It is an embedded library; a database host does not authenticate application users on its own.

The core package includes file-per-object storage. [InterfaceDB.Storage.SegmentCore](https://www.nuget.org/packages/InterfaceDB.Storage.SegmentCore) adds the optional append-only provider. Both packages are free and MIT-licensed.

**Preview.9 is a runtime and encrypted-payload format update.** Plaintext envelopes remain `IDB6`; AES-GCM repositories now write context-bound `IDBE` version 3 and require a controlled rewrite of older contextless ciphertext.

## Install and get started

```shell
dotnet add package InterfaceDB --version 1.0.0-preview.9
# Optional append-only provider:
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.9
```

Start with the [getting-started guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/getting-started.md) for contracts, configuration, registration and your first save/query. The snippets below assume an initialized repository and your existing `IPerson` contract with its `Name` property; they are individual operations, not a complete application.

## Security hardening in preview.9

- AES-GCM writes authenticate the provider, repository registration, contract identity and physical record key through `IDBE` v3. A copied ciphertext fails when opened under another authenticated storage context.
- `IDBE` v1/v2 reads require an explicit `AllowContextlessPayloadMigration` window. Accepted legacy ciphertext is marked for replacement; rewrite and verify the complete store before disabling the option.
- Legacy non-`IDB` typeless MessagePack payloads are disabled by default. Normal `IDB2`–`IDB6` envelopes remain readable only when the concrete type is enrolled in the frozen `StoredRuntimeTypeAllowlist<TContract>`.
- `StoragePolicy.MaximumStoredObjectBytes` defaults to 256 MiB and rejects oversized file-backed envelopes before allocating their payload buffers.
- `[EncryptedStorageField]` members are omitted from type-level search indexes, while an explicit `[SearchIndex]` on the same member is rejected.
- Persistent SegmentCore search-index catalogs are disabled and purged for encrypted repositories; the indexes are rebuilt in memory from authoritative encrypted records.
- Explicit named encryption registration works in either order relative to repository registration.
- SegmentCore restore adds `SegmentCoreRestoreOptions` for manifest, file-count, aggregate-byte, per-file, path-depth, duration and free-space admission. Backup and restore reject observed links/reparse points, and restore rejects unknown manifest paths.

See the [preview.9 release notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/preview9-release-notes.md) for defaults, migration steps, rollback and remaining qualification.

## What was added in preview.7

| Addition | Application benefit | Activation |
| --- | --- | --- |
| Revision-checked writes | Reject stale edits/deletes and retrieve current committed values after a conflict | Default save/delete behavior |
| Conditional metadata APIs | Coordinate metadata changes/imports with an expected destination revision | Explicit metadata operations |
| Repository notifications | Observe Created/Changed/Deleted entities with filters and isolated payloads | `StoragePolicy.RepositoryNotifications` |
| Retained editing | Keep an original committed version available during an explicit edit | `StoragePolicy.RetainedVersions` plus `BeginEditAsync` |
| Custom strategy contracts | Supply application-owned conflict decisions using incoming/current/original values | Explicit strategy registration |
| Maintenance policies and budgets | Schedule and bound supported cleanup, with separate maintenance summaries | Provider configuration; SegmentCore integration |
| Storage fast paths | Reduce single-write, recovery, bulk-read and complete-object scan overhead | Automatic |

File and SegmentCore support revisions, notifications and retained editing. SegmentCore supports segment compaction; the file provider has no segment-compaction operation.

## 1. Revision-checked saves and deletes

Every stored envelope carries `RevisionTimestampUtcMicroseconds`. A loaded object saves or deletes only when its expected revision matches the committed version. If another editor changed or deleted it, the stale operation is rejected. A new object without an envelope inserts only when its physical identity is absent.

```csharp
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Abstractions.Services.Serialization;

person.Name = "Updated";
try
{
    person = await repository.SaveAsync(person);
}
catch (WriteConflictException<IPerson> conflict)
{
    var current = await conflict.ReadCurrentAsync();
    // Fresh isolated value, or null after deletion.
    // Decide whether to discard or reconstruct the edit; do not retry blindly.
}
```

Use the returned saved entity for subsequent edits. Its revision is persisted with its payload. Rejection does not advance the caller's revision, and an old copy cannot recreate a deleted entity. Separate editors should use separate object copies; revision checks do not make shared mutable application objects thread-safe.

The token advances despite equal clock ticks or wall-clock rollback. It is a local revision expressed in UTC microseconds, not a globally unique identifier or exact event-time guarantee. The `.idb-revision-clock` sidecar belongs in backups.

Existing-key metadata writes require an explicit `EnvelopeWriteCondition`; the overload without a condition is insert-only. Metadata-only acknowledgement checks revision and expected metadata bytes while preserving the committed entity/revision. Import is an explicit infrastructure operation with a destination precondition.

File bulk operations can report partial success through `IStorageBulkOperationResult`, including `SucceededIndexes` and `CommittedItems`. SegmentCore retains atomic coordinated batches. [Revision, metadata and bulk-write guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/envelope-revisions.md).

## 2. Local repository notifications

Enable observation in the policy used to register the repository:

```csharp
using Idb.Libraries.Abstractions.Options;

var policy = StoragePolicy.Default with
{
    RepositoryNotifications = new RepositoryNotificationOptions()
};
// Apply policy to the contract configuration/registration.
// This does not change the chosen durability mode.
```

Resolve notifications with the same DI key as the repository:

```csharp
using Microsoft.Extensions.DependencyInjection;

var notifications = provider.GetRequiredKeyedService<
    IRepositoryNotifications<IPerson>>(databaseKey);
using var subscription = notifications.SubscribeChanged(change =>
{
    Console.WriteLine(string.Join(", ", change.ChangedProperties));
    return ValueTask.CompletedTask;
}, person => true);
```

- Created carries the new committed entity. Changed carries committed after-values and top-level changed-property names. Deleted carries the last committed values without an attached envelope.
- Each subscriber receives an isolated payload. Initial loading, unchanged contract values, rejected writes and metadata-only acknowledgement do not invent entity-change events.
- Filters select event values. Changed uses after-values; subscriptions do not maintain live-query enter/leave membership.
- Callbacks run asynchronously after storage/index publication, outside mutation locks. Marshal UI work to your application dispatcher.
- Own writes and causal descendants are suppressed by default to reduce feedback loops; explicit opt-in is available.
- Queues/concurrency are bounded. Overflow or observation/callback failure ends the affected subscription. Inspect `Completion`, requery and resubscribe after a gap; dispose subscriptions with their owning view/service.

Notifications are disabled by default and separate from physical envelope events. They observe writes through one owning repository instance; they are not durable replay, remote delivery or external-filesystem change history. Saving an old event object follows normal revision-conflict rules. [Notification guide and resource limits](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/repository-notifications.md).

## 3. Retained editing and custom conflict strategies

Retained editing keeps original committed bytes available during an explicit editing session, including across newer writes and supported compaction. Ordinary reads and saves do not acquire historical leases.

```csharp
var editingPolicy = StoragePolicy.Default with
{
    RetainedVersions = new RetainedVersionOptions
    {
        LeaseDuration = TimeSpan.FromMinutes(5),
        MaximumLifetime = TimeSpan.FromMinutes(30),
        MaximumSessions = 256,
        MaximumRecords = 256,
        MaximumBytes = 64L * 1024 * 1024
    }
};
// Configure editingPolicy before opening the repository.
```

```csharp
await using var edit = await repository.BeginEditAsync(
    person.Envelope!.EnvelopeCacheKey);
edit.Working.Name = "Updated";
var committed = await edit.SaveAsync();
```

Working values, original reads and current reads are isolated. A successful session is single-use and releases its baseline. A rejected edit can still read its original version until disposal/expiry. Renewal is explicit and bounded; repository close or process restart invalidates handles. These are not restart-persistent offline drafts or full database history.

The free `IWriteConflictStrategy<T>` contract supports acceptance, structured rejection, resolved replacement or returning current state without a new write. A strategy requiring original data needs an explicit retained edit. Strict revision equality remains the default; enabling retention does not automatically merge changes. Keep custom decisions bounded and non-reentrant. [Retained editing and custom-strategy examples](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/retained-editing.md).

## 4. Compaction policies, budgets and summaries

The free core supplies cleanup policy evaluation, candidate selection, activity/window hints, shared I/O budgets, scheduling and bounded maintenance notifications. Constructing these objects does not itself start a worker or scan files.

SegmentCore integrates `ICompactionMaintenance<T>` for advisory estimation, manual execution and provider-owned background scheduling. Policies include `ThroughputFirst`, `IdleIncremental`, `SpaceFirst` and `ManualWindowed`. Share a budget between stores when they should share limits. Manual requests still obey engine safety and working-space checks.

Default cleanup remains `SlicedAppend`. Optional `IndependentOutput` copies selected records into separate immutable files while foreground writes continue on their normal logs. It protects newer writes, active readers and retained editing baselines. Both modes require mutation WAL. Snapshot, checkpoint, publication and retirement pauses remain; some metadata/durability I/O is outside byte-rate limiting.

Physical relocation does not change entity revisions or emit Created/Changed/Deleted. Separate summaries report work, deferral, reclaimed space and failures. Failed file retirement can produce partial cleanup. Existing automatic maintenance continues unless a new schedule is configured. [Maintenance configuration and guarantees](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/compaction-maintenance.md).

## 5. Storage performance improvements

Single-object SegmentCore saves/deletes use the owning stripe's atomic append while retaining configured durability and revision checks. `SaveManyAsync` and explicit transactions retain coordinated commit, including explicit batches of one. Recovery reuses validated stripe scans while ownership remains held. Bulk reads reduce grouping/copy allocations.

Complete-object scans avoid rebuilding unchanged cache metadata, repeated revision observation, repeated immutable type inspection and redundant serializer metadata allocations. Cache publication still protects against concurrent updates/deletes. Measurements remain workload-dependent; these changes do not establish a universal lead over SQLite or remove payload-ownership copies. [Storage fast paths](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/storage-fast-paths.md).

## Upgrade, backup and rollback

**Preview.9 keeps plaintext envelopes at IDB6 but changes AES-GCM writes to context-bound IDBE v3. Preview.8 and earlier cannot read v3 ciphertext.**

1. Stop old writers and take a complete verified pre-upgrade backup. Retain the older runtime and every referenced key for rollback.
2. Rebuild applications and custom providers/envelopes/serializers/coordinators for the current APIs.
3. For an encrypted File-provider store, temporarily enable `AllowContextlessPayloadMigration`, run the File-provider `IEncryptionRotationOperation<TContract>.RotateAllAsync`, reopen and verify every record, then disable the option.
4. SegmentCore does not register a full-store rotation operation. With `AllowContextlessPayloadMigration` enabled, enumerate and materialize every record so its bounded compare-before-replace read path rewrites the frame. Checkpoint and run `ISegmentCoreRepositoryMaintenance<TContract>.VerifyIntegrityAsync`, reopen with the option disabled, and enumerate every record again.
5. If plaintext rollout data must be imported into an encrypted repository, enable both `AllowUnencryptedPayloads` and `AllowContextlessPayloadMigration`, then use the same provider-specific complete rewrite and verification before disabling them.
6. Restore the complete pre-upgrade backup to roll back to an older reader. Do not copy v3 files into an older store.

Legacy non-`IDB` typeless payloads no longer open through default repository registration. Convert trusted legacy data through an explicit offline serializer/conversion path; dedicated migration tooling remains planned.

The default runtime-type allowlist contains closed concrete implementations declared in the contract assembly. Before upgrading, inventory every concrete type already stored and pass an explicit `StoredRuntimeTypeAllowlist<TContract>` when implementations live elsewhere or the application requires a narrower set.

Keep File sidecar write locks enabled, including Performance mode. Back up the whole quiescent File root including its revision clock; use SegmentCore's point-consistent maintenance backup.

SegmentCore **independent-file compaction remains a separate engine-format 1.1 opt-in**. Default stores remain format 1.0. The first independent compaction upgrades the durable manifest even if later work is cancelled/deferred. Older runtimes reject it; switching back to `SlicedAppend` does not downgrade it. Verify a backup before enabling it.

## Qualification and supported boundaries

Preview.7 historically passed Windows/Linux CI and the Linux release workflow, including 481 public tests, 50 integration scenarios and 56 published-package smoke cases. Preview.9 adds targeted regressions for contextual ciphertext, registration order, encrypted-index/catalog isolation, oversized payloads, and SegmentCore restore links, inventory and quotas. Final package publication still requires the repository release workflow and clean-package gates.

Actual acknowledged-write physical power-cut evidence and Apple-device runtime qualification remain outstanding. Qualify your application workload and target device before critical deployment. SegmentCore permits one owning process per live root and one-contract-store transactions. These packages do not provide SQL joins, distributed transactions or remote-user authentication.

## Documentation and support

- [Preview.9 security and upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/preview9-release-notes.md)
- [Preview.7 feature notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/preview7-release-notes.md)
- [Preview.8 documentation notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/preview8-release-notes.md)
- [Documentation index](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.9/documentation/README.md)
- [Report an issue](https://github.com/ToolMaker/InterfaceDB/issues)
