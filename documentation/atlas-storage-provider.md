# Atlas hybrid storage provider

Available under MIT in `InterfaceDB.Storage.Atlas`, starting with `1.0.0-preview.17`.
Use matching InterfaceDB versions. Existing namespaces and assembly identities are
retained for compatibility; `Commercial` in an API name is historical, not a license restriction.
See the [package map and upgrade guide](preview17-release-notes.md).

## What the hybrid does

- Prefixes of the stable **physical envelope key**, encoded as UTF-8 bytes, own physical file sets.
  They are not the search-index keys and are not necessarily user-visible characters. Supply an
  identity factory if your domain categories should correspond to physical key prefixes.
- In the default dynamic layout, prefix groups are created lazily. Each owns an append-only segment sequence, a primary-address
  dictionary and a bounded writer queue. `bucket-00000042` is an immutable group identity; the durable
  prefix map explains its ownership. Segment rollover does not create another logical writer.
- Normal single saves/deletes use the owning writer's durable append path. Other buckets can write
  concurrently up to the configured I/O limit. This is not a direct assignment to NVMe hardware queues.
- A leaf exceeding its live-record **or** live-frame-byte threshold becomes eligible for a split.
  Only that branch is split. Common byte runs are skipped until a useful distinction exists; no
  chain of single-child splits is produced. At least two resulting children must meet the configured
  minimum population. Smaller additional children and short/exact-prefix keys are preserved.
- Repeated replacement of one key does not count as additional live records. Oversized identical
  bounded prefixes cannot be distributed; the operation is deferred with an inspectable reason.
- New child prefixes discovered after a split are registered lazily. Future growing children can
  split again. There is no hash-distribution fallback and no automatic merge.

Example: with a threshold of 30, `ABC` holding five objects remains one leaf. `ABE` with 32 objects
distributed between `ABEF` and `ABEG` becomes a routing parent with those two physical child groups.
If `ABEF` subsequently grows, it can split further while `ABC` and `ABEG` remain unchanged. A key
equal to a routing parent's prefix remains owned by that parent. Ten writable owners with an I/O
limit of sixteen still provide at most ten simultaneously active owning writer batches; an I/O
limit of four bounds them to four. Historical parent queues are not extra foreground parallelism.

## Configuration

```csharp
using Idb.Storage.AtlasCore;
using Idb.Libraries.Abstractions.Options;

var profile = AtlasCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.WindowsNvme);
var options = profile with
{
    DirectoryName = ".atlas-hybrid", // choose a fresh location, not an existing legacy store
    StoreOptions = profile.StoreOptions with
    {
        MaximumConcurrentPartitionWrites = 8,
        Partitioning = new AtlasPartitionOptions
        {
            InitialPrefixBytes = 3,
            MaximumPrefixBytes = 64,
            MaximumPartitions = 1_024,
            EnableAutomaticSplitting = true,
            SplitAboveLiveRecords = 100_000,
            SplitAboveLiveBytes = 64L * 1024 * 1024,
            MinimumChildRecords = 16,
            CheckEveryMutations = 64,
            RetryDelay = TimeSpan.FromSeconds(30),
            MaximumSplitRecords = 1_000_000,
            MaximumSplitBytes = 256L * 1024 * 1024,
            MaximumSplitDuration = TimeSpan.FromSeconds(30),
            MinimumFreeSpaceReserveBytes = 64L * 1024 * 1024
        }
    }
};
services.AddAtlasCoreRepository<IMyContract>(config, options,
    storageIdentityFactory: myStableIdentityFactory);
```

These are **initial, uncalibrated defaults**, not measured optimal thresholds. Increasing partition
capacity consumes more metadata/queue/handle capacity; it does not increase the I/O concurrency limit.
`MaximumPartitions` includes routing parents. Committed IDs are not recycled. If capacity is exhausted,
splits defer; a new prefix needing another owner is rejected before writing data. Existing owners
remain usable. On reopen, capacity can change only if it still accommodates all committed owners.
Prefix encoding/depth and the transaction protocol are format-bound and cannot be changed on reopen.

The first insertion in a new prefix pays durable ownership-map publication and group activation.
Existing-prefix lookup does not rebuild the map or allocate a prefix string. Registration snapshots
and map publication currently scale with total owners; the capacity bound is intentional.

`EnableBackgroundMaintenance` controls existing checkpoint/compaction scheduling, **not** splitting.
Set `StoreOptions.Partitioning.EnableAutomaticSplitting = false` to disable automatic splits.
Manual evaluation remains available through raw `AtlasCoreStore` or the non-owning DI alias:

```csharp
var maintenance = provider.GetAtlasCoreMaintenance<IMyContract>(databaseKey);
var result = await maintenance.SplitBucketAsync(saved.Envelope!.EnvelopeCacheKey);
await maintenance.FlushPartitionMaintenanceAsync(); // drain observed work after foreground writers finish
var owners = maintenance.Partitions;
var diagnostics = maintenance.PartitionMaintenanceStatistics;
```

Manual evaluation still respects thresholds and safety budgets; it does not force a split. Diagnostics
report attempts, published/deferred counts, copied bytes, failures and last reason/error. Background
failures do not reverse an already acknowledged foreground save. They remain visible in diagnostics;
`FlushPartitionMaintenanceAsync` reports recorded background failures rather than silently succeeding.
The current error record is sticky for the open-store lifetime. Disposal waits for owned split work.

## Security and resource admission

Atlas applies caller and recovery limits before durable work or large allocation. `AtlasCoreOptions`
defaults to at most 16,384 mutations in one atomic transaction, a 512 MiB aggregate encoded
transaction budget and 256 MiB for one recovery metadata file. Each put/delete must also fit the
256 MiB physical group-frame limit. Exact entry and transaction sizes are calculated before payload
copies or participant frames are staged for both transaction protocols; deterministic rejection does
not fault the live store. Repository atomic batches are copied into an owned, bounded snapshot while
they are enumerated, so a mutable or inconsistent caller collection cannot bypass the count boundary.

Backup and restore copy only the versioned Atlas layout. Unknown files or directories, noncanonical
names, symbolic links, junctions, reparse points and linked parent components are rejected. Both
operations default to at most 1,000,000 files, 4 TiB total, 1 TiB per file, depth 12 and two hours,
with 64 MiB of destination free space retained. Configure these limits through
`AtlasCoreBackupOptions` and `AtlasCoreRestoreOptions`; keep tighter deployment-specific limits when
the expected store is smaller. The existing four-argument `RestoreAsync` overload uses the defaults,
and the five-argument overload accepts explicit restore options.

These path checks stop static link traversal. A hostile local actor able to replace filesystem names
concurrently can still race ordinary path-check/open APIs. Do not accept backups from an untrusted
custody boundary until the planned handle-pinned no-follow implementation and authenticated inventory
are complete. Run backup and restore with least filesystem privilege.

Provider directory and active-version names must each be one safe path segment, and the final
canonical store root must remain beneath `DataRoot`. The provider also inherits the public IDBE v3
context binding, frozen per-contract runtime-type allowlist, bounded payload parsing and
encrypted-index protections. `StoragePolicy.MaximumStoredObjectBytes` is enforced for Atlas
repository reads and writes. Provider-native migration supplies the same stable physical-key context
used by normal reads. Historical-key or explicitly admitted contextless records are rewritten to the
active contextual frame after a successful read only when the stored bytes still match.

Atlas does not register the File provider's `IEncryptionRotationOperation<T>`. A controlled upgrade
must enumerate every Atlas record under the temporary migration setting, checkpoint and verify the
store, and then disable the setting. Legacy typeless payload conversion remains an offline migration
concern.

## Fixed segregation for steady-state benchmarks

`EnableAutomaticSplitting=false` alone does not freeze a dynamic layout: unseen prefixes can still
create owners, and manual splitting remains possible. Use `FixedPrefixes` when a run must have
exactly four pre-created physical groups and **no** layout changes:

```csharp
var fixedFour = profile with
{
    DirectoryName = ".atlas-hybrid-fixed-four",
    EnableBackgroundMaintenance = false,
    CheckpointOnDispose = false,
    StoreOptions = profile.StoreOptions with
    {
        MaximumConcurrentPartitionWrites = 4,
        Partitioning = new AtlasPartitionOptions
        {
            InitialPrefixBytes = 3,
            FixedPrefixes = ["000", "001", "002", "003"],
            MaximumPartitions = 4,
            EnableAutomaticSplitting = false
        }
    }
};
services.AddAtlasCoreRepository<IMyContract>(config, fixedFour,
    storageIdentityFactory: myStableIdentityFactory);
// Resolve IRepository<IMyContract> and await InitializeAsync BEFORE steady-state timing.
// Physical keys must start with one of the four configured prefixes, e.g. "002/item-00000042".
```

Four means the number of owners, not prefix depth or segment-file count. Prefixes must be distinct,
valid UTF-8 strings of exactly `InitialPrefixBytes` bytes. `FixedPrefixes` is an immutable array;
declaration order is canonicalized and has no effect on stable owner IDs. The I/O concurrency limit
is independent: four owners with a limit of two may run at most two normal writer batches at once.

`AtlasCoreStore.OpenAsync` and repository `InitializeAsync` activate every configured fixed group
before returning, without inserting dummy entities. This initialization cost must be reported
separately, not hidden as a claimed startup improvement. All subsequent accepted keys use the
existing groups; unknown prefixes are rejected before storing that record. Fixed mode disables
manual splitting too (`SplitBucketAsync` returns `Published=false`, `Reason="fixed-layout"` without
starting maintenance). Normal segment rollover and the normal commit/index/serialization work
still happen. This measures steady-state storage, not total lifecycle cost with partition maintenance.

The fixed prefix set/mode is durable. Reopen requires the same set (any declaration order is fine).
It cannot silently become dynamic or change prefixes; use a separate fresh store for split tests.
Fixed ownership maps use version 2, which older dynamic-only readers reject; dynamic maps remain
version 1. Initial activation is marked complete only after every owner's files are durable.
Failed initial activation can be retried, while missing files from an already-ready owner fail closed.

Before and after every steady-state timing window, assert four owners, zero split depths and an
unchanged placement generation; require zero partition-maintenance attempts/published splits and
no unexpected initialization/compaction/checkpoint work inside timing. Do not use an enormous split
threshold as a substitute for the fixed-layout guarantee.

## Commit, concurrency and recovery guarantees

1. A split takes the shared maintenance gate and excludes new store mutations, draining admitted
   writers. It captures current live records of one eligible parent. Reads may continue.
2. It copies opaque payload bytes into unpublished child groups, subject to record/byte/free-space
   admission and a cooperative elapsed-time/cancellation budget. It does not deserialize domain objects.
3. Child frames and checkpoints become durable before publication of a checksummed ownership-map
   generation. Candidate handles are ready before the new map becomes visible in memory.
4. Ownership publication is the split commit. Cancellation is no longer accepted once it begins.
   An uncertain publication faults the store; reopen resolves the durable result. Reads that started
   before publication can complete against old addresses; subsequent writes route to the new owners.
5. Pre-publication failure leaves the parent authoritative. Recovery removes only uncommitted tail
   child groups. Post-publication recovery uses the committed children. A corrupt newest map or
   missing committed split-child files fails closed, rather than falling back and resurrecting data.

**Current latency tradeoff:** split copying pauses foreground mutations store-wide. This is not a
fully nonblocking repartitioner. Bounds are cooperative: a filesystem flush already in progress
cannot be promised a hard deadline. Splits and compaction cannot run simultaneously. The existing
independent compaction mode remains available, but a lower-pause live split is future work.

Parent files/index entries are retained until retention-aware compaction: in-flight reads and leased
edit bases must remain valid. This temporarily duplicates physical data and some index metadata;
full scans filter through the current ownership map. Reclamation is not immediate and must be
included in memory/read/recovery benchmarks. A split preserves revisions, logical keys and payloads;
it emits no repository Created/Changed/Deleted event. Existing application index updates and notification
semantics stay in the repository layer.

Explicit/multi-object transactions retain coordinated atomic commit and the store-wide mutation
boundary. Single records do not enter that machinery. One contract store is the atomic boundary,
with one owner process per live store root. This does not add cross-store transactions, remote
authentication, multi-process writers or new query snapshot-isolation promises.

## Format and migration

The hybrid has a distinct `IDBATHYB` format identity, durable prefix ownership map and stable WAL
address capacity. Old Atlas 1/2/3 stores are rejected. Do not rename directories or copy old group
files under a new manifest. Keep the old reader/source and a backup, then perform an explicitly
validated data-copy migration into a fresh hybrid store. A legacy-Atlas-to-hybrid migration tool
has **not** been implemented by this work. Existing migration APIs are not a promise that old Atlas
formats can now be opened. Backup/restore and compaction of hybrid stores are tested separately.

## Qualification and remaining work

Use the active hybrid provider; historical Atlas 1/2/3 formats are not accepted. Keep the old reader and a backup if migrating such data. Adaptive tuning, sibling merge and cold-writer eviction remain roadmap work. Splits pause mutations while copying. See [release validation](preview17-validation.md) for tested platforms; physical power-loss and mobile-device qualification remain outstanding.
