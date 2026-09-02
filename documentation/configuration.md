# Configuration Guide

## Contract Configuration

Every repository receives an `IDbContractConfigDto<TContract>`:

| Member | Purpose |
|---|---|
| `EnvelopeDICacheKey` | Default DI/database registration key. |
| `ContractName` | Logical contract/storage name. |
| `FileExtension` | Payload extension, normally `.mpk`. |
| `DataRoot` | Root directory containing active-version folders. |
| `ActiveVersion` | Current persisted contract version such as `1.0.0`. |
| `RepositoryType` | Provider selection: `FileSystem` or `SegmentCore`. |
| `ObjectCardinality` | Whether a logical identity has one or multiple physical objects. |
| `StoragePolicy` | Default runtime tuning for the repository. |
| `ContractPath` | Contract-baseline source location used by maintenance tooling. |
| `ImplementationsPath` | Concrete implementation source location used by tooling. |
| `MigrationsPath` | Migration source location used by tooling. |

Search indexes are declared with `[SearchIndex]` on contract types or members. There is no separate configured-index array.

## Storage Policy

Create policies from the immutable default record:

```csharp
var policy = StoragePolicy.ForHardwareProfile(StorageHardwareProfile.Portable) with
{
    DurabilityMode = StorageDurabilityMode.Durable,
    ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
    MaxConcurrency = 8,
    WatchStorageChanges = true
};

services.AddRepository<IPerson>(personOptions, storagePolicy: policy);
```

`StoragePolicy` is the provider-neutral layer. It carries the hardware profile, repository
concurrency, read concurrency, batching, caching, notification, and coordination intent.
Use `ForHardwareProfile` rather than changing only the `HardwareProfile` property: the factory
also seeds the matching concurrency, batching, buffer, and hydration values.

| Hardware profile | Provider-neutral starting point | SegmentCore starting point |
|---|---|---|
| `Portable` | 12 write workers, 12 read workers, 4,096-object bulk window | 8 stripes/read groups, explicit flush, 1,024-write queues, 64 MiB segments |
| `LowMemory` | At most 2 workers, 256-object window, no eager hydration | 2 stripes, 256-write queues, 16 MiB segments, small read/compaction windows |
| `HighThroughput` | CPU-scaled bounded concurrency, 8,192-object window, 64 KiB file buffers | 8 stripes, larger queues/write coalescing/read windows, explicit flush |
| `WindowsNvme` | CPU-scaled bounded concurrency and larger buffers | Qualified 8-stripe Windows write-through profile used by the maintained NVMe benchmarks |
| `Custom` | Application-owned values | Requires explicit `SegmentCoreRepositoryOptions` |

Profiles are reproducible starting points, not hardware detection. In particular,
`WindowsNvme` must be selected deliberately after validating the actual filesystem, controller,
power-loss behavior, and tail latency.

The append-only provider has its own registration path and maintenance options:

```csharp
using Idb.Storage.SegmentCore;

services.AddSegmentCoreRepository<IPerson>(
    segmentCorePersonOptions,
    SegmentCoreRepositoryOptions.ForHardwareProfile(StorageHardwareProfile.HighThroughput) with
    {
        MinimumMutationsPerCheckpoint = 10_000
    });
```

See [SegmentCore Storage Provider](segment-core-storage-provider.md) before changing durability or compaction defaults.

The registration argument overrides `personOptions.StoragePolicy`.

For SegmentCore, configuration precedence is deterministic:

1. The `StoragePolicy` registration argument overrides the policy on the contract configuration.
2. When provider options are omitted, the effective policy's hardware profile creates a complete
   `SegmentCoreRepositoryOptions` instance; `MaxReadConcurrency` flows into physical read parallelism.
3. Explicit `SegmentCoreRepositoryOptions` win completely. Seed them with
   `ForHardwareProfile(...)` and use record `with` expressions for selective overrides.

The resolved policy and options are registered in dependency injection and are also available from
`ISegmentCoreRepositoryMaintenance<TContract>.StoragePolicy` and `.Options`. This makes the actual
repository, stripe, queue, read, transaction, checkpoint, and compaction settings observable at runtime.

The complete SegmentCore option graph also exposes `HashAlgorithm`, `HashSeed`, `GroupCommitDelay`,
`MaximumBatchRecords`, `MaximumCombinedWriteBytes`, `WalRecycleMinimumReclaimBytes`,
`WalMaximumBytesBeforeForcedRecycle`, `StreamingBatchRecords`, and
`SynchronousPointReadMaximumBytes`. Keep the WAL maximum greater than or equal to its minimum
reclaim threshold. A zero point-read threshold forces asynchronous point reads; the portable
64 KiB default uses a dedicated synchronous random-access handle only for small local frames.
These are physical provider settings, so prefer a named profile and change one variable at a time
under the maintained scale and transaction gates.

### Core Choices

| Setting | Default | Guidance |
|---|---:|---|
| `DurabilityMode` | `Performance` | Use `Durable` for proposal recovery and cross-process ordering. |
| `ObjectCacheMode` | `MetadataOnly` | Use `StrongObject` for a bounded hot dataset; `WeakObject` when the application should control lifetime. |
| `MaxConcurrency` | 12 | Tune against storage latency and payload size; more workers can make one device slower. |
| `MaxReadConcurrency` | 12 | Bounds provider read groups; SegmentCore also caps this at its selected profile's physical-read limit. |
| `BulkWriteBatchSize` | 4,096 | Lower it to reduce peak task/result memory. |
| `UseSingleWriteBuffer` | `false` | Often improves small/medium object writes at the cost of one payload byte array. |
| `HydrateOnInitialize` | `true` | Disable for faster write-heavy startup or metadata-only stores. |
| `RaiseNotifications` | `true` | Disable only if in-process lifecycle subscribers are not needed. |
| `WatchStorageChanges` | `true` | Disable when no external process edits the data root. |

### Coordination Settings

`UseSidecarWriteLocks` protects the same payload across processes. Keep it enabled for Durable multi-process use. `WriteLockTimeout` and `WriteLockRetryDelay` control acquisition behavior. `StaleWriteLockTimeout` must exceed the longest legitimate write; setting it too low can misclassify a slow live writer after a process restart.

The defaults favor conservative cleanup with a five-minute stale threshold. Recovery-sensitive applications can lower it only after testing their maximum payload/write duration.

### Watcher Settings

`StorageChangeDebounce` coalesces repeated filesystem events. File change delivery is currently
at-least-once, so a local repository notification can also arrive as a watcher echo.
`LocalStorageChangeSuppression` is retained as a compatibility placeholder for future exact-revision
acknowledgements; a path-only acknowledgement is deliberately not used because it could hide a newer
external commit. Watchers are reconciliation mechanisms, not distributed event delivery;
overflow/error paths rescan the store.

On Android, place `DataRoot` under the application's persistent internal files directory and set
`WatchStorageChanges = false` unless filesystem notifications have been qualified for the target
device. Do not place an authoritative InterfaceDB root in Android's cache directory. See
[SegmentCore Storage Provider](segment-core-storage-provider.md#android-support) for the Android
storage, trimming, AOT, and durability qualification boundary; the storage-location and watcher
guidance also applies to the file-per-object provider.

On iOS, place `DataRoot` in the application's persistent private container. InterfaceDB does not
start a `FileSystemWatcher` on iOS even when `WatchStorageChanges` is `true`, because .NET marks the
API unsupported there. Explicit rescans remain available. macOS and Mac Catalyst can use the
watcher, but applications that do not expect another process to edit the root can disable it.
Apple applications must also coordinate repository lifetime with application suspension; see
[SegmentCore Storage Provider](segment-core-storage-provider.md#apple-platform-support).

### Provider Applicability

The shared policy deliberately does not contain provider-format details. File buffering, sidecar
locks, and filesystem watchers apply to the file-per-object provider. SegmentCore's stripe, hash,
queue, segment, transaction, checkpoint, read, and compaction settings live in
`SegmentCoreRepositoryOptions`. Durable format identity cannot change after creation without
migration. The hardware profile connects the layers without making the core InterfaceDB package
depend on a particular storage engine.

Search-index persistence is automatic for SegmentCore repositories. The derived catalog has no
separate durability setting: it is accepted only when its checksum, contract schema, runtime-type
schemas, entry count, and primary-locator fingerprint all match. Otherwise InterfaceDB rebuilds it
from authoritative envelopes and republishes it.

## Multiple Databases For One Contract

```csharp
services.AddRepository<IPerson>(localOptions, "LocalPeople");
services.AddRepository<IPerson>(secureOptions, "SecurePeople");

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
var secure = provider.GetInterfaceDbRepository<IPerson>("SecurePeople");
```

Use the same key when registering keyed encryption. The first registration also receives unkeyed compatibility aliases; applications with multiple stores should resolve explicitly by key.
