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
| `RepositoryType` | Current provider selection; `FileSystem` is implemented. |
| `ObjectCardinality` | Whether a logical identity has one or multiple physical objects. |
| `StoragePolicy` | Default runtime tuning for the repository. |
| `ContractPath` | Contract-baseline source location used by maintenance tooling. |
| `ImplementationsPath` | Concrete implementation source location used by tooling. |
| `MigrationsPath` | Migration source location used by tooling. |

Search indexes are declared with `[SearchIndex]` on contract types or members. There is no separate configured-index array.

## Storage Policy

Create policies from the immutable default record:

```csharp
var policy = StoragePolicy.Default with
{
    DurabilityMode = StorageDurabilityMode.Durable,
    ObjectCacheMode = StorageObjectCacheMode.MetadataOnly,
    MaxConcurrency = 8,
    WatchStorageChanges = true
};

services.AddRepository<IPerson>(personOptions, storagePolicy: policy);
```

The registration argument overrides `personOptions.StoragePolicy`.

### Core Choices

| Setting | Default | Guidance |
|---|---:|---|
| `DurabilityMode` | `Performance` | Use `Durable` for proposal recovery and cross-process ordering. |
| `ObjectCacheMode` | `MetadataOnly` | Use `StrongObject` for a bounded hot dataset; `WeakObject` when the application should control lifetime. |
| `MaxConcurrency` | 12 | Tune against storage latency and payload size; more workers can make one device slower. |
| `BulkWriteBatchSize` | 4,096 | Lower it to reduce peak task/result memory. |
| `UseSingleWriteBuffer` | `false` | Often improves small/medium object writes at the cost of one payload byte array. |
| `HydrateOnInitialize` | `true` | Disable for faster write-heavy startup or metadata-only stores. |
| `RaiseNotifications` | `true` | Disable only if in-process lifecycle subscribers are not needed. |
| `WatchStorageChanges` | `true` | Disable when no external process edits the data root. |

### Coordination Settings

`UseSidecarWriteLocks` protects the same payload across processes. Keep it enabled for Durable multi-process use. `WriteLockTimeout` and `WriteLockRetryDelay` control acquisition behavior. `StaleWriteLockTimeout` must exceed the longest legitimate write; setting it too low can misclassify a slow live writer after a process restart.

The defaults favor conservative cleanup with a five-minute stale threshold. Recovery-sensitive applications can lower it only after testing their maximum payload/write duration.

### Watcher Settings

`StorageChangeDebounce` coalesces repeated filesystem events. `LocalStorageChangeSuppression` prevents a repository from processing its own write echo. Watchers are reconciliation mechanisms, not distributed event delivery; overflow/error paths rescan the store.

## Multiple Databases For One Contract

```csharp
services.AddRepository<IPerson>(localOptions, "LocalPeople");
services.AddRepository<IPerson>(secureOptions, "SecurePeople");

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
var secure = provider.GetInterfaceDbRepository<IPerson>("SecurePeople");
```

Use the same key when registering keyed encryption. The first registration also receives unkeyed compatibility aliases; applications with multiple stores should resolve explicitly by key.
