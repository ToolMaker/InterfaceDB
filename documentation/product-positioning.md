# Why InterfaceDB Exists

InterfaceDB is an interface-first object database for applications that want local object persistence without giving up polymorphism, version-aware storage, or storage lifecycle awareness.

The current implementation is file-backed and intentionally simple. The goal is not to imitate SQL table storage. The goal is to store objects through their contract interface and recover the correct concrete object later.

## Core Advantages

### Interface-Level Polymorphism

Most common persistence tools need a known concrete type or a manually mapped table shape. InterfaceDB starts from the interface:

```csharp
IRepository<IPerson>
```

The stored object can still be a concrete subtype:

```csharp
Student
Employee
```

The envelope stores the metadata needed to recover the correct implementation behind the interface. That gives the application a stable contract while allowing multiple concrete storage types.

### Rich Envelope Metadata

Every stored object is wrapped in an envelope containing storage metadata such as:

- storage key;
- provider location;
- version tag;
- object state;
- concrete implementation metadata;
- object instance reference when retained in cache.

This is deliberate overhead. It is what lets InterfaceDB support interface contracts, migration, concrete type recovery, deletion state, and repeat saves without rediscovering the backing storage location.

### Encryption Extensibility

InterfaceDB has a storage encryption layer below the repository and above the physical write. The default encryptor is a no-op, but AES-GCM can be enabled without changing repository code or contract usage.

Because encryption is applied to the serialized envelope payload, the file-backed provider can protect both the object data and the envelope metadata stored inside the payload. SQL Server has encryption features at the database/platform level, and application code can encrypt values manually before using Dapper, but Dapper itself does not provide a repository-level object encryption pipeline. InterfaceDB owns that pipeline as part of the storage model.

### Envelope Cache As The Primary Catalog

InterfaceDB keeps an in-memory envelope catalog. This avoids normal query flows scanning the file system.

The file payload is durable storage. The envelope cache is the repository's known-object catalog. Secondary indexes can later sit beside this catalog, but the envelope cache is the minimum object index.

Write behavior now always caches the envelope metadata. The only memory tuning switch is whether to retain the full object instance after write or keep a lightweight envelope skeleton.

### Storage Lifecycle Notifications

InterfaceDB has built-in repository notifications:

- `Stored` when an object is saved or changed;
- `Added` when a new object is persisted;
- `Deleted` when an object is deleted;
- `Removed` when an object is removed or renamed away by storage synchronization.

These notifications are in-process repository events. They allow application code to react to object lifecycle changes without polling.

Relational databases can provide change notifications in some environments, but those mechanisms are usually table/row oriented and provider-specific. Dapper itself does not provide object lifecycle notifications; it is a data mapper over ADO.NET.

### External Storage Change Awareness

The file-backed repository can watch the storage location for external committed changes:

- created payloads;
- changed payloads;
- deleted payloads;
- renamed payloads;
- watcher errors and recovery rescans.

This makes multiple repository instances pointed at the same storage root aware of each other. A local write suppresses its own watcher echo, while external changes can update or remove envelope cache entries.

In plain terms: one process can write or delete an object and another process can observe the file-system change and reconcile its cache.

### Provider-Neutral Direction

The envelope location is an opaque provider locator. The file provider treats it as a path, but future providers can map it to SQL keys, document IDs, object-store keys, event-stream offsets, or in-memory dictionary keys.

The exception model is also provider-neutral, so file, SQL, document, event, and memory providers can report comparable storage failures while keeping provider-specific details as inner exceptions.

## Current Performance Baseline

Latest full stack benchmark provided on 2026-06-19.

Read benchmarks use 100 queries over 50k persons and materialize matching objects for SQL Server and Dapper. InterfaceDB predicate scan is the non-indexed fallback path. InterfaceDB indexed search uses the opt-in in-memory search index layer.

| Scenario | Technology | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------- |----------- |-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Broad predicate/materialized query | InterfaceDB predicate scan | 1,069.74 ms | 15.977 ms | 14.945 ms | - | - | - | 38.20 MB |
| Broad predicate/materialized query | SQL Server indexed materialized | 912.15 ms | 11.702 ms | 10.946 ms | 4000.0000 | 3000.0000 | 2000.0000 | 265.33 MB |
| Broad predicate/materialized query | Dapper indexed materialized | 1,016.84 ms | 19.263 ms | 18.019 ms | 4000.0000 | 3000.0000 | 2000.0000 | 349.28 MB |
| Broad indexed search | InterfaceDB indexed search | 841.55 ms | 14.988 ms | 13.287 ms | 1000.0000 | - | - | 82.26 MB |
| Selective indexed search | InterfaceDB indexed search | 63.19 ms | 1.167 ms | 1.433 ms | 1000.0000 | 428.5714 | - | 8.41 MB |
| Selective indexed search | SQL Server indexed materialized | 163.48 ms | 2.017 ms | 1.887 ms | 1250.0000 | 250.0000 | - | 10.78 MB |
| Selective indexed search | Dapper indexed materialized | 161.03 ms | 1.180 ms | 1.104 ms | 1750.0000 | 500.0000 | - | 14.14 MB |

Write benchmarks store 50k persons. SQL Server and Dapper use object-by-object insert loops in a transaction. `SqlBulkCopy` is included as an upper-bound bulk-ingest reference, not as the object-by-object comparison.

| Scenario | Technology | Mean | Error | StdDev | Gen0 | Gen1 | Gen2 | Allocated |
|--------- |----------- |-----:|------:|-------:|-----:|-----:|-----:|----------:|
| Object transaction write | InterfaceDB baseline | 5,491.32 ms | 107.020 ms | 184.605 ms | 4000.0000 | - | - | 227.36 MB |
| Object transaction write | InterfaceDB indexed | 5,812.01 ms | 116.152 ms | 180.835 ms | 7000.0000 | 2000.0000 | - | 343.21 MB |
| Object transaction write | SQL Server | 3,728.50 ms | 73.705 ms | 169.350 ms | 16000.0000 | - | - | 159.84 MB |
| Object transaction write | SQL Server indexed | 4,159.31 ms | 82.799 ms | 215.206 ms | 16000.0000 | - | - | 159.84 MB |
| Object transaction write | Dapper | 3,912.45 ms | 95.593 ms | 281.857 ms | 47000.0000 | - | - | 404.74 MB |
| Object transaction write | Dapper indexed | 4,377.70 ms | 86.729 ms | 118.715 ms | 47000.0000 | - | - | 404.74 MB |
| Bulk ingest reference | SQL Server SqlBulkCopy | 177.05 ms | 0.704 ms | 0.588 ms | - | - | - | 23.11 MB |

Current file benchmark settings:

```text
INTERFACEDB_BENCH_MAX_CONCURRENCY=16
INTERFACEDB_BENCH_BULK_BATCH_SIZE=16384
INTERFACEDB_BENCH_SINGLE_WRITE_BUFFER=true
```

These results are encouraging because InterfaceDB is doing more than scalar row insertion:

- interface-based object persistence;
- concrete implementation recovery metadata;
- envelope creation and lifecycle;
- version-aware storage;
- file payload writes;
- envelope catalog updates.

Despite that richer storage model, write allocation is significantly lower than the current Dapper transaction benchmark.

The query results show why indexing matters. The non-indexed InterfaceDB predicate scan is still slower than SQL Server for broad reads, but the indexed InterfaceDB path now beats the SQL Server and Dapper materialized-object benchmarks in both broad and selective indexed reads. The selective indexed case is the clearest win because the index can return a small candidate set before object filtering.

The remaining performance gap is on writes. InterfaceDB indexed writes currently add about 321 ms and 116 MB over the non-indexed InterfaceDB write path for 50k objects. The next optimization work should focus on reducing index update allocation and file-write overhead.

## Benchmark Interpretation

SQL Server raw ADO.NET is a useful lower-level storage baseline, but it is not a full apples-to-apples comparison. The current SQL benchmark reads properties from pre-created `IPerson` instances and writes scalar columns. It does not store the interface object model, envelope metadata, concrete implementation metadata, or migration metadata.

Dapper is the closer mainstream comparison because it maps objects to database rows. Even then, the current Dapper benchmark uses a flat reusable object and scalar columns. It does not reproduce InterfaceDB's interface-level polymorphic storage unless additional discriminator and subtype mapping logic is added.

Entity Framework is a valuable future benchmark because it brings a richer object model and change tracking. It should be added as a separate comparison, not as a replacement for the raw SQL and Dapper baselines.

## Practical Positioning

InterfaceDB is strongest where the application wants:

- interface-first storage contracts;
- local desktop/mobile object persistence;
- polymorphic storage without hand-written table mapping;
- versioned objects and migrations;
- low memory overhead;
- application-level lifecycle notifications;
- external storage change awareness;
- future ability to swap file storage for other providers.

The current file-backed implementation is not yet a finished enterprise database. Indexing, true storage-level bulk writes, and additional providers are still roadmap work. But the current baseline is already close enough to common database write paths to justify further optimization.
