# InterfaceDB.Storage.SegmentCore

## Preview.14 release

Connection recovery, safe transport diagnostics and optional native accounts are documented in the [upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview14-release-notes.md). Update all four public packages together. Existing JWT settings, isolation, committed reads and durability remain unchanged.

## Inherited preview.12 storage baseline

File and SegmentCore reads return independent committed copies, including strong/weak caches. Unsaved edits stay private until a successful save. Correctness fixes cover indexed predicates, key identity, index failure visibility, transactions and compaction-safe scans. Serializer-aware context handling removes unused work without changing encryption or durability defaults.

Cached selective searches improve, while copied large results and scans cost more. See the [preview.12 upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview12-release-notes.md) and [measured trade-offs](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/performance-proposal-review-20261020.md). Update all InterfaceDB packages together and rebuild.

The optional **MIT-licensed append-only storage provider for InterfaceDB on .NET 10**. Use the same `IRepository<T>` contracts, object serialization, indexes and encryption as the file provider, with store-local atomic batches, a mutation WAL, checkpoints, integrity checking, backup/restore and compaction.

**Preview.10 fixes encrypted-field preservation and source-version validation in native migration, and updates dependencies.** It includes preview.9 security hardening. Plaintext envelopes remain `IDB6`; AES-GCM repositories write context-bound `IDBE` version 3, and restore has explicit resource-admission controls.

## New in preview.11

Optional `InterfaceDB.Transport` and `InterfaceDB.Authentication` packages add TLS TCP access, JWT validation and disposable pub/sub for one remote database. The TCP client has no persistent local database or offline queue.

Storage and TCP can share the registered revision-aware envelope serializer, including contextual encryption wrappers. TCP metadata acknowledgements reuse materialized records. Default MessagePack and encrypted envelope formats are unchanged. See the [preview.11 upgrade guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview11-release-notes.md) and [serializer configuration](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/serializer-configuration.md).

## Install and register

```powershell
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.14
dotnet add package Microsoft.Extensions.DependencyInjection
```

The package depends on InterfaceDB preview.14. Configure your contract, data root and storage policy, then register with `AddSegmentCoreRepository<TContract>`. Follow the [complete registration example](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/segment-core-storage-provider.md#install-and-register) and [getting-started guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/getting-started.md).

## Inherited from preview.10

Native migration restores encrypted fields before the first `Up()`, validates each canonical source version and preserves supported metadata/revisions. Failed migrations leave the source intact; earlier batches may already exist in the separate target. Inspect failures and verify the target before cutover. See [migration setup](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/migrations.md).

Shared APIs now expose `IRepository<T>.RunningVersion` and `IRepositorySubscription.Ready`. Await each subscription's readiness before an initial query. See [preview.10 release and API compatibility notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview10-release-notes.md).

## Security hardening inherited from preview.9

- Encrypted writes bind `IDBE` v3 ciphertext to the SegmentCore provider, repository registration, contract and physical key. Contextless v1/v2 reads require a temporary `AllowContextlessPayloadMigration` setting and must be rewritten before it is disabled.
- Encrypted fields cannot be persisted into search indexes. Encrypted repositories keep search indexes in memory and delete stale plaintext catalog artifacts at startup.
- Named encryption registration works before or after repository registration, stored-object reads have a 256 MiB default payload limit, and persisted runtime types must be enrolled in a frozen `StoredRuntimeTypeAllowlist<TContract>`.
- Backup and restore reject observed symbolic links, junctions and reparse points. Restore accepts only known canonical manifest paths.
- `SegmentCoreRestoreOptions` adds manifest, file-count, aggregate-byte, per-file, path-depth, elapsed-time and free-space admission.

## New capabilities introduced in preview.7

| Capability | Behavior |
| --- | --- |
| Revision-checked saves/deletes | Exact expected-revision checks reject stale updates and deletes; the accepted revision is committed with its payload |
| Conditional metadata/import APIs | Existing-key metadata writes require an explicit destination condition; acknowledgements preserve the entity revision and check expected metadata bytes |
| Optional repository notifications | Created/Changed/Deleted subscriptions expose isolated committed objects, filters, changed-property names and bounded queues |
| Optional retained editing | Explicit edit sessions preserve original committed versions through newer writes and compaction, with bounded leases and archive accounting |
| Custom conflict strategy contracts | Applications can supply bounded decisions, including rejection, resolved replacement or no-change; strict equality stays the default |
| Maintenance policies | Choose throughput-first, idle-incremental, space-first or manual-windowed cleanup admission |
| Independent cleanup output | Opt-in immutable cleanup files allow copying alongside foreground log writes, subject to reader/revision/retention safety |
| Shared I/O budgets and summaries | Limit supported copy work across stores; inspect estimates, deferral, reclaimed space and partial failures separately from entity events |
| Write, recovery and read optimizations | Single-record atomic appends, reuse of validated recovery scans, lower-allocation bulk reads and faster complete-object scan metadata handling |

The [InterfaceDB package overview](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.14) explains these shared features with save/conflict, notification and editing examples.

## Choosing maintenance behavior

Default compaction is `SlicedAppend`. `IndependentOutput` is an explicit option: selected records are copied to immutable cleanup files while ordinary writers use their existing logs. Publication rechecks physical identity so a stale copy cannot replace a newer save/delete/recreation. Active readers and retained editing baselines remain protected.

The following is a provider-options fragment; supply it to your existing `AddSegmentCoreRepository<T>` registration:

```csharp
using Idb.Storage.SegmentCore;

var providerOptions = new SegmentCoreRepositoryOptions
{
    CompactionOptions = new()
    {
        Mode = SegmentCoreCompactionMode.IndependentOutput,
        MaximumCopyConcurrency = 2,
        Selection = new()
        {
            MaximumCandidates = 8,
            MaximumCopyBytes = 64L * 1024 * 1024
        }
    }
};
```

**Back up before using this option.** Its first execution upgrades the durable engine manifest from 1.0 to **1.1**, even if subsequent work is cancelled/deferred. Older runtimes reject 1.1, and switching back to `SlicedAppend` does not downgrade a store.

Both compaction modes require the mutation-WAL protocol, selected by the repository provider. Raw-store callers must select it explicitly. A null schedule preserves legacy automatic scheduling; configuring a policy does not itself start another worker. `EnableBackgroundMaintenance = false` leaves polling to the application.

`ICompactionMaintenance<T>` exposes advisory estimates and manual execution. Policies can use explicit activity/window hints; budgets can be shared between stores on the same volume. Manual work still obeys retention, reader and free-space safety. Checkpoint/publication/retirement pauses remain, and some metadata I/O is outside the copy-byte budget.

Maintenance summaries report physical work rather than logical entity changes. Moving a record does not advance its revision or emit Created/Changed/Deleted. Reclaimed bytes mean observed net reduction, not simply all deleted-file bytes; concurrent growth and retained history affect the result. Failed retirement is observable and may leave a partial cleanup. [Full maintenance configuration and recovery rules](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/compaction-maintenance.md).

## Durability, performance and ownership

Ordinary single saves/deletes use the owning stripe's atomic append and still wait for the configured durability policy. `SaveManyAsync` and explicit transactions retain coordinated atomic commit, including batches containing one item. Single-write transaction counters therefore differ from explicit-transaction counters.

Recovery reuses validated stripe results while keeping exclusive ownership. Bulk reads and scans reduce allocations and repeated metadata work while preserving checksums, complete-object materialization and guarded cache publication. These are workload-dependent improvements, not a universal SQLite ranking or a guarantee of zero GC/maintenance latency. [Fast-path details](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/storage-fast-paths.md).

One owning process is allowed per live store root. Transactions cover one contract store, not multiple stores or machines. Notifications are local observations through one repository owner, not a durable feed. Retained edit handles expire and do not survive close/restart; backups cover current/recovery state, not resumable editing sessions.

## Restore admission

The existing `RestoreBackupAsync(backup, destination, storeOptions, cancellationToken)` overload uses the new defaults. Use the overload with `SegmentCoreRestoreOptions` to set deployment-specific limits:

```csharp
var restored = await SegmentCoreStore.RestoreBackupAsync(
    backupDirectory,
    destinationDirectory,
    storeOptions,
    new SegmentCoreRestoreOptions
    {
        MaximumFiles = 25_000,
        MaximumTotalBytes = 128L * 1024 * 1024 * 1024,
        MaximumDuration = TimeSpan.FromHours(2),
        MinimumFreeSpaceReserveBytes = 512L * 1024 * 1024
    },
    cancellationToken);
```

Defaults are a 16 MiB manifest, 100,000 files, 1 TiB total, 256 GiB per file, four path components, 24 hours, required free-space admission, and a 64 MiB reserve. Validation and SHA-256 checks complete before the new destination is published. Protect or sign the manifest separately when backup authenticity is required.

## Upgrade from older previews

Preview.10, like preview.9, writes plaintext **IDB6** envelopes, but encrypted repositories now write context-bound **IDBE v3**. Preview.8 and earlier cannot read v3 ciphertext.

Stop old writers and retain a verified full pre-upgrade backup, the older runtime, and all required keys. SegmentCore does not register `IEncryptionRotationOperation<TContract>`. For an encrypted store, temporarily enable `AllowContextlessPayloadMigration`, enumerate and materialize every record so the bounded compare-before-replace read path rewrites each contextless frame, checkpoint, and run `ISegmentCoreRepositoryMaintenance<TContract>.VerifyIntegrityAsync`. Reopen with the option disabled and enumerate every record again before considering the migration complete. Restore the complete pre-upgrade backup for rollback; do not transplant v3 files into an older store.

Legacy non-`IDB` typeless payloads are disabled by default and need an explicit trusted offline conversion. The default runtime-type allowlist contains concrete implementations declared in the contract assembly; pass an explicit `StoredRuntimeTypeAllowlist<TContract>` to `AddSegmentCoreRepository` for implementation types elsewhere. The plaintext IDB6 transition and optional SegmentCore engine-format 1.1 cleanup transition remain separate; switching back to `SlicedAppend` does not downgrade engine format 1.1.

## Qualification and documentation

Preview.7 historically passed Windows/Linux CI, clean-package consumption and upgrade/backup/restore validation. Preview.9 adds targeted tests for context binding, encrypted-catalog isolation, registration order, oversized payloads, restore quotas, unknown manifest paths, and Windows link/reparse rejection. Final package publication still requires the release workflow and clean-package gates. Platform-specific handle-pinned namespace-swap hardening, Windows/Linux adversarial-filesystem qualification, physical power-cut evidence and Apple-device runtime qualification remain outstanding before corresponding stable claims.

- [Complete provider guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/segment-core-storage-provider.md)
- [Revision and metadata operations](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/envelope-revisions.md)
- [Retained editing and strategy examples](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/retained-editing.md)
- [Notification semantics and limits](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/repository-notifications.md)
- [Preview.10 release notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview10-release-notes.md)
- [Preview.9 security and upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview9-release-notes.md)
- [Preview.8 documentation notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.14/documentation/preview8-release-notes.md)
- [Report an issue](https://github.com/ToolMaker/InterfaceDB/issues)
