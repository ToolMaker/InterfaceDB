# Startup revision repair and writable partial availability

Available under MIT in `InterfaceDB.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

## Agreed behavior

Ernie authorized startup repair of legacy records whose revision is zero, and
explicitly selected **reads and writes** for healthy records while repair is pending.
Unrelated repositories remain independently usable. A separate manual offline command
is not required; migration still excludes other source writers during each attempt.

- Existing positive revisions, keys, logical values and extension metadata are preserved.
- Source files remain unchanged during incomplete migration. Optional cleanup runs
  only after full verified completion. Missing revisions are allocated for the destination,
  recorded durably before import and reused after interruption. Allocation observes
  both the source floor and, after partial publication, the destination's current floor.
- Partial availability is opt-in. Missing/corrupt source data, an invalid revision,
  failed verification, uncertain commits or arbitrary exceptions do not authorize
  skipping a record. Only valid revision-0 records deliberately deferred by the repair
  policy or repair budget can remain pending.
- Completion remains false until all source records have been processed. Progress
  exposes the phase and source-record counts. `AvailableRecords` counts source records
  released to the application; it is not a live destination row count after user edits.
- Published records become application-owned. Updates, deletes and additions made
  during partial availability survive retry/restart; deleted records are not imported
  again from the old source. Published ownership is durable before access is returned.
- Pending physical keys are reserved. For the supplied local providers, a new identity
  in the same logical key group is also rejected until its pending suffixes are repaired.
  Existing healthy records in that group remain writable. Explicit-key and bulk writes
  are checked too; normal bulk atomicity semantics remain provider-defined.
- Retained edits and local notifications remain available. A retry serializes with
  application mutations: writes queue for that repair pass, while reads can continue
  against committed destination data. No maximum pause time is promised.
- Built-in resilient, local/remote and peer synchronization require a complete
  repository. Pending records are never inferred to be deletions. Infrastructure and
  export aliases remain gated until completion. Custom synchronization/export code
  must check `IRepositoryDataAvailability` after initialization and before using a full
  inventory. A query over an incomplete repository is not an authoritative inventory.

## Configuration

The defaults remain `LegacyRevisions = Reject` and `IncompleteBehavior = Block`.
Enable the feature explicitly in the existing migration registration:

```csharp
var options = existingOptions with
{
    LegacyRevisions = LegacyRevisionHandling.RepairOnStartup,
    IncompleteBehavior = IncompleteMigrationBehavior.ContinueWithAvailableRecords,
    MaximumRevisionRepairsPerAttempt = 256 // Optional; default is int.MaxValue.
};

services.AddKeyedSingleton<IStorageMigrationLegacyRevisionRepair<IMyContract>>(
    "messages", (_, _) => new TimestampStorageMigrationRevisionRepair<IMyContract>(
        new Idb.Libraries.Implementation.Serialization.MonotonicRevisionTimestampGenerator()));

// In the existing AddStorageMigration factory, pass this final constructor argument:
// revisionRepair: sp.GetRequiredKeyedService<IStorageMigrationLegacyRevisionRepair<IMyContract>>("messages")
```

Register the logical repository with `AddStorageMigration` after its private providers.
This composes key reservations with the provider's existing identity factory; consumers
still depend on abstractions. Do not expose raw source/destination registrations to
application code. Custom destinations must supply equivalent mutation admission and
current revision-floor capabilities before opting into partial availability, through
`IStorageMigrationWriteReservations` and `IStorageMigrationDestination<T>.ReadRevisionFloorAsync`.

```csharp
var repository = provider.GetRequiredKeyedService<IRepository<IMyContract>>("messages");
var migration = provider.GetRequiredKeyedService<IStorageMigrationProgress<IMyContract>>("messages");
await repository.InitializeAsync(cancellationToken);
// Show migration.Progress.PendingRecords when Phase is Incomplete.
// Healthy records can now be queried, saved, added or deleted through repository.
await migration.RetryAsync(cancellationToken); // Explicit continuation after a budget/deferred repair.
```

Every startup attempts repair automatically when configured. `RetryAsync` is an
explicit continuation, not polling or an automatic reopen after a fault. A repair
implementation may return null to defer a record; exceptions propagate. Completed
migrations do not rerun repair. After an uncertain destination failure, use fresh
provider instances and the durable receipt, as before. Do not delete receipts.

The first attempt validates the entire source and imports healthy records before
returning partial availability; the repair budget does not bound total startup time.
Do not upgrade to a new contract version until an interrupted older target stage can
finish: the coordinator will not expose records in the wrong requested schema.

During partial recovery, receipts contain revision assignments and published key
ownership, so memory/receipt size grows with the affected dataset. The File receipt
store has a configurable 16 MiB default limit and rejects oversized state before
replacement; configure an appropriate bound for larger deployments. Assignments are
written once before an import pass and completion compacts the receipt. Actual stored
records, rather than per-record journal rewrites, determine recovery progress. No
throughput improvement or large-store performance claim is made.

## Review items retained for decision

| Item not adopted | Current disposition |
| --- | --- |
| Require every valid empty source to opt in | Rejected; valid existing empty stores remain supported. Missing folders must still fail. |
| Remove the migration registration globally after one release | Rejected for pending/skipped-release installations. Per-installation retirement is now implemented; see the follow-up. |
| Disable ordinary File watchers, hydration or all earlier initialization | Rejected; preserve normal File behavior and use fresh migration inventories. |
| Restart the process after every error or cancellation | Rejected; safe preflight failures can be retried. Faulted destination providers still require fresh instances. |
| Delete all unkeyed DI services | Rejected; gate owned aliases, preserve unrelated registrations and capabilities. |
| Drop read-back or weaken completed-startup verification | Deferred unless equivalent integrity guarantees are demonstrated. |
| Automatic fallback, abort/quarantine or receipt deletion | Not added. Verified original-source cleanup after completion is now authorized; unknown, changed and unmigrated records remain protected. |
| Silently rewrite all revisions | Rejected. Explicit startup repair of **missing** revisions is now implemented; positive revisions are unchanged. |
| Concurrent imports | Deferred pending measurements, bounded memory and provider/serializer/transform concurrency qualification. |
| Change TCP retry or feedback safeguards to hide the intermittent timeout | Rejected without a reproduced cause; add targeted diagnostics if it recurs. |

Recommended follow-up priorities: controlled recovery ergonomics, consistent configured
same-provider startup orchestration outside this coordinator, then measured parallel
imports and equivalent-integrity startup checks. Physical power loss, Android and
large-store qualification remain separate validation work.
