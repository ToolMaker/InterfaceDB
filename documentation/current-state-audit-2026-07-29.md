# InterfaceDB Current-State Audit

Date: 2026-07-29

This audit covers the current `master` working tree in `C:\Code\C#\InterfaceDB`
and the candidate indexer solution in
`C:\Code\C#\Github\AgileCoding.Library.Indexers`.

## Executive Summary

InterfaceDB is a substantial working prototype of an interface-first, file-backed
object database. Its primary storage path, envelope lifecycle, migrations,
encryption, file watching, cardinality handling, and basic expression indexing
are implemented and covered by a broad executable integration harness.

The current working tree builds cleanly and all existing tests pass. It is not
release-ready yet because there are correctness failures outside the present
test suite, the cache-mode work is uncommitted and API-breaking, benchmark
evidence is inconsistent with the current source, and the library lacks the
normal CI, packaging, compatibility, and source-control controls expected for a
reusable database package.

The most urgent defect is concurrent index maintenance. A public-API stress
probe wrote 4,000 records successfully. A predicate scan and the file system
both contained all 4,000 records, while the indexed query returned only one.
The index mutation exception was swallowed and the repository silently removed
its internal index subscription.

`AgileCoding.Library.Indexers` is a good candidate for replacing the low-level
range and prefix structures. It is thread-safe, packageable, and well tested,
but it is not a drop-in query engine. InterfaceDB still needs its expression
planner, property discovery, physical-key mapping, update snapshots, and a
manager-level consistency boundary.

## Repository State

- Branch: `master`
- HEAD: `552edce34fd44b45c617204fa4b4ec0bcba52062`
- Last commit: `2026-06-19`, `more documentation`
- Git remote: none configured
- Other branch: `feature/v1.2`, 57 commits behind `master`
- Solution projects: 11
- Main target: .NET 10
- Analyzer target: `netstandard2.0`
- SDK used for verification: .NET SDK `10.0.301`

The working tree already contained 21 modified tracked files and one untracked
source file before this report was added. The existing changes contain 464
insertions and 193 deletions, primarily for metadata-only, weak, and strong
object-cache modes.

Important repository-level gaps:

- no CI workflow;
- no configured remote;
- no license;
- no `global.json` to pin the SDK;
- no shared `Directory.Build.props` or enforced analyzer policy;
- generated benchmark and trace artifacts are tracked;
- no release/versioning workflow.

## Implemented Product Surface

The following capabilities are present in code and exercised to varying depths:

- interface-first `IRepository<TContract>`;
- polymorphic concrete objects behind a stored interface;
- file-backed, versioned envelope storage;
- metadata catalog and logical-to-physical key groups;
- single and multiple cardinality;
- GUID v7 and in-memory sequential identity factories;
- single and bulk save, delete, existence, streaming query, first, and
  single-cardinality query methods;
- exact, range, prefix, `AND`, and `OR` expression search;
- concrete-implementation-specific query overloads;
- metadata-only, weak, strong, per-type, and per-envelope object caching;
- performance and durable write modes;
- proposal files, sidecar locks, and last-operation-wins durable commits;
- file-system watcher reconciliation and rescan;
- eager and lazy migration chains;
- whole-envelope AES-GCM encryption and key rotation;
- field-level encryption through interface proxies;
- Windows Credential Manager key storage;
- contract baseline CLI and Roslyn analyzer;
- SQL Server, Dapper, EF Core, and InterfaceDB benchmark projects.

Only the file-system storage provider exists. Memory, single-file, SQL,
document, and other providers remain roadmap work.

## Verified Health

### Build

```text
dotnet build InterfaceDB.sln -c Release --no-incremental
Build succeeded.
0 Warning(s)
0 Error(s)
```

### Automated Tests

```text
Idb.Analyzers.Tests: 5 passed
InterfaceDb.Tests:    1 passed
```

The one `InterfaceDb.Tests` test launches the standalone integration executable.

### Integration Harness

The standalone harness reported 49 passing scenarios. Coverage includes:

- save/delete/recovery;
- single and multiple cardinality;
- storage-key groups;
- index selection and basic search behavior;
- repository cardinality APIs;
- bulk success and partial failure;
- cache modes;
- identity factories;
- watcher synchronization and rescan;
- keyed DI;
- cross-process update/delete coordination;
- migration chains and failure handling;
- whole-envelope and field encryption;
- key rotation and Windows Credential Manager.

### Dependencies and Packages

- NuGet audit found no known vulnerable packages.
- The main library packs successfully as `Idb.Libraries.1.0.0.nupkg`.
- The analyzer packs successfully as `Idb.Analyzers.1.0.0.nupkg`.
- Main dependencies are only patch releases behind, but test and Roslyn tooling
  have newer major versions available.

The main package still contains placeholder metadata:

```text
Authors: Idb.Libraries
Description: Package Description
Readme: missing
License: missing
Repository URL: missing
```

## Confirmed Correctness Defects

### P0: Concurrent Saves Corrupt or Disable Search Index Maintenance

`SearchIndexStore<TContract>` mutates ordinary dictionaries, sorted
dictionaries, and hash sets without synchronization. Concurrent repository
saves can enter index updates at the same time.

The repository notification publisher catches every subscriber exception and
disposes the subscription without logging or surfacing the failure. When the
internal search-index subscriber fails once, writes continue to succeed while
the index permanently stops receiving updates.

Reproduction through `IRepository<TContract>`:

```text
Save failures: 0
Indexed query count: 1 / 4000
Predicate scan count: 4000 / 4000
Payload file count: 4000 / 4000
```

Affected areas:

- `Implementation/Indexes/SearchIndexStore.cs`
- `Implementation/Indexes/SearchIndexManager.cs`
- `Implementation/Repositories/FileEnvelopeRepositoryBase.cs`

Required fix:

- synchronize index reads and writes;
- make multi-property updates consistent;
- never silently remove the internal index-maintenance subscription;
- add a concurrent save/search regression test that compares indexed results,
  scan results, and persisted file count.

### P1: Multiple-Cardinality Insert Fails After Deleting a Middle Suffix

The next physical suffix is calculated from the current group count. After
creating `key-0`, `key-1`, and `key-2`, deleting `key-1` leaves a count of two.
The next insert selects `key-2`, which already exists.

Verified result:

```text
Before insert: person-same-0, person-same-2
StorageObjectAlreadyExistsException for person-same-2.mpk
```

The exception message also incorrectly describes this multiple-cardinality
failure as a single-cardinality conflict.

Required fix:

- allocate from the highest occupied suffix plus one, or use a collision-safe
  retry/allocator;
- define cross-process allocation behavior for concurrent creates in the same
  logical group;
- test gaps, restarts, and two-process creates.

### P1: Async Deserializer Rejects Valid Partial Stream Reads

`BinaryObjectSerializer.DeserializeCoreAsync` assumes one `ReadAsync` call will
return all four header bytes. The `Stream` contract permits shorter reads even
when more data is available.

A stream intentionally returning one byte per read reproduced:

```text
System.InvalidOperationException: Unexpected end of stream.
```

Required fix:

- use `ReadExactlyAsync` for the header;
- use the equivalent exact-read logic in synchronous header reads;
- add chunked/non-seekable stream tests.

### P1: Synchronous Migration API Misuses `ValueTask`

`FileSystemMigrationStrategyBase.Migrate` calls
`MigrateFileAsync(...).GetAwaiter().GetResult()` directly. Analyzer rule
`CA2012` correctly flags this: an incomplete `ValueTask` is not guaranteed to
block correctly when its result is accessed this way.

Required fix:

- remove the synchronous migration surface, or bridge through
  `.AsTask().GetAwaiter().GetResult()` with a documented blocking contract;
- ensure the cancellation token is passed into the fallback MessagePack
  deserialization path.

### P1: Cache-Mode Work Breaks Public API Compatibility

The current uncommitted work:

- removes `StoragePolicy.KeepObjectInstanceInCacheOnWrite`;
- adds `StoragePolicy.ObjectCacheMode`;
- adds `ObjectInstanceWeakReference` to public
  `IObjectEnvelope<TContract>`.

This breaks consumers compiling against the old policy and third-party envelope
implementations. Package version remains `1.0.0`.

Required fix:

- decide whether this is pre-release API churn or a versioned breaking change;
- preserve an obsolete compatibility property where practical;
- avoid exposing cache implementation details on the public serialized
  envelope contract, or provide a default interface implementation.

## High-Priority Product and Operational Gaps

### Durability Defaults Are Not Clear

`StoragePolicy.Default` uses `StorageDurabilityMode.Performance`, which bypasses
proposal coordination and write-through durability. Documentation prominently
describes cross-process coordination without clearly stating that durable mode
must be selected.

Choose and document one of these positions:

- safe/durable by default with performance as explicit opt-in; or
- performance by default with a prominent crash/concurrency warning.

### Errors Are Deliberately Hidden

Repository subscriber exceptions and watcher callback exceptions are swallowed.
This makes index drift and reconciliation failures invisible.

Add structured logging, health state, and an explicit policy for external
subscriber failures. Internal maintenance subscribers must not be treated like
optional application callbacks.

### Benchmarks Do Not Describe the Current Source

`documentation/product-positioning.md` claims strong InterfaceDB read results
from a 2026-06-19 baseline. The modified `TestResults/results.txt` contains
radically slower metadata-only numbers, including approximately 406 seconds for
100 scans and 190 seconds for 100 broad indexed searches. The current benchmark
source has since been changed again to split strong, weak, and metadata-only
cache modes, but the generated report still uses the older method names and
does not contain those split results.

Do not use the current benchmark claims for external positioning. First:

- fix index correctness;
- commit the cache-mode design;
- rerun all cache modes from the current binary;
- record dataset size and configuration in every table;
- generate documentation from the benchmark artifact instead of copying values.

### Test Architecture Needs Refactoring

The 49-scenario harness is valuable, but it is a 3,215-line top-level
executable. MSTest sees only one wrapper test, reducing failure isolation,
filtering, coverage, and CI diagnostics.

Split the harness into focused MSTest fixtures while retaining true
multi-process tests. Add tests for:

- concurrent indexed saves and searches;
- multiple-cardinality suffix gaps;
- cross-process creates in one logical group;
- chunked and non-seekable streams;
- weak-reference collection followed by rehydration;
- synchronous migration behavior or API removal;
- index updates from watcher and migration events;
- cancellation during initialization and tolerant migration;
- Linux path and watcher behavior.

### Demo and Tooling Are Not Finished

- The demo migration `Up` and `Down` methods are generated TODO stubs that
  return success without mapping.
- `PersonOptions` still targets version `1.0.0` while a `1.0.1` contract and
  migration exist.
- The console app does not use its `appsettings.json`.
- `MigrationTool --help` prints usage but exits with code 1.
- Public names contain compatibility-hostile spelling errors such as
  `InterfaceRepositryDb`, `IEnvelopeDictionaryCaheManager`, and `Extentions`.
- `IIndex<TContract>` is an unfinished public interface with a TODO for its
  operational methods.

## Static Analysis

The normal build is clean because these rules are not enforced as build
warnings. `dotnet format analyzers --verify-no-changes --severity info` reports:

- 14 `CA2012` direct `ValueTask` result accesses;
- 4 `CA1068` public cancellation-token ordering issues;
- 1 `CA2016` dropped cancellation token;
- 1 `CA2208` incorrect `ArgumentNullException` parameter;
- additional API, namespace, P/Invoke, and test-style findings.

Most findings are polish, but the migration `CA2012` issue is behavioral.

## Candidate Indexer Assessment

Candidate:

```text
C:\Code\C#\Github\AgileCoding.Library.Indexers
```

The directory is not currently a Git repository and has no configured history
or remote.

### Verified State

- 4 package projects;
- 4 MSTest projects;
- Release build: zero warnings and zero errors;
- tests: 97 passed;
- NuGet audit: no known vulnerable packages;
- generated `.nupkg` and `.snupkg` files for all four packages;
- thread safety implemented with `ReaderWriterLockSlim`;
- concurrent read/write tests exist.

Package projects:

- `CharacterTreeIndex<TItem>` for exact and prefix string lookup;
- `RangeIndex<TKey,TItem>` for comparable ranges;
- `FilteredObjectIndex<TItem>` for key/type/scope/tag/metadata lookup;
- indexed dependency-injection activation.

### Fit for InterfaceDB

The best-fit packages are `CharacterTreeIndex` and `RangeIndex`.

Use:

- `RangeIndex<TKey, string>` for numeric, date, enum, GUID, and other
  comparable property ranges, storing physical envelope keys as items;
- `CharacterTreeIndex<string>` for string prefix lookup and optionally exact
  string lookup;
- a hash-based exact index for equality-only properties where a trie or sorted
  range index adds unnecessary overhead.

Do not use `FilteredObjectIndex` as the InterfaceDB expression engine. Its
query model targets runtime object resolution by key, type, scope, tags, and
metadata. InterfaceDB still needs its existing expression parser and candidate
planner.

### Why It Is Better Than the Current Structures

- It supplies internal synchronization and avoids the reproduced dictionary
  corruption.
- `RangeIndex` uses binary search over distinct keys instead of scanning the
  complete sorted dictionary for every range query.
- Snapshot methods are safe to enumerate after mutations.
- Indexes support explicit compaction and disposal.
- Tests cover overlays, updates, compaction, ordering, and concurrent use.

Tradeoffs:

- `CharacterTreeIndex` calculates broad prefix results by traversing the
  matching subtree, while the current trie stores candidate-key sets on every
  prefix node. The new design should use less memory but broad prefix searches
  may do more query-time traversal.
- Overlay tombstones retain item references until compaction.
- Per-index locks do not make a multi-property InterfaceDB update atomic.
- There are no comparative benchmarks yet.

### Required Work Before Adoption

1. Put the indexer solution under Git and add a reproducible package source or
   move the projects into a controlled repository.
2. Normalize package versions and add license, repository URL, CI, changelog,
   and release metadata.
3. Add an InterfaceDB-internal property-index adapter so generic
   `RangeIndex<TKey,string>` instances can be selected from runtime property
   metadata.
4. Retain InterfaceDB's expression planner, property discovery, old-value
   snapshots, and physical-key candidate sets.
5. Add a manager-level reader/writer consistency boundary around complete
   multi-property updates and candidate snapshot generation. Per-index locking
   alone can still expose partial updates and false negatives.
6. Define compaction thresholds and call `Compact` after initial rebuilds and
   bounded write batches.
7. Make index-maintenance failures observable and recoverable through rebuild.
8. Rerun correctness stress tests and current-vs-new allocation/latency
   benchmarks before replacing the existing implementation.

## Recommended Execution Order

### Stabilization

1. Fix concurrent index correctness and error visibility.
2. Fix multiple-cardinality suffix allocation.
3. Fix exact stream reads.
4. remove or repair synchronous migration.
5. finalize the cache-mode public API and commit the working tree.

### Verification

1. Convert critical integration scenarios to isolated MSTest cases.
2. Add the missing concurrency, stream, cardinality, GC, and cross-platform
   cases.
3. Enable selected analyzers as build warnings.
4. Add Windows and Linux CI.

### Indexer Adoption

1. Source-control and package the candidate indexer.
2. Build InterfaceDB property-index adapters.
3. Add manager-level atomicity and compaction.
4. Compare correctness, memory, rebuild time, write cost, exact lookup, range,
   narrow prefix, and broad prefix performance.

### Release Readiness

1. Decide supported target frameworks and platform matrix.
2. Add package metadata, readme, license, Source Link, deterministic builds,
   and semantic versioning.
3. Replace generated demo TODOs with a real end-to-end version migration.
4. Publish benchmark results generated from the exact release commit.
5. Configure a remote and protect the release branch with CI.
