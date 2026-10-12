# InterfaceDB

InterfaceDB is an interface-first object repository and embedded object database for .NET 10. Application code works with versioned interfaces and `IRepository<TContract>`; serialization, concrete types, storage providers, indexes, migrations, caching, concurrency, and encryption stay behind that boundary.

The central design rule is simple:

> Let the application core depend on contracts and repository abstractions, not storage implementations.

InterfaceDB is not an ORM. An ORM maps objects to a relational model. InterfaceDB persists polymorphic object implementations behind interface contracts and provides the repository, envelope, migration, index, and storage lifecycle itself.

## Why Use It?

- Keep file I/O, serialization, and storage locations out of domain and application services.
- Store several concrete implementations behind one interface.
- Change the storage provider below `IRepository<TContract>` without changing callers.
- Version contracts and migrate stored objects deliberately.
- Add exact, range, prefix, suffix, and ordinal contains indexes with attributes.
- Choose metadata-only, weak, or strong object caching per workload.
- Use authenticated AES-GCM encryption, field-level encryption, and key rotation when configured.
- Run locally without a database server or connection string.
- Choose independent inspectable files or the high-throughput append-only SegmentCore provider.

InterfaceDB is a strong fit for desktop applications, local-first tools, embedded application data, offline workloads, and systems where object-native persistence and architectural boundaries matter more than relational joins.

It is not currently a replacement for a server database when you need distributed transactions, SQL joins, remote multi-user access, replication, or a persistent full-text/vector engine. See [Product Positioning](documentation/product-positioning.md).

## The Programming Model

```powershell
dotnet add package InterfaceDB --prerelease
# Add the optional append-only provider when required:
dotnet add package InterfaceDB.Storage.SegmentCore --prerelease
```

> Preview compatibility: APIs and storage formats may still change before stable `1.0.0`.
> Preview.9 keeps plaintext envelopes at `IDB6` and changes AES-GCM writes to context-bound `IDBE`
> version 3. Older contextless encrypted frames require an explicit migration window and complete
> rewrite before strict mode is restored. See the [preview.9 release notes](documentation/preview9-release-notes.md).

Define a versioned interface in the application contract layer:

```csharp
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Enums;

[StorageScope(VersionTag = "1.0.0")]
public interface IPerson : IStoredContract<IPerson>
{
    [SearchIndex]
    string Name { get; set; }

    [SearchIndex(SearchIndexMode.Exact | SearchIndexMode.Range)]
    int Age { get; set; }

    [SearchIndex]
    string Role { get; set; }
}
```

### One Repository, Multiple Concrete Types

Implement the same interface more than once. These are ordinary application classes; none inherits an InterfaceDB persistence base class:

```csharp
using Idb.Libraries.Abstractions.Services.Serialization;

public sealed class Student : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "Student";
    public string Course { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}

public sealed class Employee : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "Employee";
    public string Department { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}

public sealed class SeniorCitizen : IPerson
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = "SeniorCitizen";
    public string DiscountCardNumber { get; set; } = string.Empty;
    public IObjectEnvelope<IPerson>? Envelope { get; private set; }
}
```

`IRepository<IPerson>` is one mixed, polymorphic repository. It can store all three classes together. InterfaceDB records each object's concrete type and restores that exact runtime type later when it is enrolled in the repository's runtime-type allowlist, including properties such as `Course`, `Department`, and `DiscountCardNumber` that are not part of `IPerson`.

At the composition root, register the file provider and resolve the repository abstraction:

```csharp
using Idb.Libraries.Abstractions.Services.Repository;
using Idb.Libraries.Implementation.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddRepository<IPerson>(new PersonOptions());

await using var provider = services.BuildServiceProvider();
var people = provider.GetRequiredService<IRepository<IPerson>>();

await people.InitializeAsync();
await people.SaveManyAsync(new IPerson[]
{
    new Student { Name = "Jane", Age = 22, Course = "Physics" },
    new Employee { Name = "John", Age = 41, Department = "Engineering" },
    new SeniorCitizen { Name = "Mary", Age = 72, DiscountCardNumber = "SC-1042" }
});

// Read the mixed collection through the common contract.
await foreach (var person in people.QueryManyAsync(_ => true))
{
    Console.WriteLine($"{person.GetType().Name}: {person.Name}");
}

// Or request only one concrete implementation.
await foreach (var employee in people.QueryManyAsync<Employee>(
    employee => employee.Department == "Engineering"))
{
    Console.WriteLine(employee.Name);
}

await foreach (var person in people.SearchManyAsync(
    person => person.Age >= 18 &&
              person.Name.StartsWith("Ja", StringComparison.Ordinal)))
{
    Console.WriteLine(person.Name);
}
```

The application service sees `IPerson` and `IRepository<IPerson>`. Only the composition root selects the file-backed implementation. See [Storing Multiple Concrete Types](documentation/polymorphic-storage.md) for the full polymorphic model.

See [Getting Started](documentation/getting-started.md) for the complete `PersonOptions` class and query examples.

## Implemented Features

- File-per-object storage in flat, versioned directories.
- Single- and multiple-object logical cardinality.
- GUID v7, sequential, and custom storage identity factories.
- Single and concurrent bulk saves, delete, existence, streaming, first, and single-result APIs.
- Polymorphic concrete type recovery.
- Exact, range, ordinal prefix, ordinal suffix, and literal ordinal contains indexes; encrypted fields are never persisted into them.
- Cost-aware `AND` intersections, safe `OR` unions, bounded ranges, residual predicates, and query-plan diagnostics.
- File change watching and in-process added/stored/removed/deleted notifications.
- Revision-checked writes and stale-write rejection, with legacy abandoned-proposal recovery.
- Performance and Durable file-write modes.
- Eager strict, eager tolerant, and lazy runtime migrations.
- MessagePack `IDB6` envelopes with exact-match revision writes and backward reads for `IDB2`–`IDB5`.
- Frozen per-contract runtime-type allowlists enforced before object construction and serialization.
- Whole-envelope and selected-field AES-GCM encryption, with context-bound `IDBE` version 3 writes.
- Active and historical key handling, compare-before-replace rotation on read, and File-provider full-store rotation.
- Windows Credential Manager key storage.
- Roslyn diagnostics that protect immutable contract baselines.
- Pluggable SegmentCore append-only storage with atomic bulk transactions, grouped WAL durability, retained-WAL seek boundaries, full/delta checkpoints, automatic paged primary locators, conditional persisted secondary-index catalogs, bounded streaming/integrity verification, provider migration, quota-controlled online backup/restore, and interruption-safe background compaction.

## Preview.10 Changes

The next public preview adds subscription readiness, running contract-version reporting, provider-owned revision clocks, updated dependencies and native SegmentCore migration fixes. Encrypted properties are restored before conversion, and incorrect canonical source versions are rejected. See the [preview.10 release notes](documentation/preview10-release-notes.md) for API compatibility and upgrades from preview.8 or preview.9.

## Preview.9 Security Changes

Preview.9 adds context-bound encrypted writes, a 256 MiB default stored-object limit, default rejection of legacy non-`IDB` typeless payloads, frozen per-contract runtime-type allowlists, deterministic named encryption registration, and safeguards that keep encrypted fields out of persistent search indexes. Encrypted SegmentCore repositories now keep search indexes in memory and purge stale plaintext catalog artifacts.

SegmentCore backup and restore reject observed links and reparse points. Restore accepts only known canonical manifest paths and has configurable manifest, file-count, per-file, aggregate-byte, path-depth, duration, and free-space admission through `SegmentCoreRestoreOptions`; the default reserve is 64 MiB.

Encrypted stores from preview.8 or earlier need a controlled upgrade: retain a verified pre-upgrade backup, enable `AllowContextlessPayloadMigration`, and convert every encrypted payload as `IDBE` v3. The File provider supplies a full-store rotation operation; SegmentCore requires complete record enumeration/materialization followed by checkpoint and integrity verification. Reopen in strict mode and enumerate again before disabling the migration window. Preview.8 cannot read v3 ciphertext. The detailed [preview.9 release notes](documentation/preview9-release-notes.md) include rollback and migration guidance.

## Mobile And Apple Platforms

The `InterfaceDB` and `InterfaceDB.Storage.SegmentCore` packages can be consumed by .NET 10
Android, iOS, and Mac Catalyst applications, as well as ordinary .NET 10 macOS processes. Android
has passed a trimmed Release/Mono AOT emulator qualification covering file-provider and SegmentCore
save, checkpoint, reopen, and indexed lookup. Apple target-framework consumers compile cleanly;
final iPhone and Mac runtime durability qualification still requires a Mac with Xcode. See the
[SegmentCore platform guidance](documentation/segment-core-storage-provider.md#android-support)
before selecting a mobile data root or application-lifecycle policy.

## Current Performance Snapshot

On the repository's Windows/NVMe development machine, the current 1,000-object quick gate measured one independent Durable write per object at a 5.574 ms median. The equivalent SQLite FULL autocommit measurements were 1.927 ms through `Microsoft.Data.Sqlite` and 1.936 ms through Dapper. The optimized file protocol is 2.74 times faster than InterfaceDB's earlier 15.299 ms Durable baseline while preserving all forced-process recovery and cross-process tests.

These are local diagnostic measurements, not universal rankings. The production-gate query paths return complete objects for every engine, verify count and checksum, and include mapping from the storage API into the object model; SQL scalar reads are not treated as equivalent to InterfaceDB object retrieval. The curated file-provider query rows use `StrongObject` and therefore describe a hot InterfaceDB object cache. `MetadataOnly`, with a fresh managed object created for every result, is the canonical mode for storage-to-object comparisons; a true cold-media claim additionally requires a controlled cold-cache run. Raw SegmentCore payload and provider-open measurements are reported separately and are not object-query results.

See [Current Performance Results](documentation/performance-results.md) and [Benchmark Methodology](documentation/production-benchmark-gate.md).

**SegmentCore is now integrated as a pluggable InterfaceDB provider.** It preserves `IRepository<TContract>`, encryption, polymorphism, and the existing exact/range/prefix/suffix/contains query layer while adding atomic `SaveManyAsync`, concurrent WAL group commit, mutation-WAL recovery, dirty-stripe full/delta checkpoints, no-op checkpoint reuse, retained-WAL seek acceleration, automatic paged primary locators, plaintext-repository search-index catalogs, parallel reopen, bounded streaming/integrity verification, explicit migration, admitted online backup/restore, automatic maintenance, and bounded copy-forward compaction. Encrypted repositories rebuild indexes in memory and remove persistent catalog artifacts.

Storage tuning is formalized through provider-neutral `StoragePolicy` hardware profiles
(`Portable`, `LowMemory`, `HighThroughput`, `WindowsNvme`, or `Custom`) and complete,
inspectable `SegmentCoreRepositoryOptions`. Profiles provide reproducible starting points while
explicit provider options retain control over every stripe, queue, durability, read, checkpoint,
and compaction setting.

In the latest maintained 5,000-object, five-iteration run, portable explicit-flush mutation WAL was SQLite-class and reached 166,914 records/s at batch 1,000, 1.21x SQLite before maintenance. Qualified Windows write-through reached 303,783 records/s at batch 1,000 and 4,368 independent durable writes/s, respectively 2.22x and 14.54x SQLite; it remained ahead after the symmetric maintenance checkpoint at every batch. Bounded compaction limited the longest observed exclusive slice to 14.88 ms under explicit flush and 8.79 ms under write-through. See [SegmentCore Storage Provider](documentation/segment-core-storage-provider.md) for setup, operations, results, and explicit boundaries.

A separate 10,000-object production pilot now exercises the public repository stack and every
persisted index family across five mutation cycles, checkpoint, compaction, verified backup,
reopen, restore, and catalog rebuild. It finished with 10,500 fully compared objects and no integrity
or query mismatch. SegmentCore also exposes listener-disabled-by-default `System.Diagnostics.Metrics`
for open stages, maintenance outcomes/latency, and storage sizes.

The latest one-million-record storage-core scale A/B passed in both modes. Resident locators reopened in 927.2 ms and allocated 139.9 MiB; paged locators reopened in 42.6 ms and allocated 25.2 MiB—a 21.8x reopen improvement and 82.0% allocation reduction. Paging costs more checkpoint time and about 40% sampled raw-payload point-read throughput, so the default switches automatically at 100,000 live entries. These measurements do not include repository object materialization and are local engineering measurements, not universal rankings.

SegmentCore is not presented as a general SQLite replacement: SQLite has the lower measured provider-open cost, maintenance can dominate very large batches at deliberately short checkpoint intervals, and SegmentCore does not implement SQL joins, relational constraints, multiple writer processes, or cross-store transactions. Provider-open timings stop before first-result object materialization and must not be read as end-to-end query latency. The five-minute Windows raw-store mixed soak, current 30-second/12.6-million-operation raw-store rerun, ENOSPC-equivalent recovery, all 93 Linux-container SegmentCore tests, 20 direct Linux InterfaceDB tests, 49 Linux integration scenarios, provider migration, online backup/restore, compatibility enforcement, package upgrade, and a 35.65 GB raw-payload larger-than-memory gate pass. The full virtualized-Linux strict-durability benchmark was 5.4% faster than SQLite at p50; the desired 15% lead remains an advisory optimization target, not a release blocker. Actual physical power-cut evidence remains outstanding and gates stable `1.0.0`; bare-metal Linux/NVMe and cold-media measurements are lower-priority hardware-specific characterization.

## Documentation

- [Documentation Index](documentation/README.md)
- [Why InterfaceDB](documentation/why-interfacedb.md)
- [Getting Started](documentation/getting-started.md)
- [Storing Multiple Concrete Types](documentation/polymorphic-storage.md)
- [Configuration](documentation/configuration.md)
- [Feature Guide](documentation/features.md)
- [Architecture](documentation/architecture.md)
- [Security And Encryption](documentation/security.md)
- [Contract Versioning And Migrations](documentation/migrations.md)
- [Storage, Indexing, And Performance](documentation/storage-indexing-performance.md)
- [Query Planning](documentation/query-planning.md)
- [Search Index Strategies](documentation/search-index-strategies.md)
- [Current Performance Results](documentation/performance-results.md)
- [Production Benchmark And Stress Gate](documentation/production-benchmark-gate.md)
- [SegmentCore Storage Provider](documentation/segment-core-storage-provider.md)
- [SegmentCore Transactions And Performance](documentation/segment-core-transactions.md)
- [Preview.10 Release Notes](documentation/preview10-release-notes.md)
- [Preview.9 Release Notes](documentation/preview9-release-notes.md)
- [Roadmap](documentation/roadmaps.md)

## Build And Verify

Prerequisite: .NET SDK 10.0.401 or a compatible newer feature band selected by `global.json`. The contract analyzers require Roslyn 5.9; older compiler hosts can skip them with `CS9057`. They run during Debug and Release compilation and add no runtime database work.

```powershell
dotnet restore InterfaceDB.sln
dotnet build InterfaceDB.sln -c Release --no-restore -warnaserror:CS9057
dotnet test InterfaceDb.Tests\InterfaceDb.Tests.csproj -c Release --no-build --no-restore
dotnet test Idb.Analyzers.Tests\Idb.Analyzers.Tests.csproj -c Release --no-build --no-restore
dotnet test InterfaceDB.Storage.SegmentCore.Tests\InterfaceDB.Storage.SegmentCore.Tests.csproj -c Release --no-build --no-restore
dotnet run -c Release --no-build --no-restore --project InterfaceDb.IntegrationTests
dotnet run -c Release --no-build --no-restore --project StorageBenchmarks.Transactions -- --segment-core-production-pilot --quick
```

Run the example:

```powershell
dotnet run -c Release --project InterfaceDb.ConsoleApp
```

Run the canonical fresh managed object-materialization quick gate:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache metadata
```

Generated traces, BenchmarkDotNet artifacts, test data, and gate reports are intentionally ignored. Curated, reproducible results belong in `documentation/performance-results.md`.

## Project Status

InterfaceDB and the opt-in SegmentCore provider were published to NuGet as `1.0.0-preview.10` on 12 October 2026. The release workflow and Windows/Linux CI passed, and a clean consumer verified both published packages; see the [release validation](documentation/preview10-validation.md). Preview.10 includes the preview.9 runtime security hardening and `IDBE` ciphertext-format transition, plus the fixes above. Upgrades from the previous published preview.8 require the documented migration steps. APIs and storage formats can still change before stable 1.0.0. Actual acknowledged-write physical power-cut evidence, platform-specific handle-pinned filesystem hardening, Windows/Linux adversarial-filesystem qualification, and Apple-device runtime qualification remain outstanding. See the [preview.9 release notes](documentation/preview9-release-notes.md) for migration and rollback requirements.
