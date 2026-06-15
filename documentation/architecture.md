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

## Encryption At Rest

Encryption is applied after the complete envelope has been serialized and before its bytes are written:

```text
object -> IDB2 serialization -> IEncryptor.Encrypt -> file
file -> IEncryptor.Decrypt -> IDB2 deserialization -> object
```

`EncryptionNone` is registered by default and returns the serialized bytes unchanged. Existing unencrypted repositories therefore keep the normal `IDB2` file format.

`AesGcmEncryptor` provides authenticated AES-GCM storage. Its files use an outer `IDBE` frame containing:

- encryption format/algorithm version;
- nonce size;
- authentication tag size;
- random nonce;
- authentication tag;
- encrypted `IDB2` payload.

The `IDB2` envelope metadata and object body are both encrypted. The filename and version directory remain visible.

AES-GCM can be selected before repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption(
    keyProvider,
    new AesGcmEncryptorOptions());
services.AddRepository<IMyContract>(options);
```

Custom encryption can be registered with `AddInterfaceDbEncryption(IEncryptor)`.

For rollout over existing plaintext files, `AesGcmEncryptorOptions.AllowUnencryptedPayloads` permits old `IDB2` payloads to be read while all new writes use `IDBE`. This compatibility setting should be disabled after the old files have been rewritten.

The current `IKeyProvider` supplies one active key and does not yet persist a key identifier. Transparent multi-key rotation is therefore future work.

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

## Storage Object Cardinality

`StorageObjectCardinality` controls how envelope-less objects receive physical identities:

- `Single`: the factory resolves one canonical key and path. Creating another envelope-less object at that path throws `StorageObjectAlreadyExistsException`. A loaded object carrying the existing envelope can update it normally.
- `Multiple`: the factory resolves a unique physical identity for every new envelope-less object. The default flat factory uses a GUID.

For `Single`, the default flat factory uses the contract name as its key. Custom factories can derive a key from `Envelope.ObjectInstance` without requiring a prescribed ID property.

Single-object creation is checked while holding the payload lock, so concurrent processes cannot both create the same path. `IEnvelopeKeyAndLocationFactory<TContract>.ObjectCardinality` exposes the setting to naming and persistence.

Repository registration preserves a factory registered by the consumer before `AddRepository`.

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
4. Register an active writer lease for migration coordination.
5. Write serialized bytes to a unique `.writing` proposal.
6. Flush the complete proposal and atomically rename it to `.ready`.
7. Acquire the payload sidecar lock.
8. Select the newest ready proposal and atomically move it over the canonical payload.
9. Remove superseded proposals and repeat until no ready proposal remains.
10. Update envelope cache according to policy.
11. Unwrap object and return it.

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

## Coalescing Last-Write-Wins Commit

Concurrent writers do not wait before serializing their replacement payload. Each writer first creates:

```text
<key>.mpk.pending.<guid>.writing
```

After the bytes are complete and flushed, the proposal is published atomically as:

```text
<key>.mpk.pending.<published-ticks>.<guid>.ready
```

Only `.ready` files are eligible for commit. The payload lock owner orders ready proposals by their publication identifier, atomically moves the newest over `<key>.mpk`, and deletes older proposals. If proposals arrive while it is working, it repeats the scan.

Every publisher attempts to acquire the payload lock after publishing. Therefore, if the previous lock owner exits just before seeing a new proposal, the publishing process becomes the next owner and finishes the commit.

Repository initialization also drains abandoned `.ready` proposals. Stale incomplete `.writing` files are deleted after the configured stale-lock timeout.

The semantic is whole-object last-write-wins. Superseded intermediate object replacements are intentionally not applied.

`SaveAsync` returns the value that is canonical when its coalescing operation completes. If the caller's proposal loses to a newer ready proposal, the writer reloads that winner, stores the winning envelope in cache, and returns the winning object. The losing input object still has its original storage envelope restored.

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
- `*.pending.*`
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

## Repository Query Semantics

The public repository exposes the existing internal async result stream through:

- `QueryManyAsync`: streams every match without materializing the complete result set;
- `QueryFirstAsync`: returns the first match or `null`;
- `QuerySingleAsync`: requires exactly one match;
- `QuerySingleOrDefaultAsync`: allows zero or one match and returns `null` for zero.

`QuerySingleAsync` accepts optional factories for the no-match and multiple-match exceptions. `QuerySingleOrDefaultAsync` accepts an optional factory for the multiple-match exception. When omitted, InterfaceDB throws `RepositoryQueryCardinalityException`.

The cancellation token is the second parameter for all query methods. `QuerySingleAsync` and `QuerySingleOrDefaultAsync` enumerate at most two matches because a second result is sufficient to establish a cardinality violation.

The original `QueryAsync` method remains as a compatibility alias for first-or-default behavior.

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

Before eager migration, the repository acquires a contract-wide migration scope:

1. prevent new writers from registering;
2. wait for registered writers to finish;
3. drain completed pending proposals;
4. migrate committed payloads;
5. release waiting writers.

Migration now reads the `IDB2` envelope header separately from the object body, preserves the envelope cache key, applies the migration chain to the body, and writes the result through the normal coalescing writer.

Runtime migrations still need broader production hardening, particularly complete version-chain validation, tolerant-mode diagnostics, rollback behavior, and generated-contract compatibility coverage.

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
- fully production-hardened runtime migrations;
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

- a single save creates a readable payload and leaves no lock or pending-write artifacts;
- two child processes can publish writes to the same payload and leave one readable last-write-wins result;
- initialization commits the newest abandoned ready proposal and removes superseded proposals;
- eager migration drains old-version proposals, migrates the newest value, preserves the key, and writes to the active version directory.

The test suite should still be expanded for file watcher change/delete/rename behavior and longer migration chains.
