# Storage, Indexing, And Performance

InterfaceDB is designed so the repository and envelope model can run on more than one storage engine. The current storage engine is file-backed and object-oriented.

## Current File Storage

Current layout:

```text
Data/
  1.0.0/
    <key>.mpk
```

Each object envelope is one payload file. This keeps the model simple and makes local object writes independent across files.

Strengths:

- easy local storage;
- simple inspection and cleanup;
- independent per-object writes;
- good fit for desktop/mobile/Unity/local-first apps;
- natural file-system watcher integration;
- good isolation for cross-process writes to different objects.

Costs:

- broad predicate scans need many file reads unless objects are cached or indexed;
- OS file metadata operations matter;
- many small files behave differently from a single database file;
- SQL-style range and join behavior requires secondary indexes or a different provider.

## Envelope Catalog

The envelope cache is always maintained as the metadata catalog:

```text
logical key -> physical key -> envelope metadata
```

It is not optional. It prevents normal repository operations from needing to scan the data directory for known identities.

Object instances are separate. They are retained only when configured through `[StorageCached]`, `Envelope.CacheObjectInstance`, or policy.

## Equality Lookup

Storage-key equality is the envelope catalog's job.

For `StorageObjectCardinality.Single`, a logical key has one physical object.

For `Multiple`, a logical key can have more than one physical object:

```text
john-smith -> john-smith-0
john-smith -> john-smith-1
```

Equality lookup is effectively:

```text
O(1 + k)
```

where `k` is the number of physical objects in the logical group.

## Search Indexes

Indexes are above the envelope catalog. They are used for property predicates:

- `Age >= 18`
- `Role == "Student"`
- `Phone.StartsWith("+27", StringComparison.Ordinal)`
- `Email.EndsWith("@example.com", StringComparison.Ordinal)`
- `Name.Contains("ann")`

The first index manager supports:

- exact equality;
- comparable range;
- string prefix;
- reversed string suffix;
- adaptive-gram literal substring containment;
- cost-aware `AND` candidate selection and conditional intersection;
- `OR` union.

Index results store physical envelope keys, not object copies. The repository then materializes exact envelopes.

The planner uses live per-path cardinality estimates. It begins with the least
expensive candidate source and adds an intersection only when the projected
reduction pays for building and intersecting another key set. Object-load cost
is weighted more heavily in metadata-only mode than in strong-object mode.
Unsupported and negative terms can remain residual filters behind a positive
index anchor. An `OR` falls back to a scan if any branch cannot safely produce
candidates.

## Why File Storage Can Still Compete

SQL Server is a highly optimized single-engine relational system. InterfaceDB's current file provider is doing different work:

- creates storage envelopes;
- stores concrete implementation metadata;
- maintains an envelope catalog;
- may update search indexes;
- supports migrations;
- supports file watcher reconciliation;
- supports whole-envelope and field-level encryption;
- serializes object graphs;
- coordinates cross-process writes.

The current object-materialization gate shows competitive `StrongObject` hot-cache range, broad-prefix, and broad-conjunction behavior on its development workload. SQLite remains substantially faster for selective exact/suffix queries, provider startup, and independently durable writes. Every timed query returns complete objects, but InterfaceDB's startup index rebuild deserializes the records and populates its strong cache before query timing while SQLite maps new objects from rows. Use `MetadataOnly` for the canonical fresh managed object-materialization comparison. The useful conclusion is that the object repository can compete on matching query shapes—not that files beat SQL universally.

## Performance Direction

The goal is not to rule out beating SQL. The goal is to compare honestly by storage pattern.

Current provider:

- many independent object files;
- in-memory envelope catalog;
- opt-in in-memory indexes;
- selective object caching.

Future providers:

- memory-backed repository for fastest in-process workloads;
- append-only journal with replay, checkpointing, and compaction;
- single-file database engine with page/object pointers;
- memory-mapped storage catalog;
- SQL-backed envelope provider;
- document database envelope provider;
- object database provider.

## Selective Object Caching

Object caching can improve read-heavy scenarios. InterfaceDB supports three repository object-cache modes:

- `MetadataOnly`: retain envelope metadata only; each query result is freshly read and deserialized, making this the canonical storage-to-object benchmark mode.
- `WeakObject`: retain a `WeakReference<TContract>` so live objects can be reused without InterfaceDB keeping them alive.
- `StrongObject`: retain the object instance strongly until cache update/removal; benchmark results in this mode describe a hot InterfaceDB object cache.

Example:

```csharp
[StorageCached]
public sealed class Employee : IPerson
{
}
```

If employees are marked as cached, the repository can skip file reads for those instances and return the retained object. Weak cache mode provides a middle ground: if the application still holds the object, InterfaceDB can reuse it; if the application releases it, the .NET GC can collect it and InterfaceDB will fall back to storage on the next read.

Future storage engines can use object pointers and page/catalog metadata to move the read head past cached objects and avoid unnecessary object payload reads.

This is one reason the current file provider should not be treated as the final performance ceiling.

## Benchmark Commands

Run the canonical fresh managed object-materialization quick gate first:

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache metadata
```

See [Current Performance Results](performance-results.md) before interpreting isolated benchmark output.

The older 50k BenchmarkDotNet suite remains useful for targeted microanalysis:

```powershell
$env:INTERFACEDB_BENCH_PERSON_COUNT="50000"
$env:INTERFACEDB_BENCH_MAX_CONCURRENCY="16"
$env:INTERFACEDB_BENCH_BULK_BATCH_SIZE="16384"
$env:INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER="true"

dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --run-all
```

The full suite includes metadata-only, weak object-cache, and strong object-cache InterfaceDB query benchmarks. The metadata-only tests repeatedly materialize objects from file storage and are canonical when comparing fresh managed objects. Strong-object results are separately useful for the normal hot indexed-query profile. Neither mode is automatically a physical cold-media test because the operating-system page cache may remain warm.

InterfaceDB-only file tests:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --filter *FileSystem*
```

Validate and benchmark the isolated query planner and substring index:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-validate
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-quick
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --planner-benchmark --filter "*" --job short
```

On the recorded 100,000-item .NET 10 ShortRun, adaptive-gram `Contains` took
44.36 microseconds versus 3,173.83 microseconds for a full ordinal scan. For a
broad in-memory conjunction, always intersecting took 5,480.59 microseconds
versus 2,426.34 microseconds for the best single index plus residual filtering;
the cost-aware strong-cache plan correctly retained the single path at
2,503.28 microseconds. These isolated numbers measure index/set work, not file
materialization, which is why the planner applies a higher avoided-load value
to metadata-only repositories.

## Profiling

Use the direct profile targets when investigating hot paths:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-store
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-indexed-store
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-selective-indexed-query
```

See [Performance Profiling](performance-profiling.md).

## Production Gate

Index microbenchmarks intentionally exclude persistence and object materialization.
Before drawing product conclusions, run the production gate against embedded SQLite
and, when configured, SQL Server. Timed queries return and validate complete objects;
`MetadataOnly` is the canonical fresh-object profile and `StrongObject` is an explicitly
hot InterfaceDB cache profile. The separate startup timing includes InterfaceDB object
deserialization and index rebuilding but stops before a separately issued query; the
relational counterpart only opens a connection. The gate also measures durability profiles, mixed
concurrent mutations, final data integrity, and terminated-writer recovery.

```powershell
.\Scripts\Test-ProductionGate.ps1 -Quick -Cache metadata
```

See [Production Benchmark And Stress Gate](production-benchmark-gate.md) for the
full soak matrix and interpretation constraints.
