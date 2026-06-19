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
- `Phone.StartsWith("+27")`

The first index manager supports:

- exact equality;
- comparable range;
- string prefix;
- `AND` intersection;
- `OR` union.

Index results store physical envelope keys, not object copies. The repository then materializes exact envelopes.

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

The current benchmark already shows strong selective indexed reads. That does not mean the file provider beats SQL in every scenario, and it does not mean SQL comparisons are invalid. It means the object database model has real potential when the query shape matches the index model.

## Performance Direction

The goal is not to rule out beating SQL. The goal is to compare honestly by storage pattern.

Current provider:

- many independent object files;
- in-memory envelope catalog;
- opt-in in-memory indexes;
- selective object caching.

Future providers:

- memory-backed repository for fastest in-process workloads;
- single-file database engine with page/object pointers;
- memory-mapped storage catalog;
- SQL-backed envelope provider;
- document database envelope provider;
- object database provider.

## Selective Object Caching

Object caching can improve read-heavy scenarios.

Example:

```csharp
[StorageCached]
public sealed class Employee : IPerson
{
}
```

If employees are marked as cached, the repository can skip file reads for those instances and return the retained object. Future storage engines can use object pointers and page/catalog metadata to move the read head past cached objects and avoid unnecessary object payload reads.

This is one reason the current file provider should not be treated as the final performance ceiling.

## Benchmark Command

Full 50k overnight benchmark:

```powershell
$env:INTERFACEDB_BENCH_PERSON_COUNT="50000"
$env:INTERFACEDB_BENCH_MAX_CONCURRENCY="16"
$env:INTERFACEDB_BENCH_BULK_BATCH_SIZE="16384"
$env:INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER="true"

dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --run-all
```

InterfaceDB-only file tests:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --filter *FileSystem*
```

## Profiling

Use the direct profile targets when investigating hot paths:

```powershell
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-store
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-indexed-store
dotnet run -c Release --project .\StorageBenchmarks.Transactions -- --profile filesystem-selective-indexed-query
```

See [Performance Profiling](performance-profiling.md).
