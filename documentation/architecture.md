# InterfaceDB Architecture

InterfaceDB is an interface-first storage stack for storage-aware contract objects. Its current implementation is a file-system-backed repository that persists concrete objects behind versioned C# interfaces, using envelopes as the low-level storage metadata boundary.

The project is currently a sandbox/library rather than a production database engine. The active storage baseline is intentionally simple: a flat file layout aimed at desktop/mobile style local object storage. Enterprise storage backends such as SQL, document databases, or object databases are future roadmap work.

## Solution Layout

- `Idb.Libraries`
  Core abstractions and file-system implementation.
- `IdbDemoStorage`
  Demo contract/storage project for `Person`. This project is required by the solution and should be tracked as source.
- `InterfaceDb.ConsoleApp`
  Console smoke demo that writes and queries `IPerson` records.
- `InterfaceDb.IntegrationTests`
  Console integration harness used for process-level locking scenarios.
- `InterfaceDb.Tests`
  MSTest project discoverable by Visual Studio Test Explorer. It runs the integration harness.
- `MigrationTool`
  CLI for creating versioned contract baselines and migration stubs.
- `PerformanceCommon`
  Shared benchmark data factory.
- `StorageBenchmarks`
  BenchmarkDotNet comparison against InterfaceDB, SQL Server, Dapper, and EF Core.
- `StorageBenchmarks.Transactions`
  Transaction-oriented benchmark variant.

## Core Concept

Stored records are defined by interfaces. A contract interface must inherit `IStoredContract<TContract>` and be decorated with `[StorageScope]`.

Example:

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    Guid Id { get; }
    string Name { get; }
    int Age { get; }
    string Role { get; }
}
```

Concrete storage objects implement the contract and expose an `Envelope` property with a non-public setter:

```csharp
public IObjectEnvelope<IPerson>? Envelope { get; private set; }
```

This is intentional. InterfaceDB treats these as storage-aware objects, not pure domain objects. The envelope is repository-owned metadata that lets a loaded object retain its storage reference.

## Envelope Model

`IObjectEnvelope<TContract>` is the storage wrapper around a contract instance. It carries:

- `EnvelopeCacheKey`
- `VersionTag`
- `ObjectInstance`
- `Location`

The envelope is the low-level DB metadata boundary. Upper layers usually receive the object itself, not a separate envelope wrapper, but the object keeps a reference to its envelope.

This supports fast file-backed behavior:

- loaded objects know their source file;
- repeat saves do not need to rediscover location;
- version information travels with the object;
- cache identity is preserved.

## Repository API

The main application entry point is `IRepository<TContract>`:

- `InitializeAsync`
- `SaveAsync`
- `QueryAsync`
- `ExistsAsync`

`InterfaceRepositryDb<TContract>` validates that `TContract` is an interface and has `[StorageScope]`, then delegates work to `IIndexManager<TContract>`.

The current `IIndexManager<TContract>` implementation is `PassthroughIndexManager<TContract>`. It does not provide full secondary indexing yet. Secondary indexing is tracked as roadmap work.

## Dependency Injection

Repositories are registered with:

- `AddRepository<TContract>`
- `AddFileRepository<TContract>`
`ContractRepositoryType` currently exposes the file-system repository:

- `FileSystem`

The DI setup registers:

- serializer
- envelope repository
- index manager
- envelope lifecycle
- envelope cache
- reader/writer
- sidecar write lock
- file storage change watcher/handler
- optional migration strategy

## File Layout

Flat layout:

```text
Data/
  1.0.0/
    <key>.mpk
```

The active version comes from contract config, usually `IDbContractConfigDto<TContract>.ActiveVersion`. Sharded directory layouts were removed from the active implementation because they added migration and operational complexity before the core object database model was stable.

## Serialization

`BinaryObjectSerializer<TContract>` uses MessagePack and writes an `IDB2` payload format.

The current format stores:

- magic prefix: `IDB2`
- version tag
- envelope cache key
- concrete runtime type name
- serialized object payload

Envelope references are guarded during serialization to avoid object-envelope-object cycles.

Current limitation: payloads still depend on concrete .NET type names. Stable type aliases are a future hardening item.

## Envelope Lifecycle

`EnvelopeLifecycle<TContract>` handles wrapping and unwrapping.

On write:

1. If the object has no envelope, a new envelope is resolved from DI.
2. If the object already has an envelope, that envelope is reused.
3. The object's envelope reference is temporarily cleared.
4. The envelope receives the object instance.
5. The file-name factory initializes key/location.
6. The writer serializes the envelope.
7. The object is unwrapped and receives the envelope reference again.

On read:

1. The file is deserialized into an envelope.
2. The envelope carries the object instance.
3. `Unwrap` sets the envelope reference back onto the object.
4. The caller receives the storage-aware object.

## File Name Factories

`FlatFileNameFactory<TContract>` creates:

```text
<DataRoot>/<Version>/<EnvelopeCacheKey>.<extension>
```

If the envelope already has a location, the factory leaves it alone.

## Envelope Cache

The cache is currently an in-memory concurrent dictionary abstraction:

```csharp
IEnvelopeDictionaryCaheManager<string, IObjectEnvelope<TContract>>
```

During initialization, the file repository scans the data root and stores skeleton envelopes. Hydration is optional and controlled by `StoragePolicy.HydrateOnInitialize`.

If hydration is disabled, envelopes can remain lightweight until query/read time.

## Write Path

`FileObjectWriter<TContract>` performs the physical file write.

Current write flow:

1. Wrap object into envelope.
2. Resolve target payload path.
3. Ensure target directory exists.
4. Acquire sidecar write lock for the payload path.
5. Write serialized bytes to a unique temp file.
6. Commit temp file to payload path.
7. Update envelope cache according to policy.
8. Unwrap object and return it.

## Sidecar Write Locks

The write lock is implemented by `SidecarFileWriteLock<TContract>`.

For payload:

```text
<key>.mpk
```

the lock is:

```text
<key>.mpk.lock
```

Lock acquisition uses `FileMode.CreateNew`, which is atomic across processes. Only one process can create the lock file. The lock file is held open with `FileShare.None`.

The lock file stores small owner metadata:

- process-level owner GUID
- machine name
- process ID
- creation timestamp

Lock policy lives in `StoragePolicy`:

- `UseSidecarWriteLocks`
- `WriteLockTimeout`
- `WriteLockRetryDelay`
- `StaleWriteLockTimeout`

This lock is primarily for out-of-process synchronization. It serializes writes to the same payload file while allowing writes to different files to proceed independently.

## Atomic Commit

Writers do not write directly over the payload file.

They write to:

```text
<key>.mpk.tmp.<guid>
```

Then commit with:

- `File.Replace` if the payload already exists;
- `File.Move` if this is the first payload write.

The temp file is deleted on write failure. Successful writes should leave no `.lock` or `.tmp.*` files.

## Storage Change Watching

File repositories now include a low-level storage change layer.

Abstractions:

- `EnvelopeStorageChange`
- `EnvelopeStorageChangeType`
- `IEnvelopeStorageChangeSource<TContract>`
- `IEnvelopeStorageChangeHandler<TContract>`

File implementation:

- `FileEnvelopeStorageChangeSource<TContract>`
- `FileEnvelopeStorageChangeHandler<TContract>`

The source uses `FileSystemWatcher` against `DataRoot` with subdirectories enabled.

It watches committed payload changes and ignores transient files:

- `*.lock`
- `*.tmp.*`
- non-payload extensions

Events are debounced using `StoragePolicy.StorageChangeDebounce`.

## Storage Change Handling

The handler treats file-system events as signals. The file remains the source of truth.

Behavior:

- `Created` / `Changed`
  - read committed envelope from disk;
  - update envelope cache;
  - publish repository stored/added events as appropriate.
- `Deleted`
  - remove envelope from cache;
  - publish deleted event.
- `Renamed`
  - remove old cache key;
  - read new payload if valid;
  - publish removed/stored/added events as appropriate.
- `Error`
  - does not mutate cache.

This keeps the envelope cache closer to disk state when another process changes files.

Important limitation: existing object references already held by application code are not automatically replaced. The cache and pub/sub events update; subscribers can react and refresh their references if needed.

## Pub/Sub

`IContractPubSubService<TContract>` exposes repository events:

- added
- removed
- stored
- deleted

The file repository publishes these events for direct writes and for storage watcher changes.

Subscriptions are in-process. They are not a distributed event bus.

## Storage Policy

`StoragePolicy` controls file repository behavior:

- write concurrency;
- notification publishing;
- cache-on-write behavior;
- object retention in cache;
- single-buffer vs stream write;
- writer semaphore;
- sidecar locks;
- stale lock handling;
- initialization hydration;
- file watcher behavior.

The policy is supplied through `IDbContractConfigDto<TContract>.StoragePolicy`.

## Migration Tool

`MigrationTool` creates versioned contract baselines and migration stubs.

Baseline folders look like:

```text
Contracts/
  Person/
    v1_0_0/
      IPerson.cs
    v1_0_1/
      IPerson.cs
```

Migration stubs are generated between adjacent versions:

```text
Migrations/
  Person/
    Person_v1_0_0_To_v1_0_1_Migration.cs
```

The tool can:

- create initial baselines;
- increment versions;
- copy previous baselines into new version folders;
- update namespaces and `VersionTag`;
- scaffold missing adjacent migrations.

## Runtime Migrations

Runtime migration abstractions exist:

- `IMigration`
- `IMigrationStrategy<TContract>`
- `FileSystemLazyMigrationStrategy<TContract>`
- `FileSystemEagerMigrationStrategy<TContract>`

Strategies can be configured as:

- `None`
- `Lazy`
- `EagerStrict`
- `EagerTolerant`

Current state: runtime migrations need hardening before production use. Known concerns include registration, envelope-aware deserialization, and complete version-chain validation.

## Demo Storage

The demo storage project centers on `Person`.

Version `1.0.0`:

- `Id`
- `Name`
- `Age`
- `Role`

Version `1.0.1` adds:

- `Surname`

Runtime implementations:

- `Student`
- `Employee`

The console app writes 500 sample records, then queries and streams results through the repository/index manager.

## Benchmarks

Benchmarks compare InterfaceDB file writes against:

- SQL Server raw commands;
- Dapper;
- EF Core.

The benchmark projects are not zero-config. They expect SQL Server connection settings and use local file paths configured in code/settings.

## Current Guarantees

The current system provides:

- storage-aware contract objects;
- file-backed persistence;
- flat versioned file layout;
- MessagePack payloads;
- envelope cache;
- sidecar write locking;
- temp-file atomic commit;
- low-level file change watching;
- in-process pub/sub notifications;
- baseline/migration scaffolding;
- clean solution build.

## Current Non-Goals / Roadmap Boundaries

The current system does not yet provide:

- full secondary indexing;
- distributed event bus;
- production-hardened runtime migrations;
- stable type alias serialization;
- transactional multi-record commits;
- automatic replacement of all object references held by user code after external file changes.

See `documentation/roadmaps.md` for roadmap and gap tracking.

## Verification State

Current practical verification:

- `dotnet build InterfaceDB.sln --no-restore`
- console smoke run through `InterfaceDb.ConsoleApp`
- Visual Studio / `dotnet test` tests through `InterfaceDb.Tests`
- integration harness scenarios through `InterfaceDb.IntegrationTests`

`InterfaceDb.Tests` is the VS-discoverable MSTest project. It invokes `InterfaceDb.IntegrationTests`, which is a no-external-package console harness. The harness currently verifies:

- a single save creates a readable payload and leaves no `.lock` or `.tmp.*` files;
- two child processes can load the same payload, write concurrently through the same file path, complete successfully, leave one readable payload, and leave no sidecar artifacts.

The test suite should still be expanded for file watcher change/delete/rename behavior and runtime migrations.
