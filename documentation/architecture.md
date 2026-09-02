# InterfaceDB Architecture

InterfaceDB is an interface-first storage stack for storage-aware contract objects. It persists concrete objects behind versioned C# interfaces, using envelopes as the low-level provider-neutral storage metadata boundary.

The project has two local storage providers. The flat-file provider favors independently inspectable object files, file watching, and file-oriented migrations. SegmentCore favors dense append-only storage, atomic bulk transactions, checksummed WAL recovery, checkpointed primary locators, and bounded compaction. Both sit under the same repository and query/index layers.

## Solution Layout

- `Idb.Libraries`
  Core abstractions and file-system implementation.
- `InterfaceDB.Storage.SegmentCore`
  Packable append-only SegmentCore engine and InterfaceDB provider integration.
- `InterfaceDB.Storage.SegmentCore.Tests`
  Storage-engine recovery, transaction, maintenance, and end-to-end provider tests.
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
- `State`

`Location` is an opaque provider locator. The file provider interprets it as a path; SegmentCore uses the physical envelope key. Future SQL, document, event-stream, or memory providers can assign their own locator semantics. `State` is provider-neutral and prevents a deleted in-memory storage object from being saved accidentally.

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

`InterfaceRepositoryDb<TContract>` validates that `TContract` is an interface and has `[StorageScope]`, then delegates work to `IIndexManager<TContract>`.

The active `IIndexManager<TContract>` is selected per registered contract:

- `PassthroughIndexManager<TContract>` is used when the contract has no searchable index metadata.
- `SearchIndexManager<TContract>` is used when `[SearchIndex]` metadata is discovered.

This keeps the no-index path small while allowing indexed contracts to maintain in-memory exact, range, prefix, suffix, and adaptive substring search structures.

The intended query routing boundary is:

- equality against the storage identity/logical envelope key goes directly to the envelope cache;
- range predicates belong to secondary indexes;
- the envelope cache must not become a range-query engine.

For `StorageObjectCardinality.Multiple`, one logical key can resolve to multiple physical envelopes. Equality lookup is therefore constant-time to the logical group plus the cost of returning the matching group members. Property ranges use the attribute-discovered per-field range index and return matching physical envelope keys.

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

`BinaryObjectSerializer<TContract>` uses MessagePack and writes the current `IDB4` payload format. Legacy `IDB2` and `IDB3` payloads remain readable for backward compatibility.

Serialization is already behind `IObjectSerializer<TContract>`. The default registration wraps `BinaryObjectSerializer<TContract>` with `EncryptingObjectSerializer<TContract>`, so future serializer implementations can be registered below the repository without changing `IRepository<TContract>` or the file reader/writer contracts.

The current format stores:

- magic prefix: `IDB4`
- version tag
- envelope cache key
- concrete runtime type name
- per-object cache flag
- encrypted field metadata
- serialized object payload

Envelope references are guarded during serialization to avoid object-envelope-object cycles.

`IDB3` differs from `IDB2` by persisting `IObjectEnvelope<TContract>.CacheObjectInstance`. That flag lets an individual object opt into full object retention even when the concrete type is not marked with `[StorageCached]`.

`IDB4` differs from `IDB3` by persisting `IObjectEnvelope<TContract>.EncryptedFields`. That dictionary stores ciphertext for properties marked with `[EncryptedStorageField]`.

Current limitation: payloads still depend on concrete .NET type names. Stable type aliases are a future hardening item.

## Encryption At Rest

Encryption is applied after the complete envelope has been serialized and before its bytes are written:

```text
object -> IDB4 serialization -> IEncryptor.Encrypt -> file
file -> IEncryptor.Decrypt -> IDB4 deserialization -> object
```

`EncryptionNone` is registered by default and returns the serialized bytes unchanged. New unencrypted repositories therefore use the normal plaintext `IDB4` file format. Existing `IDB2` and `IDB3` files remain readable.

`AesGcmEncryptor` provides authenticated AES-GCM storage. Its files use an outer `IDBE` frame containing:

- encryption format version;
- nonce size;
- authentication tag size;
- key identifier for key-aware version 2 payloads;
- random nonce;
- authentication tag;
- encrypted plaintext envelope payload.

The plaintext envelope metadata and object body are both encrypted. The filename and version directory remain visible.

AES-GCM can be selected before repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption(
    keyProvider,
    new AesGcmEncryptorOptions());
services.AddRepository<IMyContract>(options);
```

Custom encryption can be registered with `AddInterfaceDbEncryption(IEncryptor)`.

## Field-Level Encryption

Field-level encryption is implemented through interface attributes and the envelope lifecycle.

```csharp
[StorageScope(VersionTag = "1.0.0")]
[EncryptedStorage("PeopleSecure")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }

    [EncryptedStorageField("NationalId")]
    string NationalIdentifier { get; set; }
}
```

On write, `EnvelopeLifecycle<TContract>` asks `EncryptedStorageFieldProcessor<TContract>` to inspect the contract metadata, encrypt marked property values, store ciphertext in `Envelope.EncryptedFields`, and clear the concrete backing property before serialization.

On read, `EnvelopeLifecycle<TContract>` wraps the materialized object in `EncryptedStorageDispatchProxy<TContract>` when encrypted field definitions exist. Non-encrypted properties delegate to the concrete object. Encrypted getters decrypt the envelope field value on demand. Encrypted setters update the envelope ciphertext and clear the concrete backing property.

The attribute argument is a profile name, not a raw secret. It is intended to point to DI/configuration that resolves the actual key store and key selection policy. The first implementation stores the profile name in field metadata; a full encryption profile resolver remains roadmap work.

For rollout over existing plaintext files, `AesGcmEncryptorOptions.AllowUnencryptedPayloads` permits old plaintext payloads to be read while all new writes use `IDBE`. This compatibility setting should be disabled after the old files have been rewritten.

`IKeyProvider` remains the simple single-key contract. `IRotatingKeyProvider` resolves an active key ID and historical keys by ID. New key-aware payloads store only the non-secret key ID; key material remains in the configured key store.

`WindowsCredentialEncryptionKeyStore` stores active and historical keys as Generic Credentials for the current Windows user:

```csharp
var keyStore = new WindowsCredentialEncryptionKeyStore("People");
keyStore.AddOrUpdateKey("key-2025", oldKey);
keyStore.AddOrUpdateKey("key-2026", currentKey);
keyStore.SetActiveKey("key-2026");

services.AddInterfaceDbWindowsCredentialEncryption(
    "People",
    new AesGcmEncryptorOptions
    {
        LegacyKeyId = "key-2025",
        RotationMode = EncryptionRotationMode.ReencryptOnRead
    });
services.AddRepository<IPerson>(options);
```

`LegacyKeyId` is required when a rotating provider must read version 1 `IDBE` files because those files predate key identifiers.

Rotation has two independent execution modes:

- `ReencryptOnRead` decrypts once with the payload's historical key and encrypts once with the active key. It does not process intermediate keys. The replacement is committed only if the stored bytes have not changed since the read.
- `IEncryptionRotationOperation<TContract>.RotateAllAsync` acquires the exclusive storage scope, drains pending writes, and rewrites every payload not already using the active key. This is an encryption maintenance operation, not a schema migration.

Lazy rotation is activated through `AesGcmEncryptorOptions.RotationMode` during encryption registration. It is intentionally not part of `StoragePolicy` because encryption behavior belongs to the selected encryption provider.

Full rotation can be invoked in application code through `IEncryptionRotationOperation<TContract>` or from the maintenance CLI:

```text
dotnet run --project MigrationTool/MigrationTool.csproj -- encryption rotate
  --database People
  --data-root C:\Data\People
  --extension .mpk
  --legacy-key-id key-2025
```

The CLI project retains the `MigrationTool` name for backward compatibility, but presents itself as the InterfaceDB Maintenance Tool because it now owns baseline, migration-scaffolding, and encryption-maintenance commands.

Historical keys must remain available while any payload references them. Remove an old key only after a full rotation reports that all payloads are current.

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

Single-object creation is checked while holding the payload lock, so concurrent processes cannot both create the same path. `IEnvelopeStorageIdentityFactory<TContract>.ObjectCardinality` exposes the setting to naming and persistence.

Repository registration preserves a factory registered by the consumer before `AddRepository`.

## Keyed Dependency Injection

Every repository registration creates a keyed dependency graph. The public database key is:

1. `IDbContractConfigDto<TContract>.EnvelopeDICacheKey`;
2. `ContractName` when the first value is blank.

Namespaces and contract versions are not used as DI keys.

Two databases can use the same contract type with different roots and behavior:

```csharp
services.AddRepository<IPerson>(localOptions);
services.AddRepository<IPerson>(archiveOptions);

var local = provider.GetInterfaceDbRepository<IPerson>("LocalPeople");
var archive = provider.GetInterfaceDbRepository<IPerson>("ArchivePeople");
```

The key can also be supplied at registration time when the options object should remain shared or environment-neutral:

```csharp
services.AddRepository<IPerson>(personOptions, "LocalPeople");
```

The keyed graph isolates configuration, repository, cache, serializer, envelope lifecycle, reader/writer, change watcher, migration strategy, and write coordination.

Database-specific encryption can be registered before the repository, and storage identity factories can be passed into repository registration:

```csharp
services.AddInterfaceDbAesGcmEncryption("SecurePeople", keyProvider);
services.AddRepository<IPerson>(
    secureOptions,
    storageIdentityFactory: secureIdentityFactory);
```

Repeated registration of the same contract, key, and configuration instance is idempotent. Registering the same contract/key with a different configuration instance throws a clear configuration error.

For backward compatibility, unkeyed services resolve to the first database registered for that contract. Applications with multiple databases should use keyed resolution explicitly.

## Envelope Cache

The cache is currently an in-memory concurrent dictionary abstraction:

```csharp
IEnvelopeDictionaryCacheManager<string, IObjectEnvelope<TContract>>
```

Conceptually, it is the primary equality catalog for envelopes. The logical storage identity is the lookup key, and the result can contain one or more physical envelopes depending on `StorageObjectCardinality`.

```text
logical key -> physical key -> envelope
```

For single cardinality, the logical group contains one physical envelope. For multiple cardinality, the logical group contains zero-based physical identities such as:

```text
john-smith -> john-smith-0
john-smith -> john-smith-1
```

Repository equality queries that can be reduced to the logical storage key should use this cache directly. That gives `O(1 + k)` behavior, where `k` is the number of physical envelopes in the matching logical group.

The envelope cache is intentionally equality-only. It should not evaluate `>`, `>=`, `<`, `<=`, between, prefix, full-text, vector, or other range/similarity predicates. Those belong to the index layer.

During initialization, the file repository scans the data root and stores skeleton envelopes. Physical
activation is concurrency-safe and single-flight: concurrent callers share one scan, and later calls
use the ready fast path. Optional hydration, controlled by `StoragePolicy.HydrateOnInitialize`, is a
separate one-time upgrade so a metadata-only index bootstrap cannot consume a later hydration request.
The dependency-injection graph has one keyed disposable repository owner; unkeyed compatibility
resolutions are non-owning facades over that owner.

If hydration is disabled, envelopes can remain lightweight until query/read time.

When a search-index rebuild already has to materialize every object, the file repository exposes one
initialization/enumeration stream. Index construction therefore performs one payload-deserialization
pass rather than a hydration pass followed by a second enumeration pass. SegmentCore uses the same
shared index-bootstrap contract; providers with persistent catalogs can stay metadata-only on a valid
warm catalog.

The envelope metadata catalog is always maintained. Full object instances are retained only when one of these policies allows it:

- `StoragePolicy.ObjectCacheMode` is set to `StrongObject`;
- the concrete storage class has `[StorageCached]`;
- the envelope has `CacheObjectInstance = true`.

The default is metadata-only object caching. A query that materializes an uncached object returns that object to the caller but does not promote it into the envelope cache unless the type or envelope opted in.

## Query And Index Routing

The delegate query path is still pass-through because a compiled `Func<TContract, bool>` cannot be inspected. Indexed queries use expression-based search APIs such as `SearchManyAsync` and `SearchFirstAsync`.

Search indexes are opt-in with `[SearchIndex]` and `[NotSearchIndexed]`. No attribute means no automatic indexing. A type-level `[SearchIndex]` indexes eligible scalar root members, and member-level attributes override the inherited default. Nested objects are traversed only when the nested property is explicitly marked with `[SearchIndex]`.

Each indexed property path owns a property index container. That container can hold more than one internal index:

- exact index for `==`;
- range index for `>`, `>=`, `<`, and `<=`;
- prefix index for ordinal string `StartsWith`;
- optional reverse-codec suffix index for ordinal string `EndsWith`;
- optional adaptive gram index for literal ordinal string `Contains`.

For example:

```csharp
person => person.Age >= 18 && person.Age <= 30
```

routes to the range index registered for `Age`. The index returns physical envelope keys, and the repository materializes those exact envelopes. This avoids returning an entire multiple-cardinality logical group when only one physical object matched an indexed property.

Equality and range should use different mechanisms:

- `Name == "John Smith"` on the storage identity key: envelope cache.
- `Age >= 18`: `Age` range index.
- `Phone.StartsWith("+27", StringComparison.Ordinal)`: `Phone` prefix index.
- `Email.EndsWith("@example.com", StringComparison.Ordinal)`: reversed `Email` suffix index.
- `Name.Contains("ann")`: adaptive gram substring index.
- `Role == "Student"`: `Role` exact index.

The planner normalizes expression trees into indexable access paths and
residual predicates. It supports `==`, range comparisons, explicit ordinal
`StartsWith` and `EndsWith`, literal ordinal `Contains`, `&&`, and `||`.
Compatible lower and upper comparisons on one property become one bounded
range. An `AND` starts with the lowest-cost path and conditionally intersects
additional paths only when estimated object-load savings justify the set work.
The cost model accounts for metadata-only, weak-object, and strong-object cache
modes. An `OR` unions its branches only when every branch has a safe candidate
source; otherwise it scans to avoid false negatives. Unsupported and negative
conditions remain residual predicates when a positive path bounds the search,
and every returned candidate is validated with the complete compiled predicate.

`IIndexManager<TContract>.ExplainSearch` exposes the selected hierarchy,
estimated candidates and cost, index usage, and residual-predicate count without
loading objects. See [Query Planning](query-planning.md).

## Write Path

`FileObjectWriter<TContract>` performs the physical file write.

Performance mode writes the canonical payload directly. Durable mode uses this flow:

1. Wrap object into envelope.
2. Resolve target payload path.
3. Ensure target directory exists.
4. Register an active writer lease for migration coordination.
5. Write serialized bytes to a unique `.writing` proposal.
6. Write the proposal through intermediate caches and atomically rename it to `.ready`.
7. Acquire the payload sidecar lock.
8. Select the newest ready proposal and atomically move it over the canonical payload.
9. Remove superseded proposals and repeat until no ready proposal remains.
10. Update envelope cache according to policy.
11. Unwrap object and return it.

## Sidecar Write Locks

Storage write locking is abstracted behind `IStorageWriteLock`. The file-system provider implements it with `SidecarFileWriteLock<TContract>`.

For payload:

```text
<key>.mpk
```

the lock is:

```text
<key>.mpk.lock
```

Lock acquisition uses `FileMode.CreateNew`, which is atomic across processes. Only one process can create the lock file. The lock file is held open with `FileShare.None`.

The lock file is intentionally empty. Its exclusive open handle is the synchronization primitive; persisting diagnostic metadata in every ephemeral lock added measurable latency without contributing to ownership or recovery correctness.

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

After the write-through payload is complete and its handle is closed, the proposal is published atomically as:

```text
<key>.mpk.pending.<published-ticks>.<guid>.ready
```

Only `.ready` files are eligible for commit. The payload lock owner orders ready proposals by their publication identifier, atomically moves the newest over `<key>.mpk`, and deletes older proposals. If proposals arrive while it is working, it repeats the scan.

Every publisher attempts to acquire the payload lock after publishing. Therefore, if the previous lock owner exits just before seeing a new proposal, the publishing process becomes the next owner and finishes the commit.

Repository initialization also drains abandoned `.ready` proposals. Stale incomplete `.writing` files are deleted after the configured stale-lock timeout.

The semantic is whole-object last-write-wins. Superseded intermediate object replacements are intentionally not applied.

`SaveAsync` returns the value that is canonical when its coalescing operation completes. If the caller's proposal loses to a newer ready proposal, the writer reloads that winner, stores the winning envelope in cache, and returns the winning object. The losing input object still has its original storage envelope restored.

Writes and deletes share the same cross-process last-operation-wins model. A timed-out delete removes its own uncommitted delete proposal, preventing a failed call from deleting storage later during recovery.

## Delete Path

`DeleteAsync` requires the exact persisted object and its envelope. Detached objects without an envelope throw `StorageObjectNotPersistedException`.

The file provider publishes deletion into the same ordered stream as writes:

```text
<key>.mpk.pending.<published-ticks>.<guid>.delete.ready
```

The payload lock owner selects the newest completed write or delete proposal. A winning delete proposal removes the canonical payload and older proposals. Repository initialization also commits abandoned delete proposals, so a process failure does not leave an older pending write able to recreate deleted storage.

After deletion, the envelope state becomes `Deleted`, the cache entry is removed, and the deleted notification is published. Repeating delete returns `false`. Saving that same storage-aware object throws `StorageObjectDeletedException`; explicit recreation requires a new envelope-less object.

Provider-neutral storage failures use:

- `StorageObjectNotPersistedException`
- `InvalidStorageEnvelopeException`
- `StorageObjectDeletedException`
- `StorageConcurrencyException`
- `StorageOperationException`

Provider-specific exceptions such as `IOException` remain available through `InnerException`.

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

Repository startup arms the source before the cache walk. It first captures changes by normalized
provider path, reconciles a final filesystem snapshot against both the initial snapshot and the paths
loaded by the repository, then changes from `Capturing` through `Reconciling` to `Live` under the same
callback gate. Reconciliation reads current disk state instead of replaying raw event order, so a
rename followed by recreation or deletion converges to the files that actually exist. Dirty bootstrap
memory is bounded by the number of distinct paths.

Path comparison is selected by a small capability probe against the configured data volume rather
than by operating-system name. This matters because macOS and iOS volumes are commonly
case-insensitive but can be case-sensitive. The source maintains a snapshot of committed payload
length, last-write time, and exact provider-path spelling. The spelling component lets a case-only
rename update the ordinal envelope key on a case-insensitive volume. On watcher error, including
internal buffer overflow, it publishes the error and rescans storage:

- missing snapshot paths emit `Deleted`;
- new paths emit `Created`;
- changed stamps emit `Changed`.

`RescanAsync` exposes the same current-state reconciliation explicitly. The snapshot advances only
after every registered handler succeeds, so a transient read/apply failure remains visible to a later
signal or rescan.

Storage-change delivery is at-least-once. The repository publishes its local notification directly,
and the watcher may publish a duplicate echo. A path-only acknowledgement cannot safely suppress that
echo: another process can commit a newer revision between the local commit and acknowledgement, and
marking that path as handled would lose the external update. `LocalStorageChangeSuppression` and the
path-only acknowledgement hook remain compatibility placeholders until the coordinator can supply an
immutable commit fingerprint. Subscribers must therefore be idempotent.

When watching is disabled, or on iOS where `FileSystemWatcher` is not supported, initialization still
performs its scan/final reconciliation. External mutations after activation require an explicit
`RescanAsync` or a new repository lifetime.

## Storage Change Handling

The handler treats file-system events as signals. The file remains the source of truth.

Behavior:

- `Created` / `Changed`
  - read committed envelope from disk;
  - derive physical cache identity from the actual file name rather than serialized metadata;
  - update envelope cache;
  - publish repository stored/added events as appropriate.
- `Deleted`
  - remove envelope from cache;
  - publish deleted event.
- `Renamed`
  - remove old cache key;
  - read the new payload if valid and make the new file name authoritative;
  - publish removed/stored/added events as appropriate.
- `Error`
  - publishes the watcher error;
  - triggers snapshot reconciliation.

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

`StoragePolicy` is the provider-neutral runtime-policy boundary. It supplies a named hardware
profile plus repository-level behavior:

- write and read concurrency;
- notification publishing;
- envelope metadata caching;
- selective object retention in cache;
- single-buffer vs stream write;
- writer semaphore;
- sidecar locks;
- stale lock handling;
- initialization hydration;
- file watcher behavior.

The policy is supplied through `IDbContractConfigDto<TContract>.StoragePolicy` or overridden at
registration. The file provider consumes the applicable file, lock, watcher, concurrency, and
cache members directly. SegmentCore uses the shared concurrency/cache/notification intent and
resolves the hardware profile into `SegmentCoreRepositoryOptions`, which owns physical stripe,
queue, read-window, transaction, checkpoint, and compaction settings. Explicit provider options
take precedence. This keeps the core abstraction independent of SegmentCore's storage format while
ensuring every effective physical setting is public and observable.

## Migration Tool

The InterfaceDB Maintenance Tool, currently hosted by the backward-compatible `MigrationTool` project, creates versioned contract baselines and migration stubs and can run explicit encryption rotation.

Each generated baseline also contains `.interfacedb-baseline`, an immutable identity manifest recording the logical contract name, storage version, and fully qualified interface name.

## Contract Analyzer

`Idb.Analyzers` is a `netstandard2.0` Roslyn analyzer package, allowing the same diagnostics to run in Visual Studio, VS Code, Rider, command-line builds, and CI.

It compares `[StorageScope]` interfaces with the committed baseline manifests:

- `IDB001` warns when a recorded interface was removed, renamed, or moved;
- `IDB002` warns when a storage contract has no baseline manifest;
- `IDB003` warns when an existing baseline's `VersionTag` was edited;
- `IDB004` warns when a manifest cannot be interpreted.

This tooling does not prevent deliberate schema changes. It requires them to follow the intended workflow: create a new versioned baseline, retain the previous contract identity, and provide a migration.

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

Migration now reads the plaintext envelope header separately from the object body, preserves the envelope cache key, applies the migration chain to the body, and writes the result through the normal coalescing writer.

Migration chains follow these rules:

- every migration target must be greater than its source;
- only one migration may be registered for a source version;
- each stored version must have a complete path to `ActiveVersion`;
- migration state is serialized between each step, allowing multi-step chains;
- legacy storage is deleted only after the complete chain is persisted successfully.

Migration failures use:

- `MissingMigrationException`
- `AmbiguousMigrationException`
- `InvalidMigrationDefinitionException`
- `MigrationExecutionException`

`EagerStrict` rolls back target payloads written during a failed initialization batch and propagates the failure. `EagerTolerant` retains unchanged legacy payloads when a chain is missing or migration code fails.

Lazy migration begins from the skeleton envelope and provider locator. It reads the raw stored payload, applies the complete chain, and only then materializes the active contract. Legacy envelopes are deliberately excluded from startup cache hydration under the lazy strategy.

Runtime migration chains, strict rollback, tolerant retention, lazy conversion, and generated adjacent-version contracts have executable coverage. Remaining production work is operational: richer per-record diagnostics, a dry-run/report mode, backup integration, and longer multi-platform upgrade/rollback soaks.

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

- embedded SQLite through direct `Microsoft.Data.Sqlite` commands;
- embedded SQLite through Dapper;
- SQL Server raw commands;
- Dapper;
- EF Core.

The embedded SQLite production gate is zero-service and runs by default. SQL Server comparisons remain opt-in because they require an external connection.

Benchmark boundaries are reported explicitly. Repository query comparisons return complete objects for every engine and validate their fields; scalar SQL payloads are not substitutes. `MetadataOnly` is the canonical fresh managed object-materialization mode, while `StrongObject` measures a hot InterfaceDB object cache. Raw SegmentCore `byte[]` probes and their provider-open timings are storage-layer diagnostics and must not be presented as object-query results; those raw-provider open timings end before a first application object is materialized. The file-provider production-gate startup metric is different: InterfaceDB deserializes stored objects while rebuilding indexes, whereas the relational counterparts only open connections.

## Current Guarantees

The current system provides:

- storage-aware contract objects;
- file-backed persistence;
- flat versioned file layout;
- MessagePack payloads;
- envelope cache;
- exact, range, prefix, suffix, and ordinal contains indexes;
- cost-aware expression query planning;
- sidecar write locking and migration writer leases;
- recoverable proposal-file commit;
- external file create/update/delete/rename reconciliation;
- in-process pub/sub notifications;
- baseline/migration scaffolding and eager/lazy runtime migration;
- AES-GCM envelope and selected-field encryption with key rotation;
- contract baseline analyzer diagnostics;
- clean solution build.

## Current Non-Goals / Roadmap Boundaries

The current system does not yet provide:

- distributed event bus;
- stable type alias serialization;
- transactional multi-record commits;
- persistent full-text, fuzzy, or vector indexes;
- replication, remote query transport, or distributed transactions;
- automatic replacement of all object references held by user code after external file changes.

See `documentation/roadmaps.md` for roadmap and gap tracking.

## Verification State

Current practical verification:

- `dotnet build InterfaceDB.sln --no-restore`
- console smoke run through `InterfaceDb.ConsoleApp`
- Visual Studio / `dotnet test` tests through `InterfaceDb.Tests`
- integration harness scenarios through `InterfaceDb.IntegrationTests`

`InterfaceDb.Tests` and `Idb.Analyzers.Tests` are VS-discoverable MSTest projects. `InterfaceDb.IntegrationTests` is an executable end-to-end harness. Together they verify:

- a single save creates a readable payload and leaves no lock or pending-write artifacts;
- two child processes can publish writes to the same payload and leave one readable last-write-wins result;
- initialization commits the newest abandoned ready proposal and removes superseded proposals;
- eager migration drains old-version proposals, migrates the newest value, preserves the key, and writes to the active version directory.
- watcher create/update/delete/rename and rescan behavior;
- exact/range/prefix/suffix/contains index correctness and maintenance;
- multi-step, strict, tolerant, and lazy migration behavior;
- encrypted round trips, tamper rejection, field proxies, and key rotation;
- keyed database isolation, cardinality, bulk failure reporting, and object cache modes.
