# InterfaceDB.Storage.SegmentCore

The optional **MIT-licensed append-only storage provider for InterfaceDB on .NET 10**. Use the same `IRepository<T>` contracts, object serialization, indexes and encryption as the file provider, with store-local atomic batches, a mutation WAL, checkpoints, integrity checking, backup/restore and compaction.

**Preview.8 is a documentation-only release of the preview.7 implementation.** It updates the public NuGet pages; it introduces no additional runtime or storage-format change.

## Install and register

```powershell
dotnet add package InterfaceDB.Storage.SegmentCore --version 1.0.0-preview.8
dotnet add package Microsoft.Extensions.DependencyInjection
```

The package depends on InterfaceDB preview.8. Configure your contract, data root and storage policy, then register with `AddSegmentCoreRepository<TContract>`. Follow the [complete registration example](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/segment-core-storage-provider.md#install-and-register) and [getting-started guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/getting-started.md).

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

The [InterfaceDB package overview](https://www.nuget.org/packages/InterfaceDB/1.0.0-preview.8) explains these shared features with save/conflict, notification and editing examples.

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

Maintenance summaries report physical work rather than logical entity changes. Moving a record does not advance its revision or emit Created/Changed/Deleted. Reclaimed bytes mean observed net reduction, not simply all deleted-file bytes; concurrent growth and retained history affect the result. Failed retirement is observable and may leave a partial cleanup. [Full maintenance configuration and recovery rules](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/compaction-maintenance.md).

## Durability, performance and ownership

Ordinary single saves/deletes use the owning stripe's atomic append and still wait for the configured durability policy. `SaveManyAsync` and explicit transactions retain coordinated atomic commit, including batches containing one item. Single-write transaction counters therefore differ from explicit-transaction counters.

Recovery reuses validated stripe results while keeping exclusive ownership. Bulk reads and scans reduce allocations and repeated metadata work while preserving checksums, complete-object materialization and guarded cache publication. These are workload-dependent improvements, not a universal SQLite ranking or a guarantee of zero GC/maintenance latency. [Fast-path details](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/storage-fast-paths.md).

One owning process is allowed per live store root. Transactions cover one contract store, not multiple stores or machines. Notifications are local observations through one repository owner, not a durable feed. Retained edit handles expire and do not survive close/restart; backups cover current/recovery state, not resumable editing sessions.

## Upgrade from older previews

Preview.7/preview.8 write **IDB6 envelopes**, with backward reads for IDB2–IDB5. Preview.6 and earlier cannot read IDB6. Stop old writers, retain a verified full pre-upgrade backup, and rebuild custom providers/envelopes/serializers/coordinators. Restore that backup for rollback to an older reader and discard outstanding application objects/edit handles.

The IDB6 envelope change and the optional engine-format 1.1 cleanup change are separate. Default stores stay engine format 1.0, but their new envelope writes are still IDB6. Moving from preview.7 to preview.8 adds neither transition.

## Qualification and documentation

Preview.7 passed Windows/Linux CI, clean-package consumption and upgrade/backup/restore validation. Its local public qualification included 481 tests with four platform-specific skips, 50 integration scenarios and 56 smoke cases against published packages. Preview.8 changes documentation/package metadata only and uses the same release gates. Physical power-cut evidence and Apple-device runtime qualification remain outstanding before corresponding stable durability/platform claims.

- [Complete provider guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/segment-core-storage-provider.md)
- [Revision and metadata operations](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/envelope-revisions.md)
- [Retained editing and strategy examples](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/retained-editing.md)
- [Notification semantics and limits](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/repository-notifications.md)
- [Preview.8 release notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.8/documentation/preview8-release-notes.md)
- [Report an issue](https://github.com/ToolMaker/InterfaceDB/issues)
