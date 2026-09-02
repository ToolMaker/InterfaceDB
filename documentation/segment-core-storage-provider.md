# InterfaceDB SegmentCore Storage Provider

SegmentCore is InterfaceDB's append-only, striped storage provider for .NET 10. It plugs in below the same `IRepository<TContract>` and query/index layers as the file-per-object provider. Application code continues to depend on interfaces; the composition root chooses the storage implementation.

The provider is a production candidate. Its process-crash behavior, atomic transactions, bounded recovery, full/delta checkpoint fallback, retained-WAL seek boundaries, paged primary locators, bounded WAL recycling, group commit, compaction, online backup/restore, explicit provider migration, persisted secondary-index catalogs, streaming reads, format compatibility enforcement, data-larger-than-managed-memory execution, and InterfaceDB integration are implemented and tested. Actual physical-power-loss evidence and the first-stable-version decision remain release work. Multi-process live-store ownership is an explicit product boundary; bare-metal Linux/NVMe testing is lower-priority characterization for hardware-specific claims.

## When To Use It

Choose SegmentCore when you want:

- object-native local or embedded persistence without a server or connection string;
- high write throughput on NVMe storage;
- atomic `SaveManyAsync` operations within one contract store;
- exact, range, prefix, suffix, and ordinal contains queries through InterfaceDB's existing index layer;
- append-only recovery with checksummed frames and a mutation WAL;
- bounded background checkpoints and copy-forward compaction;
- encryption and polymorphic object recovery behind `IRepository<TContract>`.

Choose the file-per-object provider when every object must remain a separately inspectable, copyable, or independently recoverable file. Choose SQLite or a server database when relational joins, foreign keys, multiple writer processes, SQL tooling, replication, or cross-store transactions are requirements.

## Install And Register

The package project is `InterfaceDB.Storage.SegmentCore`, targets .NET 10, and is currently versioned `1.0.0-preview.5`. While working from this repository, reference the project directly. The consumer command is:

```powershell
dotnet add package InterfaceDB.Storage.SegmentCore --prerelease
dotnet add package Microsoft.Extensions.DependencyInjection
```

Set the repository type and register the provider at the composition root:

```csharp
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Storage.SegmentCore;
using Microsoft.Extensions.DependencyInjection;

var config = new PersonOptions(); // RepositoryType returns ContractRepositoryType.SegmentCore

var services = new ServiceCollection();
services.AddSegmentCoreRepository<IPerson>(config);

await using var provider = services.BuildServiceProvider();
var people = provider.GetRequiredService<IRepository<IPerson>>();
await people.InitializeAsync();
```

`AddSegmentCoreRepository` registers the complete stack: envelope serialization, encryption hooks, polymorphic lifecycle, cache, InterfaceDB search indexes, query planner, repository facade, SegmentCore store, and maintenance controls.

## Android Support

The file-per-object provider and SegmentCore support .NET 10 Android applications when their data
root is an app-owned persistent internal-storage directory. Use `Context.FilesDir` in a .NET for
Android application or `FileSystem.AppDataDirectory` in .NET MAUI. Do not use the cache directory,
shared storage, removable storage, or a hard-coded `/data/...` path for an authoritative store.

The Android storage path uses the same managed random-access I/O as desktop builds. SegmentCore
holds its root-owner lease and object-log writer leases for the store lifetime through exclusive
`FileShare.None` opens and uses POSIX `fsync` for durable directory publication. Keep the
one-writer-process boundary: two Android processes must not open the same live SegmentCore root.

Recommended mobile policy:

```csharp
var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.LowMemory) with
{
    WatchStorageChanges = false,
    MaxConcurrency = 1,
    MaxReadConcurrency = 1
};
```

`FileSystemWatcher` is not part of the current Android qualification. Leave
`WatchStorageChanges` disabled unless an application has a tested requirement for another process
to edit the file-per-object root. Dispose the dependency-injection provider asynchronously so
pending search-index catalog publication and SegmentCore maintenance can complete without blocking
the Android UI synchronization context:

```csharp
await using var provider = services.BuildServiceProvider();
```

InterfaceDB uses runtime type discovery, reflection-backed indexes, and MessagePack resolvers. It is
therefore not currently declared trim-safe. A .NET Android application that opts into full trimming
must preserve its contract implementations and the InterfaceDB serialization path. The following
configuration was used by the Android Release qualification; omit the SegmentCore root in a
file-provider-only application:

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="$(AssemblyName)" RootMode="All" />
  <TrimmerRootAssembly Include="Idb.Libraries" RootMode="All" />
  <TrimmerRootAssembly Include="InterfaceDB.Storage.SegmentCore" RootMode="All" />
  <TrimmerRootAssembly Include="MessagePack" RootMode="All" />
</ItemGroup>
```

This is a preservation boundary, not a claim that InterfaceDB is warning-free under full trimming.
The current Android gate passed a trimmed .NET 10 Release build with Mono AOT on an Android 15/API
35 x64 emulator: durable file-provider save/reopen/index lookup, SegmentCore atomic save,
checkpoint/reopen/index lookup, raw byte save, and raw-store reopen. NativeAOT (`PublishAot`) is not
qualified. Real arm64 hardware, abrupt process termination, low-storage behavior, and physical
power-loss durability remain required before making a device-level durability claim.

`WindowsCredentialEncryptionKeyStore` is Windows-only. Android applications that enable encrypted
fields must provide an application-owned key-store implementation backed by an appropriate Android
secure-storage facility.

## Apple Platform Support

The file-per-object provider and SegmentCore are prepared for ordinary .NET 10 macOS applications,
.NET for iOS applications, and Mac Catalyst applications. Apple runtime qualification must still be
performed on a Mac with the matching Xcode and .NET workload before treating those platforms as a
release durability claim.

Use an app-owned persistent directory. For .NET MAUI on iPhone or Mac Catalyst,
`FileSystem.AppDataDirectory` is the portable starting point. Do not use the cache or temporary
directory for authoritative data, and do not place a live SegmentCore root in iCloud Drive or a
shared app-group container without separately qualifying the one-writer-process boundary.

SegmentCore acquires its lifetime root-owner lease and its object-log writer leases by opening their
lock files with `FileShare.None` on every supported platform. On Unix, .NET implements that
exclusive open as an advisory, open-description `flock`, so symlink or bind-mount aliases still
contend and closing an unrelated descriptor cannot release the lease. An ordinal in-process path
guard remains as a fast supplemental check; the filesystem lease is the identity authority. Do not
disable .NET file locking for a root that could be opened by another process.

`FileSystemWatcher` is unsupported on iOS, so InterfaceDB automatically omits it there even if
`WatchStorageChanges` is `true`. Direct file-provider operations and explicit rescans continue to
work. The watcher is available on macOS and Mac Catalyst; disabling it is still appropriate when
all changes come from the current process.

Apple application suspension is a separate lifecycle boundary. .NET file streams use advisory
locks on Unix, and an iOS application can be terminated if it is suspended while holding a file
lock. Stop writes and asynchronously dispose the InterfaceDB service provider or repository before
the application is suspended, then recreate and initialize it after returning to the foreground.
The file-per-object provider normally holds locks only during writes; SegmentCore intentionally
keeps its writer lease and active segment open for the lifetime of the store.

The current Apple path uses managed file flushes and atomic same-volume moves, but does not yet
claim a qualified durable directory barrier or physical power-loss guarantee on APFS. A Mac gate
must exercise save, checkpoint, abrupt process termination, reopen, duplicate-writer rejection,
free-space exhaustion, and iPhone background/foreground transitions. Apple documents `fsync`,
`F_BARRIERFSYNC`, and `F_FULLFSYNC` with different durability and device-wear tradeoffs; selecting
an Apple-specific barrier is intentionally deferred until those measurements are available.

iOS applications are AOT-compiled and trimmed. InterfaceDB is not currently declared trim-safe, so
the Android preservation roots above also apply to an iOS Release application. A Mac build must
confirm the actual linker warnings and preserve application contract implementations. The optional
.NET Native AOT deployment mode remains unqualified.

`WindowsCredentialEncryptionKeyStore` is not available on Apple platforms. Applications that
enable encrypted fields must supply an application-owned key-store implementation backed by the
Keychain or another appropriate Apple secure-storage facility.

## Normal Repository Use

Callers use the same repository API as any other InterfaceDB provider:

```csharp
var saved = await people.SaveManyAsync(new IPerson[]
{
    new Student { Name = "Jane", Age = 22 },
    new Employee { Name = "John", Age = 41 },
    new SeniorCitizen { Name = "Mary", Age = 72 }
});

await foreach (var person in people.SearchManyAsync(
    person => person.Age >= 18 && person.Name.StartsWith("Ja")))
{
    Console.WriteLine(person.Name);
}

await people.DeleteAsync(saved[0]);
```

### Repository Lifetime And Initialization

Treat the built dependency-injection provider, and therefore each registered repository, as an
application-lifetime service. Do not build a new provider or open the same SegmentCore root for each
request or query. The keyed envelope repository is the single disposable owner of the store; the
public repository and maintenance views share that owner.

`InitializeAsync` is concurrency-safe and single-flight. Concurrent first callers share one store
activation and one index initialization. Later calls use an already-initialized fast path; they do
not reopen SegmentCore or rebuild the repository. Cancellation stops an individual caller from
waiting but does not cancel the shared initialization needed by other callers. Catalog-only
metadata activation also cannot consume a later explicit hydration request: when eager hydration
is enabled, that later request shares one upgrade pass. Asynchronously
dispose the service provider once, at the end of the application lifetime, so dirty derived index
catalogs and final SegmentCore maintenance are completed in the correct order.

A raw-store open benchmark intentionally measures a fresh store lifetime, such as application
startup or process restart. It is not a per-query connection cost and should not be interpreted as
one.

One `SaveManyAsync` call is one atomic SegmentCore transaction: either every serialized envelope becomes visible or none does. A normal `SaveAsync` is an atomic one-object transaction. Transactions are scoped to one SegmentCore contract store; they are not ambient or distributed transactions across repository roots.

An update must preserve InterfaceDB identity by modifying an object returned by `SaveAsync`,
`SaveManyAsync`, or a query and then saving that tracked object. With
`StorageObjectCardinality.Multiple`, a detached instance has no envelope and is intentionally a new
physical object under its logical storage key; it is not an implicit upsert. Load by storage key
before replacing values when the caller only has an external identifier:

```csharp
var person = await people.QueryFirstByStorageKeyAsync(personId)
    ?? throw new KeyNotFoundException(personId);
person.Name = "Updated name";
await people.SaveAsync(person);
```

## Production Defaults

The provider defaults to:

- eight hash-routed stripes;
- mutation-bearing write-ahead logging;
- explicit-flush durability;
- two independent checkpoint recovery roots;
- no-op checkpoint reuse and compact delta checkpoints for small dirty sets;
- a 4 MiB minimum WAL-reclaim threshold and a 64 MiB forced-recycle threshold;
- a checkpoint check every minute after at least 1,000 mutations;
- compaction eligibility every 15 minutes after at least 100,000 mutations;
- 64 records per copy slice and one stripe rotation per preparation slice;
- 64 MiB free-space reserve before compaction;
- a final checkpoint during orderly disposal when mutations remain.

These defaults favor portable, conservative durability and short maintenance pauses. Configure them explicitly for a qualified deployment:

```csharp
using Idb.Libraries.Abstractions.Enums;
using Idb.Libraries.Abstractions.Options;
using Idb.Storage.SegmentCore.ObjectLog;

var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.WindowsNvme) with
{
    ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
    MaxReadConcurrency = 8
};

services.AddSegmentCoreRepository<IPerson>(config, storagePolicy: policy);
```

When provider options are omitted, SegmentCore resolves the effective `StoragePolicy` hardware
profile into complete concrete options. For selective or custom tuning, seed the provider options
from a profile and override only the required values:

```csharp
var segmentOptions = SegmentCoreRepositoryOptions
    .ForHardwareProfile(StorageHardwareProfile.HighThroughput) with
{
    CheckpointInterval = TimeSpan.FromMinutes(2),
    MinimumMutationsPerCheckpoint = 10_000,
    CompactionInterval = TimeSpan.FromMinutes(30),
    MinimumMutationsPerCompaction = 250_000,
    CompactionOptions = new SegmentCoreCompactionOptions
    {
        MaximumRecordsPerSlice = 64,
        MaximumStripesPerPreparationSlice = 1,
        MinimumFreeSpaceReserveBytes = 512L * 1024 * 1024
    },
    StoreOptions = new SegmentCoreOptions
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        CheckpointGenerationsToKeep = 2,
        WalRecycleMinimumReclaimBytes = 16L * 1024 * 1024,
        WalMaximumBytesBeforeForcedRecycle = 256L * 1024 * 1024,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 8,
            HashAlgorithm = SegmentCoreHashAlgorithm.Fnv1a32,
            HashSeed = 0,
            MaximumBatchRecords = 256,
            QueueCapacityPerStripe = 4_096,
            GroupCommitDelay = TimeSpan.FromMilliseconds(1),
            MaximumCombinedWriteBytes = 4 * 1024 * 1024,
            MaxSegmentBytes = 256L * 1024 * 1024,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        },
        ReadOptions = new SegmentCoreReadOptions
        {
            MaximumParallelReads = 16,
            MaximumReadWindowBytes = 8 * 1024 * 1024,
            MaximumReadGapBytes = 128 * 1024,
            SynchronousPointReadMaximumBytes = 64 * 1024,
            StreamingBatchRecords = 8_192
        }
    }
};

services.AddSegmentCoreRepository<IPerson>(config, segmentOptions);
```

`WriteThrough` is materially faster on the tested Windows/NVMe machine, but it is not the portable default. Select it only after storage-specific durability and power-loss qualification.

Explicit `SegmentCoreRepositoryOptions` take precedence over the policy profile. Every effective
setting is available through `ISegmentCoreRepositoryMaintenance<TContract>.Options`, including:

- storage directory, hardware-profile identity, and background-maintenance lifecycle;
- transaction protocol, durable filesystem implementation, checkpoint generations, and WAL recycle bounds;
- resident/paged/automatic primary-locator mode and the automatic paging threshold;
- stripe count, persisted hash algorithm/seed, queue behavior/capacity, group-commit delay, write batch/coalescing limits,
  segment size, and flush strategy;
- physical read parallelism, read-window size, read-gap coalescing, point-read threshold, and streaming batch size;
- checkpoint/compaction intervals and mutation thresholds;
- compaction slice sizes, inter-slice delay, free-space admission, and reserve.

### Complete Option Surface

| Repository option | Portable default |
|---|---:|
| `HardwareProfile` | `Portable` |
| `DirectoryName` | `.segment-core` |
| `CheckpointInterval` | 1 minute |
| `MinimumMutationsPerCheckpoint` | 1,000 |
| `CompactionInterval` | 15 minutes |
| `MinimumMutationsPerCompaction` | 100,000 |
| `EnableBackgroundMaintenance` | `true` |
| `CheckpointOnDispose` | `true` |

| Store/log option | Portable default |
|---|---:|
| `TransactionProtocol` | `MutationWriteAheadLog` |
| `CheckpointGenerationsToKeep` | 2 |
| `DurableFileSystem` | platform implementation |
| `WalRecycleMinimumReclaimBytes` | 4 MiB |
| `WalMaximumBytesBeforeForcedRecycle` | 64 MiB |
| `PrimaryIndexOptions.Mode` | `Automatic` |
| `PrimaryIndexOptions.AutomaticPagingMinimumEntries` | 100,000 |
| `StripeCount` | 8 |
| `HashAlgorithm` | `Fnv1a32` |
| `HashSeed` | 0 |
| `MaximumBatchRecords` | 64 |
| `QueueCapacityPerStripe` | 1,024 |
| `QueueFullBehavior` | `Wait` |
| `GroupCommitDelay` | zero |
| `MaxSegmentBytes` | 64 MiB |
| `MaximumCombinedWriteBytes` | 1 MiB |
| `DurabilityStrategy` | `ExplicitFlush` |

| Read/compaction option | Portable default |
|---|---:|
| `MaximumParallelReads` | 8 |
| `MaximumReadWindowBytes` | 4 MiB |
| `MaximumReadGapBytes` | 64 KiB |
| `SynchronousPointReadMaximumBytes` | 64 KiB |
| `StreamingBatchRecords` | 4,096 |
| `MaximumRecordsPerSlice` | 64 |
| `MaximumStripesPerPreparationSlice` | 1 |
| `DelayBetweenSlices` | zero |
| `RequireFreeSpaceAdmission` | `true` |
| `MinimumFreeSpaceReserveBytes` | 64 MiB |

Frame sizes, checksums, alignment, format versions, commit markers, and corruption bounds remain
internal format invariants rather than performance options. Changing those would define a storage
format migration, not a hardware profile.

### Hash Routing Choices

SegmentCore supports `Fnv1a32`, `Fnv1a64`, `XxHash64`, and `XxHash3` for assigning an object key to
one of its bounded append stripes. `Fnv1a32` with seed zero exactly preserves the original
`preview.3` route and manifest. The selected algorithm and seed are persisted in both low-level and
store-format manifests. Reopen, backup, and restore validate them before mutation; changing either
requires an explicit migration into a new store. `HashSeed` is a deterministic distribution input,
not a password or cryptographic secret.

The maintained .NET 10/Windows comparison used 500,000 keys, 16 stripes, three key shapes, zero
allocation in every routing loop, and five real SegmentCore write/checkpoint/reopen iterations:

| Algorithm | Raw routing speed vs `Fnv1a32` | End-to-end writes vs `Fnv1a32` | Guidance |
|---|---:|---:|---|
| `Fnv1a32` | 1.00x | 1.00x | Compatibility default and best general starting point. |
| `Fnv1a64` | 0.96x | 0.97x | Available for controlled experiments; no measured reason to prefer it. |
| `XxHash64` | 1.48x | 1.02x | Faster routing, but only a small storage-layer gain. |
| `XxHash3` | 1.96x | 1.04x | Best measured opt-in candidate for routing-heavy writes. |

A focused 100,000-record raw-payload confirmation measured `XxHash64` at 1.41x raw routing speed and 1.02x
geometric-mean write throughput, while `XxHash3` reached 1.86x and 1.05x respectively. `XxHash3`
improved writes by about 2.7% for sequential keys, 2.6% for the long common-prefix shape, and 8.7%
for GUID keys in that run. Checkpoint and reopen results did not show one consistently superior
algorithm. This evidence supports an opt-in choice but does not justify changing the durable
default. Benchmark representative application keys before opting in:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-hash-routing `
  --routing-keys=500000 `
  --storage-records=100000 `
  --iterations=5
```

## Operations And Health

Resolve the typed maintenance surface for manual operations and monitoring:

```csharp
var maintenance = provider
    .GetRequiredService<ISegmentCoreRepositoryMaintenance<IPerson>>();

SegmentCoreProviderStatus status = maintenance.Status;
StoragePolicy effectivePolicy = maintenance.StoragePolicy;
SegmentCoreRepositoryOptions effectiveOptions = maintenance.Options;
SegmentCoreCheckpointResult checkpoint = await maintenance.CheckpointAsync();
SegmentCoreIntegrityResult integrity = await maintenance.VerifyIntegrityAsync();

if (!integrity.IsValid)
{
    throw new InvalidDataException(string.Join(Environment.NewLine, integrity.Failures));
}

SegmentCoreCompactionResult compacted = await maintenance.CompactAsync();
Console.WriteLine(
    $"Reclaimed {compacted.BytesReclaimed:N0} bytes; " +
    $"longest write pause {compacted.LongestExclusiveSlice.TotalMilliseconds:N2} ms");

SegmentCoreBackupResult backup = await maintenance.BackupAsync(
    @"E:\Backups\People-2026-08-26");
```

Health data includes live entries, WAL transaction outcomes, replay counts, repaired tail bytes, queue pressure, dirty stripe count, pending checkpoint changes, current maintenance state, last maintenance durations, and the last background-maintenance exception.

### Runtime Metrics

SegmentCore emits listener-driven `System.Diagnostics.Metrics` telemetry under the meter
`InterfaceDB.Storage.SegmentCore`. No listener means the instruments are disabled, so ordinary
operation does not allocate a parallel event stream. OpenTelemetry and `MeterListener` consumers
can subscribe to:

| Instrument | Unit | Purpose |
|---|---|---|
| `interfacedb.segmentcore.open.stage.duration` | ms | Store-open cost split by manifest, checkpoint, transaction coordinator, object-log recovery, and replay stage. |
| `interfacedb.segmentcore.operation.duration` | ms | Open, integrity, checkpoint, compaction, backup, and restore latency. |
| `interfacedb.segmentcore.operation.count` | operation | Success/failure count for those operations. |
| `interfacedb.segmentcore.storage.bytes` | bytes | Checkpoint, compaction-before/after/reclaimed, backup, and restore sizes. |

Open and operation measurements carry `transaction.protocol` and `result` tags. Stage timings add
`stage`; storage measurements use `component`. Avoid high-cardinality values such as store paths or
object keys in metric tags. On the historical 100,000-record raw-payload fresh-process workload, the measured
open stages were 43.10 ms for the primary checkpoint, 32.92 ms for WAL validation, 30.38 ms for
object-log recovery, 8.42 ms for replay, and 10.88 ms across the two compatibility manifests. A
prototype that overlapped WAL and object-log work made median open slower through same-device I/O
contention, so the sequential implementation was retained.

Integrity verification reads and checksum-validates every live frame and verifies that a valid checkpoint exists at or after the in-memory generation. Run it after restore, before backup promotion, and periodically according to the deployment's risk profile.

`BackupAsync` creates an online, point-consistent snapshot without stopping subsequent source
writes. It rotates mutable segment tails, publishes a full checkpoint, copies only immutable
prefixes through that checkpoint, rebases the transaction coordinator at the captured watermark,
hashes every copied file with SHA-256, opens and verifies the staged backup, and only then publishes
the destination directory. A backup/restore target is never overwritten.

Restore into a new, offline directory and then point a repository configuration at that root:

```csharp
SegmentCoreRestoreResult restored = await SegmentCoreStore.RestoreBackupAsync(
    @"E:\Backups\People-2026-08-26",
    @"D:\Recovered\People\1.0.0",
    maintenance.Options.StoreOptions);
```

The backup includes encrypted stored payloads but deliberately excludes external encryption keys.
The SHA-256 manifest detects accidental or hostile file changes only when the manifest itself is
trusted; sign or protect the backup manifest separately when authenticity is required.

## Crash And Maintenance Model

An explicit transaction writes derived data frames and then queues the complete mutation set for a checksummed WAL commit. Concurrent commits can share one durable WAL flush without sharing transaction visibility. If a process stops after WAL commit but before every derived frame reaches stable storage, reopen reconstructs missing frames from the WAL. A checkpoint flushes only dirty stripes under a short mutation barrier, captures immutable index state, releases writers, and publishes the checkpoint in the background. An unchanged checkpoint reuses its generation; a small dirty set publishes a compact delta once independent fallback roots exist. WAL recycling advances only through the oldest retained recovery root and avoids replacing a small WAL until the configured reclaim or maximum-size threshold is reached.

When a WAL is intentionally retained below its recycle threshold, SegmentCore writes a checksummed
seek sidecar tied to the checkpoint generation, WAL generation, prior committed record, and SHA-256
of the skipped prefix. Reopen validates that evidence before starting at the checkpoint boundary.
The sidecar is a non-authoritative performance hint: it is published without a second durable
metadata barrier, and a missing, stale, torn, or corrupt copy causes a complete WAL scan. This
preserves recovery correctness while avoiding a durable sidecar write on every checkpoint.

The primary locator checkpoint is authoritative for reopen. Full checkpoints can also publish a
checksummed hash/offset page map. `Resident` decodes all key strings and locators at open; `Paged`
validates the checkpoint and compact map but reads individual key/locator entries on demand;
`Automatic` selects paging from 100,000 live entries by default. A corrupt or absent map falls back
to the validated resident checkpoint. Small automatic/resident stores do not pay to create a page
map. The raw store therefore has bounded reopen allocation, although the current InterfaceDB
repository adapter still enumerates physical keys to seed envelope metadata and search-index state.

InterfaceDB's exact, range, RadixForge prefix/suffix, and ordinal-contains structures also have a checksummed derived catalog beside the store. The catalog includes schema and primary-locator fingerprints. A warm matching catalog initializes indexes without deserializing every payload; runtime types are resolved once per distinct type and contains-index compaction is deferred until bulk installation completes. A missing, corrupt, stale, or schema-incompatible catalog is ignored and rebuilt from authoritative objects.

Compaction is copy-forward and interruption-safe. It rotates stripes in bounded preparation slices, copies live frames in bounded record slices, publishes two independent full checkpoints without holding the mutation barrier during file publication, retires readers briefly, and only then deletes superseded segments. Free-space admission requires enough room for another live copy plus the configured reserve. Cancellation or process termination before deletion leaves extra frames, not lost objects; a later compaction can reclaim them.

## Maintained Performance Snapshot

Measurements below are local .NET 10 results from the repository's Windows/NVMe machine on 2026-08-26. Each transaction path encodes or binds complete six-field objects, durably acknowledges writes, reopens the store, and separately materializes complete objects with count and checksum validation. SQLite uses WAL with `synchronous=FULL`. Results are medians of five fresh stores with 5,000 objects. SegmentCore reopen is a provider-open metric; the SQLite counterpart is connection-open. Both stop before the separately timed materialization pass.

| Atomic batch | SegmentCore explicit flush | SQLite | SegmentCore result |
|---:|---:|---:|---:|
| 1 | 296 records/s | 292 records/s | 1.01x faster |
| 10 | 2,930 records/s | 2,920 records/s | 1.00x faster |
| 100 | 27,553 records/s | 26,191 records/s | 1.05x faster |
| 1,000 | 166,914 records/s | 137,569 records/s | 1.21x faster |
| Independent durable writes | 301 records/s | 297 records/s | 1.01x faster |

| Atomic batch | SegmentCore Windows write-through | SQLite | SegmentCore result |
|---:|---:|---:|---:|
| 1 | 2,607 records/s | 301 records/s | 8.67x faster |
| 10 | 15,885 records/s | 2,900 records/s | 5.48x faster |
| 100 | 99,910 records/s | 24,198 records/s | 4.13x faster |
| 1,000 | 303,783 records/s | 136,790 records/s | 2.22x faster |
| Independent durable writes | 4,368 records/s | 300 records/s | 14.54x faster |

Explicit-flush checkpoints were 15.4-20.9 ms after mutation-WAL atomic workloads and 7.63 ms after already-flushed independent writes. Write-through checkpoints were 9.7-14.5 ms after atomic workloads and 7.24 ms after independent writes. SegmentCore provider-open completed in 5.0-13.0 ms, while SQLite connection-open completed in 0.36-0.55 ms. Neither reopen number includes the separately measured complete-object scan; complete-object materialization remained workload-dependent once open.

When one maintenance checkpoint is amortized across 5,000 records, write-through SegmentCore
remained 8.61x, 5.34x, 3.51x, and 1.40x faster than SQLite at batches 1, 10, 100, and 1,000.
Portable explicit flush was 1.01x faster after maintenance at batch 1, effectively tied at batches
10 and 100, and 1.18x slower at batch 1,000. Checkpoint frequency therefore remains an explicit workload policy,
not a cost hidden inside write latency.

With 5,000 live objects and two obsolete update generations, the latest maintenance case reclaimed 2,154.9 KiB, reducing 6,740.4 KiB to 4,585.5 KiB. The production bounded configuration limited the longest observed exclusive slice to 14.88 ms under explicit flush and 8.79 ms under Windows write-through. Total compaction was 178.25 ms and 148.84 ms respectively across 89 short slices.

Process termination was injected after durable commit and immediately before and after WAL replacement; every maintained scenario recovered all 128 committed objects. These are reproducible engineering measurements, not universal hardware claims.

### Scale And Recovery Qualification

The maintained write-through storage-core scale gate creates complete deterministic payloads, performs atomic batched ingestion, publishes a full checkpoint, proves no-op generation reuse, checksum-validates every live frame through the bounded streaming path, reopens with zero tail replay, and validates deterministic sampled raw-payload reads. It does not instantiate repository objects. The latest one-million-record A/B run isolates primary-locator policy:

| 1,000,000 records | Ingestion | Full checkpoint | Integrity scan | Raw-store reopen | Reopen allocation | 10,000 sampled raw-payload reads |
|---|---:|---:|---:|---:|---:|---:|
| Resident locator | 286,655 records/s | 653.2 ms | 403,335 records/s | 927.2 ms | 139.9 MiB | 131,930 reads/s |
| Paged locator | 278,641 records/s | 915.2 ms | 408,379 records/s | 42.6 ms | 25.2 MiB | 79,774 reads/s |

Paging made raw-store reopen 21.8x faster and reduced reopen allocation by 82.0%. It added 262.0 ms to the full checkpoint and reduced the sampled random-read rate by 39.5%. This is why `Automatic` pages at 100,000 live entries rather than forcing one policy on every store. Resident mode also avoids writing the page-map sidecar. In both runs the unchanged checkpoint took about 1.5 ms and allocated 1,728 bytes.

Run the gates directly:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-scale --records 1000000 --durability write-through --primary-index resident --output segment-core-scale-1m-resident.json
dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-scale --records 1000000 --durability write-through --primary-index paged --output segment-core-scale-1m-paged.json
```

The scale report includes live payload bytes, the runtime's managed-memory budget, their ratio,
whether the dataset exceeded that budget, the reopened locator mode, and skipped WAL-prefix bytes.
The maintained larger-than-memory qualification used `--checkpoint-every-batches` to bound retained
WAL growth and `--require-larger-than-memory` to fail unless live payload exceeded the runtime's
measured memory budget. It stored 34,000 one-MiB raw-payload records: 35.65 GB live versus 34.26 GB available
managed memory (1.041x), then passed final checkpoint, a 34.37-second full integrity scan, 932.8 ms
raw-store reopen with a fully verified 755 MB retained WAL prefix, and 502.5 sampled one-MiB payload reads/s. It did not instantiate repository objects.

Physical power-loss qualification is intentionally a two-boot procedure. Point `--root` at a dedicated test volume, start the writer, remove power only after acknowledged batches are reported, reboot, and run the validator against the same root:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-power-loss-writer --root E:\InterfaceDbPowerLoss
dotnet run -c Release --project StorageBenchmarks.Transactions -- --segment-core-power-loss-validate --root E:\InterfaceDbPowerLoss
```

The harness uses alternating checksummed acknowledgement ledgers. Validation requires every ledger-acknowledged record and its complete deterministic payload to survive. Any records newer than the newest durable ledger must form complete fixed-size transaction batches with no missing key. A finite 20-batch/640-record two-boot rehearsal passed, but graceful process exit does not substitute for physically removing power on each supported storage/controller/filesystem combination.

The automated raw-store qualification runner combines a deterministic mixed read/write/delete workload,
periodic checkpoints, full model comparison after reopen, integrity verification, and an injected
ENOSPC-equivalent durability-flush failure. The latest Windows engineering run seeded 100,000
1 KiB payload records and sustained five minutes of mixed activity: 81,473,282 operations completed,
provider reopen took 72.99 ms, and disk-exhaustion recovery passed. The current .NET 10 Linux Docker
run passes all 123 SegmentCore tests, including symlink-alias ownership and shutdown-admission races,
with zero skips. The separately maintained full gate passed all 49 integration scenarios and 12/12
forced-termination cycles. Its full strict-durability admission nevertheless failed: SegmentCore was
only 5.4% faster than SQLite direct at p50, below the required 15%. Docker remains virtualized evidence,
not bare-metal Linux certification, and the performance miss is a disclosed release limitation.

The repository-level production pilot uses `IRepository<T>` rather than the raw store. Its maintained
10,000-object/five-cycle run finished with 10,500 live objects after atomic updates and inserts,
indexed deletes, and exact/range/prefix/suffix/contains/conjunction validation on every cycle. It
then passed checkpoint, integrity, bounded compaction, verified online backup, warm reopen with the
persisted index catalog, restore, catalog rebuild, and complete object comparison. Compaction reduced
10,855,466 bytes to 5,243,488 bytes, reclaimed 5,611,978 bytes across 52 slices, and held the longest
exclusive slice for 18.87 ms. The verified 4,399,000-byte backup took 373.73 ms; repository reopen
took 557.05 ms and restored-repository open with index rebuild took 357.55 ms on this machine.

The current 30-second raw-store mixed-load rerun performed 12,641,604 operations, reopened the provider in 17.91 ms, and
passed the injected ENOSPC-equivalent recovery case. The current quick Linux run passed all 93
SegmentCore correctness tests and all three forced-termination cycles; its 11.1% strict p50 lead
again missed the deliberately conservative 15% virtualized performance threshold.

The corrected 2026-09-02 fresh-process comparison stored the same set of 100,000 deterministic
256-byte `PhaseZeroCodec` payloads in both engines and performed 25,000 deterministic reads per worker.
Both sides decoded each payload, instantiated a complete six-field `GateRecord`, validated every field
and content character, and produced the same parent-verified checksum. Across three final runs,
median post-open throughput was 67,442 complete objects/s for SegmentCore versus 38,806/s for SQLite
(1.74x). SQLite statement creation and preparation happened before its read timer. Median read-path
allocation was 28.0 MB versus 42.5 MB. The older 64,624-versus-31,182 result is retained only as
historical raw-payload evidence because that earlier harness did not instantiate objects.

Median raw SegmentCore activation was 83.07 ms, down from one same-harness pre-change observation of
103.5161 ms (19.76%). Recovery now overlaps independent checkpoint/WAL/object-log work but still
validates the authoritative state and holds the root-owner lease from before recovery through store
disposal. SQLite read-only connection open was 16.32 ms p50. Those open numbers stop before the first
query and object, but they do not represent equal startup work and are not ranked against each other.
The operating-system cache was not evicted, and the read-process SegmentCore worker captured stage
telemetry.

The separate five-run repository-lifecycle qualification measured 5,405.07 ms p50 for a deliberately
missing catalog, with exactly 100,000 payload deserializations, and 2,172.41 ms p50 for a valid warm
catalog, with zero payload deserializations. Time through the first fully materialized, validated
six-field object was 5,414.12 ms and 2,243.65 ms respectively. A second
`IRepository.InitializeAsync` in the same provider lifetime measured 0.0087 ms p50 across ten calls,
allocated zero bytes, and performed zero deserializations. This is why the service provider and its
singleton repository should live for the application lifetime rather than be recreated per request.
The startup runner uses a fresh process and byte-identical store clone per sample, disables synchronous
stage listeners by default, and does not evict the operating-system page cache.

Four concurrent SQLite reader processes materialized 100,000 objects at a median aggregate 86,728 reads/s
across the three final runs.
SegmentCore currently permits one owning process per live store, so its same-store group qualified one
fully materialized reader plus three explicit ownership rejections and deliberately reports no aggregate
multi-reader throughput. Treat this as an explicit deployment boundary, not a performance result.

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-qualification `
  --duration-seconds 60 `
  --records 100000 `
  --payload-bytes 1024 `
  --readers 4 `
  --output segment-core-qualification.json
```

Run the isolated repository-lifecycle qualification with:

```powershell
dotnet run -c Release --project StorageBenchmarks.Transactions -- `
  --segment-core-startup --records=100000 --iterations=5 `
  --worker-timeout-seconds=300 --output=segment-core-startup.json
```

The JSON report separates `EngineeringPass` from `ProductionCertificationComplete`. Docker proves
Linux code-path portability but not physical controller durability. An actual power cut remains
required before making the strongest hardware-durability claim. Bare-metal Linux/NVMe performance
is now lower-priority reference characterization rather than a blocker for the provider API; it is
still required before publishing machine-specific performance claims for that profile. The
larger-than-managed-memory gate is complete.

Local report names from the final commands:

- `segment-core-transactions-release-optimized-explicit.json`
- `segment-core-transactions-release-optimized-write-through.json`
- `segment-core-scale-release-optimized-1m-resident.json`
- `segment-core-scale-release-optimized-1m-paged.json`
- `segment-core-scale-larger-than-memory-release-optimized.json`
- `segment-core-qualification-windows-5m-release-optimized.json`
- `artifacts/phase-zero/linux-release-full-20260826.json`
- `segment-core-read-process-release-optimized.json`
- `artifacts/benchmark-audit/read-process-materialization-100k.json`
- `artifacts/benchmark-audit/segment-open-final-1.json` through `segment-open-final-3.json`
- `artifacts/benchmark-audit/segment-core-startup-cold-warm-100k-5x-final.json`
- `segment-core-read-process-linux-latest.json`
- `segment-core-production-pilot-full.json`
- `segment-core-qualification-current-30s.json`
- `segment-core-upgrade-preview2-current.json`
- `segment-core-limits-readiness.json`

## Compatibility Policy

SegmentCore uses three separate compatibility axes; changing one does not silently redefine the
others:

1. **Public package API.** Stable packages follow semantic versioning. Removing a public member,
   changing its signature or behavior contract, or narrowing accepted input requires a package
   major version. Additive APIs require a minor version; compatible fixes use a patch. Preview
   packages may still change before the first stable release. The test suite hashes the complete
   exported API surface for the reviewed `1.0` baseline, so every addition or break requires an
   explicit compatibility review and baseline update.
2. **Storage engine format.** Every store contains a checksummed `storage-format.manifest` with
   engine `1.0`, component versions, and its configured hash-routing identity. The original/default
   identity is `fnv1a-utf16le-hash-stripes-v1`. A runtime accepts the same engine major and no newer engine or
   component minor than it understands. Unsupported future formats, changed routers, and corrupt
   manifests fail before mutation with `SegmentCoreCompatibilityException`. Router identity and
   transaction protocol are immutable for an existing store.
3. **Serialized contract envelopes.** New writes use
   `InterfaceDB.BinaryEnvelope.IDB4`; readers retain `IDB2` and `IDB3` compatibility. Application
   contract changes use versioned contracts and registered adjacent `IMigration` steps. Old readers
   are not promised forward compatibility with a newer envelope. Do not remove a legacy reader or
   migration step while any live store or supported backup may still contain that version.

Object-log frames, full/delta primary checkpoints, mutation WAL records, participant commits, and
backup manifests are authoritative, checksummed artifacts with explicit versions. Primary page
maps, retained-WAL seek boundaries, and secondary search-index catalogs are derived accelerators;
their absence or failed validation falls back to authoritative scanning/rebuilding. A page map may
therefore be introduced or discarded without changing object durability semantics.

An upgrade must pass old-store open/read/mutate/reopen tests, backup restore, migration, corruption
fallback, the public API baseline, and clean package consumption. Take and verify an online backup
before upgrading durable production data. Downgrading after a newer runtime has written a newer
format is not guaranteed unless that exact path has its own tested migration.

`Scripts/Test-SegmentCorePackageUpgrade.ps1` automates the old-package path. The current gate created
a paged, two-stripe mutation-WAL store with the locally retained `1.0.0-preview.2` package baseline, opened and
mutated it with current source, checkpointed it, verified integrity, backed it up, restored it, and
verified both the old and new values after reopen. The complete path passed.

## Current Boundaries

- One writer process owns a store root. Multiple readers inside that process are supported.
- Atomicity covers one contract store, not several roots or external providers.
- Multiple-cardinality detached instances are inserts by design. Updates preserve the envelope of a loaded/tracked object; an explicit detached-upsert API is a separate future product decision.
- Secondary search indexes are persisted as rebuildable derived catalogs. The in-memory structures are still populated on startup, but matching catalogs avoid payload hydration; they are not lazy on-disk indexes.
- The raw primary locator can be paged, but the current repository adapter still creates one envelope metadata skeleton per physical key during initialization.
- SegmentCore provider migration is explicit, source-preserving, and within one contract/version chain; it is not an ambient cross-store transaction.
- Online backup/restore is local point-in-time copy tooling, not replication, incremental backup, or remote snapshot orchestration.
- Relational constraints, joins, vector/full-text indexes, cross-store transactions, and multiple writer processes are not included.
- Process-kill testing, virtualized Linux correctness, disk-failure injection, the two-boot power-loss harness, and true data-larger-than-managed-memory execution pass. An actual physical power cut remains external evidence and gates stable `1.0.0`; bare-metal Linux/NVMe and cold-media runs are lower-priority hardware-profile characterization. A 15% virtualized-Linux p50 lead remains an advisory optimization target, not a release blocker.

These are explicit product boundaries. They do not weaken the durability claims inside the supported one-process, one-store model.
