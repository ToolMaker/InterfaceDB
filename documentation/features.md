# Feature Guide

This document lists the main InterfaceDB capabilities and how they fit together.

The [public package overview](../Idb.Libraries/README.md) explains the current behavior with examples. Preview.9 adds context-bound encrypted writes, default rejection of legacy typeless payloads, stored-object size admission, encrypted-index isolation, deterministic named-encryption registration, and hardened SegmentCore restore. See the [preview.9 release notes](preview9-release-notes.md) for migration and rollback.

## Interface-First Persistence

InterfaceDB stores objects through interfaces instead of requiring a base entity class. A contract interface is the storage boundary:

```csharp
[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    string Name { get; set; }
}
```

This allows one repository to store multiple concrete implementations together:

```csharp
public sealed class Student : IPerson { /* Course plus IPerson members */ }
public sealed class Employee : IPerson { /* Department plus IPerson members */ }
public sealed class SeniorCitizen : IPerson { /* Discount card plus IPerson members */ }

IRepository<IPerson> people = ...;

await people.SaveManyAsync(new IPerson[]
{
    new Student { Name = "Jane", Course = "Physics" },
    new Employee { Name = "John", Department = "Engineering" },
    new SeniorCitizen { Name = "Mary", DiscountCardNumber = "SC-1042" }
});
```

Each envelope records its concrete type. Reading through `IRepository<IPerson>` therefore returns a mixed `IPerson` collection whose enrolled runtime objects are still `Student`, `Employee`, and `SeniorCitizen`. The default allowlist covers concrete implementations in the contract assembly; pass an explicit `StoredRuntimeTypeAllowlist<IPerson>` when they live elsewhere. See [Storing Multiple Concrete Types](polymorphic-storage.md).

## Envelopes

Every persisted object has an envelope. The envelope carries:

- storage key;
- version tag;
- provider location;
- concrete implementation type;
- deletion state;
- per-object cache preference;
- encrypted field values.

The application works with the object, while the object retains its envelope reference for future saves and deletes.

## Storage Identity

Storage identity is resolved by `IEnvelopeStorageIdentityFactory<TContract>`.

Built-in identity/key options include:

- GUID v7 file keys;
- in-memory sequential `int`;
- in-memory sequential `long`;
- in-memory sequential `BigInteger`;
- custom factories from application code.

The consumer does not need to add an `Id` property unless the application wants one.

## Cardinality

`StorageObjectCardinality.Single` means one object is allowed for a logical identity. A detached duplicate create fails.

`StorageObjectCardinality.Multiple` means many physical objects can share one logical group. The physical keys use zero-based suffixes:

```text
john-smith-0
john-smith-1
john-smith-2
```

## Repository API

Main methods:

- `InitializeAsync`
- `SaveAsync`
- `SaveManyAsync`
- `DeleteAsync`
- `ExistsAsync`
- `QueryManyAsync`
- `QueryFirstAsync`
- `QuerySingleAsync`
- `QuerySingleOrDefaultAsync`
- `QueryManyByStorageKeyAsync`
- `SearchManyAsync`
- `SearchFirstAsync`

`Query*` accepts delegates and can always run. `Search*` accepts expressions and can route to indexes.

## Type-Specific Queries

When one contract has multiple implementation types, generic query overloads can narrow by concrete type:

```csharp
await foreach (var employee in people.SearchManyAsync<Employee>(x => x.Age >= 30))
{
}
```

## Search Indexes

Indexes are opt-in through attributes:

```csharp
[StorageScope(VersionTag = "1.0.0")]
[SearchIndex]
public interface IPerson : IStoredContract<IPerson>
{
    [SearchIndex(SearchIndexMode.Exact
        | SearchIndexMode.Prefix
        | SearchIndexMode.Suffix
        | SearchIndexMode.Contains)]
    string Name { get; set; }
    int Age { get; set; }

    [NotSearchIndexed]
    string Notes { get; set; }
}
```

Members marked `[EncryptedStorageField]` are excluded from inherited type-level indexing. Applying
`[SearchIndex]` directly to the same encrypted member is rejected, so protected values cannot be
copied into an unprotected search-index derivative. Encrypted SegmentCore repositories also disable
and purge the persistent catalog; their indexes rebuild in memory from authoritative records.

Supported index operations:

- exact equality;
- comparable ranges;
- ordinal string `StartsWith` and `EndsWith`;
- literal ordinal string `Contains` through RadixForge's adaptive gram index;
- `&&`;
- `||`;
- fallback to scan for unsupported expressions.

Use the explicit ordinal overloads when routing string operations through the
radix indexes:

```csharp
person => person.Phone.StartsWith("+27", StringComparison.Ordinal)
person => person.Email.EndsWith("@example.com", StringComparison.Ordinal)
person => person.Name.Contains("ann")
person => person.Name.Contains("ann", StringComparison.Ordinal)
```

The one-argument `Contains` overload has ordinal semantics in .NET and is
indexable. For `StartsWith` and `EndsWith`, use the explicit ordinal overload;
their one-argument overloads are culture-sensitive and therefore fall back to
a scan. Ignore-case and culture-aware string operations are not routed through
the ordinal indexes.

The planner estimates live candidate counts, starts with the least expensive
access path, and intersects additional indexes only when the projected saving
outweighs the set-operation cost. It unions safe `||` branches and treats
unsupported or negative conditions as residual predicates when a positive
index can still bound the search.

Resolve `IIndexManager<TContract>` to inspect a plan without executing it:

```csharp
var explanation = indexManager.ExplainSearch(
    person => person.Role == "Student" && person.Age >= 18);

Console.WriteLine(explanation.PlanText);
```

See [Query Planning](query-planning.md) for the complete routing rules.

## Object Caching

The envelope metadata catalog is always maintained. Full objects are not cached by default.

Opt into object retention with:

- `[StorageCached]` on a concrete storage class;
- `object.Envelope.CacheObjectInstance = true`;
- storage policy override for database-wide object retention.

This lets the application cache hot object categories without hiding full-object memory use by default.

## Storage Providers

InterfaceDB has two opt-in persistence layouts behind the same repository abstraction:

- The file provider stores each object as an independent file and supports file watching, file-oriented migrations, and cross-process last-operation-wins coordination.
- The SegmentCore provider stores checksummed frames in append-only striped segments and adds atomic bulk transactions, mutation-WAL recovery, full/delta checkpoints, integrity verification, bounded automatic compaction, point-consistent backup, and quota/free-space/inventory-checked restore.

Provider-neutral `StoragePolicy` hardware profiles resolve into complete, inspectable SegmentCore
stripe, queue, read, durability, checkpoint, and compaction settings. Explicit
`SegmentCoreRepositoryOptions` retain final precedence for provider-specific tuning.

Register SegmentCore with `AddSegmentCoreRepository<TContract>`. Existing exact, range, prefix, suffix, contains, query-planning, encryption, cache, notification, and polymorphic behavior remains above the provider boundary. See [SegmentCore Storage Provider](segment-core-storage-provider.md).

## Durability Modes

For the file provider, `StorageDurabilityMode.Performance` writes canonical files directly and is the default. It is intended for low-latency local data where the application accepts weaker interrupted-write behavior.

`StorageDurabilityMode.Durable` writes a recoverable proposal, publishes it through a ready state, coordinates other processes, and drains abandoned completed proposals at initialization. It is slower because every object remains an independently published file.

Neither mode replaces backups or operating-system access controls. See [Current Performance Results](performance-results.md) for measured tradeoffs.

## File Change Awareness

The file provider can watch its data root. It handles:

- created files;
- changed files;
- deleted files;
- renamed files;
- watcher errors and buffer overflow through rescan.

Local writes are acknowledged so the repository does not duplicate its own notifications.

## Notifications

The repository can publish in-process events:

- added;
- stored;
- removed;
- deleted.

These are application notifications, not a distributed event bus.

## Migrations

Runtime migration strategies:

- `None`
- `Lazy`
- `EagerStrict`
- `EagerTolerant`

Migration chains are version-to-version. If a stored object is behind the active version, InterfaceDB walks each migration step until the active version is reached.

## Encryption

Supported security features:

- context-bound `IDBE` v3 whole-envelope encryption with AES-GCM;
- explicit `AllowContextlessPayloadMigration` for controlled v1/v2 rewrite;
- key-aware encrypted payloads and historical key lookup;
- compare-before-replace re-encryption on read for both providers and forced full-store rotation for the File provider;
- named encryption registration that is independent of repository-registration order;
- Windows Credential Manager key store;
- field-level encryption through interface attributes with index-derivative exclusion;
- `MessagePackSecurity.UntrustedData`, default legacy-typeless rejection, and frozen per-contract runtime-type allowlists;
- `StoragePolicy.MaximumStoredObjectBytes`, 256 MiB by default.

See [Security And Encryption](security.md).

## Tooling

`MigrationTool` currently acts as the InterfaceDB maintenance CLI. It can:

- create contract baselines;
- increment baseline versions;
- scaffold migrations;
- run encryption rotation.

`Idb.Analyzers` protects versioned contract identities through Roslyn diagnostics.

See [Contract Versioning And Migrations](migrations.md) for the supported workflow and deployment checklist.
