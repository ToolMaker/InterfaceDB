# Provider-to-provider storage migration

Available under MIT in `InterfaceDB.Repositories`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

Storage migration moves an interface's committed data from one provider to another.
The first supplied source adapter is File; the destination adapter composes the
existing committed-snapshot, conditional-import and revision-clock capabilities.
File → SegmentCore is covered by real provider tests. Other providers need their
own exclusion/integrity integration and qualification; this is not a claim of
tested Atlas or remote migration support.

## Startup and completion

Register the old and new providers under private infrastructure keys, then register
`AddStorageMigration<T>(logicalKey, factory)`. Application code receives the logical
`IRepository<T>`. Its `InitializeAsync` executes migration; the first ordinary
operation also awaits that initialization. Prefer explicitly awaiting initialization
before exposing the application, starting listeners or accepting requests.

`RunOnStartup = true` enables the operation. A stable `MigrationId` identifies the
handover. Keep the upgrade path available for installations that skip releases. A
durable completion receipt prevents subsequent startups from copying again, and
`RetireAfterCompletion = true` (the default) retires each completed installation.
That installation can then use the destination-only registration described below.
Configuration files are never rewritten at runtime. Setting `RunOnStartup` false
blocks an unfinished migration and postpones retirement/cleanup; it does not prevent
an already completed destination from opening.

The coordinator:

1. Locks the receipt and acquires the source provider's exclusive migration lease.
2. Captures committed source data, including decrypted logical values through the
   registered serializer/lifecycle, without triggering read-driven key rotation.
3. Preflights all records and records a manifest digest using a fresh committed File
   inventory under exclusion. The coordinator holds keys/hashes, and the source
   snapshot holds identities; provider catalogs also contribute to total memory.
   Configured record/byte limits bound the operation, not the process's total heap.
4. Requires an empty destination on the first attempt. On resume, checks existing
   destination records against expected content, key, version, revision and metadata.
5. Transfers the durable revision reservation floor, including reservations for
   records that have since been deleted. Then conditionally imports missing records
   under their exact physical keys and original positive revisions.
6. Verifies each import by reading it back, saves progress, verifies the complete
   destination and runs its supplied durability/integrity callback.
7. Initializes destination indexes, durably publishes completion, then exposes the
   destination repository. Source files remain available as a backup unless verified
   cleanup is explicitly enabled. The explicitly
   configured partial-availability mode below can instead publish a verified subset
   while leaving the migration incomplete.

A crash between a committed record and a receipt update is recoverable: restart
with fresh provider instances and compatible configuration. Existing matching records
are verified and reused. A changed source, conflicting target or mismatched receipt
stops startup. It does not overwrite conflicting data or automatically fall back.
Provider fault handling remains unchanged; the coordinator does not reopen a faulted
store in place. Once destination initialization/mutation begins, a failure is retained
by that coordinator; later operations report it instead of silently retrying. Recover
with fresh providers. Safe preflight cancellation and a missing source restored before
destination initialization remain retryable. Valid empty existing sources remain supported.

After retirement, the source is not opened or required. During optional cleanup it
is inspected under exclusion; an unavailable source leaves cleanup pending without
blocking the verified destination. Normal destination changes
and deletes are retained; they are not replaced by stale source data on next startup.
Keep the destination and receipt together in backups. Missing destination revision
state, corrupt receipts and changed migration identity fail closed.
The destination also contains an encrypted `.idb-storage-handover.binding` identity.
Back up and relocate it with the store: the receipt is independent of absolute paths,
but cannot be attached to an unrelated destination. Contract/runtime type identities
exclude application assembly versions in canonical fingerprints while retaining the
serialized values and metadata. Receipt format 2 is not interchangeable with receipts
created by the earlier unshipped implementation; do not delete old receipts to force
a new handover. Use the matching build for controlled recovery of those test stores.

### Retiring an installation and cleaning its original source

Set these options on the full migration before completion:

```csharp
RetireAfterCompletion = true,
CleanupOriginalSourceAfterCompletion = true
```

Retirement is enabled by default; deletion of original source payloads is **opt-in**.
The coordinator captures the source's original fingerprints and relative file locations
in a durable cleanup plan. Only after every record is migrated, the destination is
verified and completion is durable does cleanup begin. Every source payload is checked
again under the source lease before deletion. Changed, new or unreadable records are
retained and reported. Incomplete migrations retain **all** source records and recovery
state, including records awaiting revision repair. Healthy destination records remain
readable and writable according to the partial-availability setting.

Cleanup failures leave `Phase = Complete` with `Retirement = Pending`, an optional safe
`RetirementIssue` code and `RetainedSourceRecords` when an inventory could be counted.
The completed destination remains usable; startup or explicit `RetryAsync` retries
cleanup. It never recopies old data over later destination edits or resurrects deletes.
An original-source cleanup backlog also survives subsequent configured version upgrades.

Successful retirement removes the per-record cleanup/recovery lists from the receipt.
Keep the small completion marker and destination binding: deleting them would remove
the evidence needed to distinguish an already migrated installation. The File adapter
deletes only verified original payloads, not source directories, clocks, provider recovery
files, unrelated files or previous SegmentCore version stores. There is no recursive
source deletion. Cleanup requires old application instances to remain stopped.

`RetiredStorageMigration<T>` supplies a destination-only registration with the same
logical repository interface. It requires a matching retired marker, migration identity,
destination instance and contract version; absent/incomplete markers fail instead of
opening an empty database. See the [registration example](storage-migration-retirement.md#removing-source-configuration).
Keep the full registration available to pending and skipped-release installations.
Later contract upgrades still require their supported migration/history configuration.

Choose cleanup before retirement. An installation retired without cleanup keeps its
source backup; enabling deletion later does not silently reinterpret its compact marker.
To postpone retirement, use `RetireAfterCompletion = false` with cleanup disabled.
An older completed, unretired receipt can prepare cleanup only if its full original
inventory still matches the recorded manifest. Cleanup plans share the receipt's
configurable size bound (16 MiB by default); configure it for the expected dataset.

## Version selection

Explicit same-version moves accept any nonblank label when running, source and
destination labels are exactly equal (ordinal comparison) and both version ceilings
are absent. For example, File `v1_0_0` can move to SegmentCore `v1_0_0` without
renaming folders, rewriting stored labels or changing application keys. A different
label, a version ceiling or `ContractVersion` selection requires numeric versions.
Unsupported ordering is rejected before transfer. Receipt downgrade protection and
explicit transforms for actual schema changes remain in force.

`StorageMigrationTrigger.Explicit` moves storage on startup without requiring a
contract version bump. Use this for File v1 → SegmentCore v1.

For version-driven selection use `StorageMigrationTrigger.ContractVersion`:

| Setting | Meaning |
| --- | --- |
| `RunningVersion` | Version selected by the owning application's repository configuration |
| `SourceMaxVersion` | Inclusive highest version served by the old provider |
| `DestinationMaxVersion` | Inclusive highest version served by the new provider |
| A null maximum | No upper bound for that provider |

Example: File maximum `1.0.0`, SegmentCore maximum omitted. At `1.0.0`, the logical
repository serves File. At `2.0.0`, it migrates to the SegmentCore registration
configured for `2.0.0`. If File's maximum is omitted, File continues indefinitely
under the version trigger; use `Explicit` for an intentional same-version move.
Overlapping ranges and a selected provider configured for the wrong running version
are errors. A receipt prevents coordinator-aware deployments from silently
reactivating File. Old binaries that do not understand it remain subject to the
deployment exclusion requirements below.

An unbounded provider range is not permission to load unsupported schemas. The
selected registration must support the running contract version. `UnchangedStorageMigrationTransform<T>`
accepts equal versions only. For a version change supply `IStorageMigrationTransform<T>`
with a stable identity and deterministic conversion using your contract migration
code. It returns a fresh detached target instance; it must not modify the source
store, have external side effects, or generate new random values on each invocation.
It can run more than once per record during preflight, verification and retry.

Later SegmentCore → SegmentCore contract upgrades remain ordinary contract migrations.
Leave the storage-handover registration enabled. Supply `IStorageMigrationHistory<T>`
to the coordinator for subsequent version upgrades. Its DI factory opens the requested
historical destination version and returns an owned `IStorageMigrationHistoryHandle<T>`
containing its source/read and destination/verification capabilities. For a local
SegmentCore provider, compose `LocalSource` and `LocalDestination` over that version's
private provider and give the handle ownership of its `ServiceProvider`. The provider's
ordinary exclusive store ownership remains in force until the handle is disposed.
Keep the destination's logical key and contract name stable across versions.

The same configuration then handles both paths: an already migrated device upgrades
the committed destination, including subsequent edits/deletes; a skipped-release
device converts File data directly through the supported transform chain. If an old
stage was interrupted, the coordinator first completes it using the history factory,
then upgrades to the current version. Transforms must retain their recorded semantics
for unfinished stages and support all relevant version paths. A missing transform or
historical provider fails clearly; opening an empty target is never treated as upgrade.
Opening a newer SegmentCore directory alone does not run contract migration.

The intended application experience is a configured upgrade during startup, with
normal access held until it completes; a separate manual maintenance command is not
required for this flow. "Offline" refers only to exclusive access during migration,
not internet connectivity. Legacy revision-0 repair is a separate, explicitly
configured metadata conversion and must not be confused with normal contract upgrades. See the
[qualification and remaining work](preview17-release-notes.md#qualification-and-future-work).

The history factory is configured once alongside the current destination. For example,
the application's `MakeSegmentConfiguration(version)` below returns the same contract,
logical database key and data root with the requested supported active version:

```csharp
var history = new StorageMigrationHistory<IMyContract>(async (version, ct) =>
{
    ct.ThrowIfCancellationRequested();
    var configuration = MakeSegmentConfiguration(version); // Application composition root.
    var registrations = new ServiceCollection();
    registrations.AddSegmentCoreRepository<IMyContract>(configuration,
        segmentOptions, destinationPolicy, encryptor: destinationEncryptor);
    var provider = registrations.BuildServiceProvider();
    try
    {
    var directory = Path.Combine(configuration.DataRoot!, segmentOptions.DirectoryName, version);
    var previous = StorageMigrationEndpointFactory.LocalDestination<IMyContract>(
        provider, configuration.EnvelopeDICacheKey, directory, publication, async token =>
        {
            var maintenance = provider.GetRequiredKeyedService<ISegmentCoreRepositoryMaintenance<IMyContract>>(
                configuration.EnvelopeDICacheKey);
            await maintenance.CheckpointAsync(token);
            if (!(await maintenance.VerifyIntegrityAsync(token)).IsValid)
                throw new InvalidDataException("Historical destination integrity check failed.");
        });
    var previousSource = StorageMigrationEndpointFactory.LocalSource<IMyContract>(
        provider, configuration.EnvelopeDICacheKey, directory, publication);
    return new StorageMigrationHistoryHandle<IMyContract>(previousSource, previous, provider);
    }
    catch { await provider.DisposeAsync(); throw; }
});
// Pass history as the final StorageMigration<T> constructor argument.
```

Register the same custom serializer, lifecycle and encryption dependencies in these
historical providers when the application uses replacements. The factory owns its
new provider: dispose it if factory construction itself throws before returning the
handle. Do not open two owners of the same SegmentCore directory.

## DI example: File → SegmentCore at the same contract version

`fileConfig` and `segmentConfig` are the application's existing
`IDbContractConfigDto<IMyContract>` implementations. Give them distinct
`EnvelopeDICacheKey` values (`"old-file"`, `"new-segment"`), distinct data roots and
the same active version. Keep the old File identity/cardinality, serializer and
encryption configuration so the source can still be read.

```csharp
using Idb.Commercial.Repositories.StorageMigrations;

var publication = PlatformFilePublication.Instance;
var filePolicy = fileConfig.StoragePolicy ?? StoragePolicy.Default;
var destinationPolicy = segmentConfig.StoragePolicy ?? StoragePolicy.Default;

services.AddFileRepository<IMyContract>(fileConfig,
    strategyType: MigrationStrategyType.None, storagePolicy: filePolicy);
services.AddSegmentCoreRepository<IMyContract>(segmentConfig,
    segmentOptions, destinationPolicy, encryptor: destinationEncryptor);

services.AddStorageMigration<IMyContract>("messages", sp =>
{
    var destinationRoot = Path.Combine(segmentConfig.DataRoot!,
        segmentOptions.DirectoryName, segmentConfig.ActiveVersion!);
    return new StorageMigration<IMyContract>(
        new StorageMigrationOptions
        {
            MigrationId = "messages-file-to-segment-v1",
            RunningVersion = segmentConfig.ActiveVersion!,
            RunOnStartup = true,
            Trigger = StorageMigrationTrigger.Explicit
        },
        StorageMigrationEndpointFactory.FileSource<IMyContract>(
            sp, "old-file", publication),
        StorageMigrationEndpointFactory.LocalDestination<IMyContract>(
            sp, "new-segment", destinationRoot, publication, async ct =>
            {
                var maintenance = sp.GetRequiredKeyedService<
                    ISegmentCoreRepositoryMaintenance<IMyContract>>("new-segment");
                await maintenance.CheckpointAsync(ct);
                if (!(await maintenance.VerifyIntegrityAsync(ct)).IsValid)
                    throw new InvalidDataException("Destination integrity check failed.");
            }),
        new UnchangedStorageMigrationTransform<IMyContract>(),
        new SerializerStorageMigrationFingerprint<IMyContract>(
            sp.GetRequiredKeyedService<BinaryObjectSerializer<IMyContract>>("new-segment"),
            "binary-logical-v1"),
        new FileStorageMigrationStateStore(receiptPath,
            "MyApplication:IMyContract:messages", destinationEncryptor, publication));
});

// At application startup, before accepting work:
var repository = servicesProvider.GetRequiredKeyedService<IRepository<IMyContract>>("messages");
await repository.InitializeAsync(cancellationToken);
```

Use the **same destination encryptor** for the receipt, including context-aware
encryption where configured. `EncryptionNone` is appropriate only for an unencrypted
destination. The receipt contains digests that also deserve protection.
Source encryption is registered under the source key using the existing InterfaceDB
DI mechanisms. Different source and destination encryption keys are supported.
Records and encrypted fields are decrypted with their source context and encrypted
with their destination context, including the destination contract version.

`SerializerStorageMigrationFingerprint<T>` requires a deterministic *logical*
serializer before its encryption decorator implementing `ICanonicalObjectSerializer<T>`.
The default example uses the existing
registered binary serializer. If the application replaces that format, inject the
matching deterministic serializer or implement `IStorageMigrationFingerprint<T>`.
Custom serializers may implement this focused capability or supply their own
`IStorageMigrationFingerprint<T>`. Do not pass randomized encrypted bytes as a logical equality fingerprint. Actual
database reads and writes always use each provider's registered serializer.

The coordinator depends on endpoint, transform, fingerprint and state-store
interfaces. Provider-specific implementations and lifetimes stay in DI. No provider
downcasts appear in the coordinator. Each logical repository/interface needs its
own receipt path and stable identity. Use `makeDefault: false` when registering
multiple logical repositories for the same interface and resolve them by key.
Provider-owned compatibility aliases are gated and routed to the selected provider;
unrelated repositories and application replacements are preserved. Resolve maintenance
and other infrastructure capabilities after awaiting logical initialization. The
same capabilities can be resolved by the logical repository key when registered by
the underlying provider. A capability unsupported by the selected provider reports
an error rather than operating on retired or partially migrated storage.
The normal metadata-write, retained-editing and disposable-notification capabilities
are forwarded. A subscription registered before initialization becomes ready only
after migration; imported records are not replayed as new application notifications.
Normal indexing and application-save notifications remain enabled inside the destination.
Every import, including partial-availability retry, uses `IEnvelopeImportRepository<T>`
to suppress application events for that operation only. No unsubscribe/re-subscribe
window or global notification flag is used.

Custom endpoint implementations/decorators must now implement `ServiceRegistrationKey`:
forward the exact provider DI key, or explicitly return null only for an endpoint with
no service registration. Alias ownership never depends on optional `GetService` lookups.
Omitting optional services does not grant access to the raw source. Requested cleanup
without its capability still fails before receipt publication or stays pending.

Custom state stores/decorators must implement/forward `ValidateCapacityAsync`. It checks
the proposed receipt without publishing it, including serializer/encryption overhead and
the partial-publication ownership map. The destination binding now separates nonmutating
`ProposeAsync` from `BindAsync`, which must bind that exact identity or reject a mismatch.
This lets the coordinator validate capacity before creating a binding, seeding the
destination clock or importing records. The bounded File receipt remains 16 MiB by
default; `MaximumRecords` is a separate limit, not a promise that any million-record
cleanup plan fits. Key/path lengths, repairs and encryption affect the required bound.
Source validation/inventory work still precedes exact capacity validation.

The File state store also proves exclusive-handle enforcement before acquiring its lease.
The default guard rejects disabled Unix locking and filesystems that allow a competing
exclusive open. This protects cooperating implementations; it does not qualify mobile
devices or protect against old processes that ignore the locking protocol.

## Ownership and supported boundaries

- Run on the machine that owns the database. This does not change server-owned
  contract versions or let a TCP client initiate server storage migration.
- Stop old application instances before cutover. The File lease blocks ordinary
  competing writes during transfer, but old binaries must not resume writing the
  retired File store after the lease is released. Opt-in verified source cleanup is
  described above; the completion marker cannot control unaware old binaries.
- Source and destination keys are infrastructure-only during transfer. Do not expose
  them as parallel application access to the staged/retired providers. Earlier source
  initialization no longer determines the migration inventory. Before the version
  boundary the normal source repository retains its watcher, hydration and application
  behavior; transfer reads committed snapshots without initializing that application
  path. Use fresh provider instances for recovery after destination failure.
- Use disjoint physical directories without directory links. The destination must
  be privately owned by this startup operation. SegmentCore supplies its ordinary
  exclusive store ownership; custom endpoints must provide equivalent exclusion.
- Destination commits must be Durable. Durability defaults and revision-conflict
  checks are not weakened. This operation does not invent a cross-provider transaction:
  the destination is staged and hidden until the completion barrier succeeds.
- Legacy zero-revision records fail safely by default. Enable the startup repair
  policy below to assign missing revisions during import. Ordinary positive revisions
  are preserved exactly, and source files are not rewritten.
- Retained edit sessions are process-local and do not survive restart. Close them
  before migration; ordinary retained editing works after cutover. This migration
  does not claim to archive live edit leases.
- This version adapter understands the current 16-byte durable revision-clock
  reservation format. A future format requires its own adapter; malformed values
  fail closed. Custom providers can implement `IStorageMigrationRevisionClock`.

## Verification and limits

The source suite is `tests/InterfaceDB.StorageMigrations.Tests`. It covers actual File and SegmentCore providers, encrypted fields, legacy repair, interruptions, relocation, partial availability, alias gates and retirement. See [release validation](preview17-validation.md) for this build. Android/iOS devices and physical power loss remain unqualified.
